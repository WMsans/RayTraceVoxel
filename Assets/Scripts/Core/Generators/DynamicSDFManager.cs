using System; 
using System.Collections.Generic; 
using UnityEngine; 
using VoxelEngine.Core.Data; 
using System.Runtime.InteropServices;

namespace VoxelEngine.Core.Generators
{
    public class DynamicSDFManager : MonoSingleton<DynamicSDFManager>
    {
        [Header("Configuration")] 
        [Tooltip("Extra margin added to the root bounds to prevent frequent rebuilding of the coordinate space.")] 
        public float globalBoundsMargin = 100.0f; 
        public bool rebuildEveryFrame = true; 
        public bool drawDebugGizmos = false;
        
        // --- Data ---
        private List<SDFObject> _objects = new List<SDFObject>();
        private List<Bounds> _dirtyRegions = new List<Bounds>();
        private List<Bounds> _debugDirtyRegions = new List<Bounds>();
        private bool _isDirty = false;

        // BVH Data
        private struct MortonEntry : IComparable<MortonEntry>
        {
            public uint code;
            public int originalIndex;

            public int CompareTo(MortonEntry other)
            {
                if (code < other.code) return -1;
                if (code > other.code) return 1;
                return 0;
            }
        }
        
        private MortonEntry[] _mortonKeys;
        private LBVHNode[] _bvhNodes;
        private int[] _sortedObjectIndices;
        private int _nodeCount = 0;

        // --- GPU Buffers ---
        public GraphicsBuffer SDFObjectBuffer { get; private set; }
        public GraphicsBuffer LBVHNodeBuffer { get; private set; }
        public GraphicsBuffer ObjectIndexBuffer { get; private set; }

        public int ObjectCount => _objects.Count;
        public bool IsReady => _objects.Count > 0 && LBVHNodeBuffer != null && LBVHNodeBuffer.IsValid();

        // --- Public API ---

        public void RegisterObject(SDFObject obj)
        {
            _objects.Add(obj);
            AddDirtyRegion(obj);
            _isDirty = true;
            // Immediate rebuild not strictly necessary if handled in Update, but good for responsiveness
            if (!rebuildEveryFrame) RebuildBVH(); 
        }

        public void UpdateObject(int index, SDFObject obj)
        {
            if (index >= 0 && index < _objects.Count)
            {
                SDFObject oldObj = _objects[index];
                if (IsSame(oldObj, obj)) return;

                AddDirtyRegion(oldObj); // Dirty old position
                _objects[index] = obj;
                AddDirtyRegion(obj);    // Dirty new position
                _isDirty = true;
            }
        }

        /// <summary>
        /// Removes the object at the specified index.
        /// </summary>
        public void RemoveObjectAt(int index)
        {
            if (index >= 0 && index < _objects.Count)
            {
                // Mark the region occupied by the object as dirty so chunks can regenerate (and disappear the object)
                AddDirtyRegion(_objects[index]);
                
                _objects.RemoveAt(index);
                _isDirty = true;
                
                // If list is empty, we should clear buffers
                if (_objects.Count == 0) ReleaseBuffers();
            }
        }

        /// <summary>
        /// Finds the index of the closest SDF object to the given point within a radius.
        /// </summary>
        public int FindClosestObject(Vector3 position, float radius)
        {
            int bestIndex = -1;
            float minSqrDst = radius * radius;

            for (int i = 0; i < _objects.Count; i++)
            {
                // Simple distance check to object center
                float sqrDst = Vector3.SqrMagnitude(_objects[i].position - position);
                if (sqrDst < minSqrDst)
                {
                    minSqrDst = sqrDst;
                    bestIndex = i;
                }
            }
            return bestIndex;
        }

        public void ClearObjects()
        {
            foreach (var obj in _objects) AddDirtyRegion(obj);
            _objects.Clear();
            _isDirty = true;
            ReleaseBuffers();
        }

        public List<Bounds> GetAndClearDirtyRegions()
        {
            if (_dirtyRegions.Count == 0) return null;
            _debugDirtyRegions.Clear();
            _debugDirtyRegions.AddRange(_dirtyRegions);
            var list = new List<Bounds>(_dirtyRegions);
            _dirtyRegions.Clear();
            return list;
        }

        // --- Internals ---

        private bool IsSame(SDFObject a, SDFObject b)
        {
            if (a.position != b.position) return false;
            if (a.rotation != b.rotation) return false;
            if (a.scale != b.scale) return false;
            if (a.type != b.type || a.operation != b.operation) return false;
            if (Mathf.Abs(a.blendFactor - b.blendFactor) > 0.0001f) return false;
            return true;
        }

        private void AddDirtyRegion(SDFObject obj)
        {
            Vector3 center = (obj.boundsMin + obj.boundsMax) * 0.5f;
            Vector3 size = obj.boundsMax - obj.boundsMin;
            size += Vector3.one * 2.0f; // Padding
            _dirtyRegions.Add(new Bounds(center, size));
        }

        private void OnDisable() => ReleaseBuffers();

        private void Update()
        {
            if (rebuildEveryFrame && _isDirty)
            {
                RebuildBVH();
                _isDirty = false;
            }
        }

        // --- BVH Logic (Standard) ---
        public void RebuildBVH()
        {
            int numObjects = _objects.Count;
            if (numObjects == 0) return;

            // Resize arrays if needed
            if (_mortonKeys == null || _mortonKeys.Length < numObjects) _mortonKeys = new MortonEntry[numObjects];
            if (_sortedObjectIndices == null || _sortedObjectIndices.Length < numObjects) _sortedObjectIndices = new int[numObjects];
            if (_bvhNodes == null || _bvhNodes.Length < numObjects * 2) _bvhNodes = new LBVHNode[numObjects * 2];

            // 1. Compute Global Bounds
            Vector3 globalMin = Vector3.one * float.MaxValue;
            Vector3 globalMax = Vector3.one * float.MinValue;
            for (int i = 0; i < numObjects; i++)
            {
                globalMin = Vector3.Min(globalMin, _objects[i].boundsMin);
                globalMax = Vector3.Max(globalMax, _objects[i].boundsMax);
            }
            globalMin -= Vector3.one * globalBoundsMargin;
            globalMax += Vector3.one * globalBoundsMargin;
            Vector3 range = globalMax - globalMin;
            range = Vector3.Max(range, Vector3.one * 0.001f);

            // 2. Compute Morton Codes
            for (int i = 0; i < numObjects; i++)
            {
                Vector3 center = (_objects[i].boundsMin + _objects[i].boundsMax) * 0.5f;
                Vector3 n = (center - globalMin);
                n.x /= range.x; n.y /= range.y; n.z /= range.z;
                
                uint x = (uint)Mathf.Clamp(n.x * 1023f, 0, 1023);
                uint y = (uint)Mathf.Clamp(n.y * 1023f, 0, 1023);
                uint z = (uint)Mathf.Clamp(n.z * 1023f, 0, 1023);
                
                _mortonKeys[i] = new MortonEntry { 
                    code = ExpandBits(x) | (ExpandBits(y) << 1) | (ExpandBits(z) << 2), 
                    originalIndex = i 
                };
            }

            // 3. Sort
            Array.Sort(_mortonKeys, 0, numObjects);
            for(int i=0; i<numObjects; i++) _sortedObjectIndices[i] = _mortonKeys[i].originalIndex;

            // 4. Build
            _nodeCount = 0;
            GenerateHierarchy(0, numObjects - 1);

            // 5. Upload
            UpdateBuffers(numObjects);
        }

        private uint ExpandBits(uint v)
        {
            v = (v * 0x00010001u) & 0xFF0000FFu;
            v = (v * 0x00000101u) & 0x0F00F00Fu;
            v = (v * 0x00000011u) & 0xC30C30C3u;
            v = (v * 0x00000005u) & 0x49249249u;
            return v;
        }

        private int GenerateHierarchy(int first, int last)
        {
            int nodeIdx = _nodeCount++;
            var node = new LBVHNode();

            if (first == last)
            {
                node.leftChild = ~first; // Leaf
                node.rightChild = -1;
                int objIdx = _sortedObjectIndices[first];
                node.boundsMin = _objects[objIdx].boundsMin;
                node.boundsMax = _objects[objIdx].boundsMax;
                _bvhNodes[nodeIdx] = node;
                return nodeIdx;
            }

            int split = FindSplit(first, last);
            int childA = GenerateHierarchy(first, split);
            int childB = GenerateHierarchy(split + 1, last);

            node.leftChild = childA;
            node.rightChild = childB;
            node.boundsMin = Vector3.Min(_bvhNodes[childA].boundsMin, _bvhNodes[childB].boundsMin);
            node.boundsMax = Vector3.Max(_bvhNodes[childA].boundsMax, _bvhNodes[childB].boundsMax);
            _bvhNodes[nodeIdx] = node;
            return nodeIdx;
        }

        private int FindSplit(int first, int last)
        {
            uint firstCode = _mortonKeys[first].code;
            uint lastCode = _mortonKeys[last].code;
            if (firstCode == lastCode) return (first + last) >> 1;

            int commonPrefix = CountLeadingZeros(firstCode ^ lastCode);
            int split = first;
            int step = last - first;
            
            while (step > 1)
            {
                step = (step + 1) >> 1;
                int newSplit = split + step;
                if (newSplit < last)
                {
                    uint splitCode = _mortonKeys[newSplit].code;
                    int splitPrefix = CountLeadingZeros(firstCode ^ splitCode);
                    if (splitPrefix > commonPrefix) split = newSplit;
                }
            }
            return split;
        }

        private int CountLeadingZeros(uint x)
        {
            if (x == 0) return 32;
            int n = 0;
            if (x <= 0x0000FFFF) { n += 16; x <<= 16; }
            if (x <= 0x00FFFFFF) { n += 8; x <<= 8; }
            if (x <= 0x0FFFFFFF) { n += 4; x <<= 4; }
            if (x <= 0x3FFFFFFF) { n += 2; x <<= 2; }
            if (x <= 0x7FFFFFFF) { n += 1; }
            return n;
        }

        private void UpdateBuffers(int numObjects)
        {
            if (SDFObjectBuffer == null || SDFObjectBuffer.count < numObjects)
            {
                SDFObjectBuffer?.Release();
                SDFObjectBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(numObjects, 16), Marshal.SizeOf<SDFObject>());
            }
            SDFObjectBuffer.SetData(_objects);

            if (ObjectIndexBuffer == null || ObjectIndexBuffer.count < numObjects)
            {
                ObjectIndexBuffer?.Release();
                ObjectIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(numObjects, 16), sizeof(int));
            }
            ObjectIndexBuffer.SetData(_sortedObjectIndices, 0, 0, numObjects);

            if (LBVHNodeBuffer == null || LBVHNodeBuffer.count < _nodeCount)
            {
                LBVHNodeBuffer?.Release();
                LBVHNodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(_nodeCount, 16), Marshal.SizeOf<LBVHNode>());
            }
            LBVHNodeBuffer.SetData(_bvhNodes, 0, 0, _nodeCount);
        }

        private void ReleaseBuffers()
        {
            SDFObjectBuffer?.Release(); SDFObjectBuffer = null;
            LBVHNodeBuffer?.Release(); LBVHNodeBuffer = null;
            ObjectIndexBuffer?.Release(); ObjectIndexBuffer = null;
        }
        
        public SDFObject GetObject(int index)
        {
            if (index >= 0 && index < _objects.Count) return _objects[index];
            return default;
        }

        private void OnDrawGizmos()
        {
            if (drawDebugGizmos)
            {
                Gizmos.color = new Color(1, 0, 0, 0.8f);
                foreach (var dirty in _debugDirtyRegions)
                    Gizmos.DrawWireCube(dirty.center, dirty.size);
            }
        }
    }
}