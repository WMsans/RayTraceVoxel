using System; using System.Collections.Generic; using UnityEngine; using VoxelEngine.Core.Data; using System.Runtime.InteropServices;
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
        // The raw list of objects
        private List<SDFObject> _objects = new List<SDFObject>();
        
        // Helper struct for sorting
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
        private int[] _sortedObjectIndices; // Maps leaf index -> original object list index
        private int _nodeCount = 0;

        // --- GPU Buffers ---
        public GraphicsBuffer SDFObjectBuffer { get; private set; }
        public GraphicsBuffer LBVHNodeBuffer { get; private set; }
        public GraphicsBuffer ObjectIndexBuffer { get; private set; } // Indirection buffer

        public int ObjectCount => _objects.Count;
        public bool IsReady => _objects.Count > 0 && LBVHNodeBuffer != null && LBVHNodeBuffer.IsValid();

        // --- Public API ---

        public void RegisterObject(SDFObject obj)
        {
            _objects.Add(obj);
        }

        public void ClearObjects()
        {
            _objects.Clear();
        }

        public void UpdateObject(int index, SDFObject obj)
        {
            if (index >= 0 && index < _objects.Count)
            {
                _objects[index] = obj;
            }
        }

        private void OnDisable()
        {
            ReleaseBuffers();
        }

        private void Update()
        {
            if (rebuildEveryFrame && _objects.Count > 0)
            {
                RebuildBVH();
            }
        }

        /// <summary>
        /// Rebuilds the Linear BVH from scratch.
        /// 1. Calculate Bounds & Morton Codes
        /// 2. Sort by Morton Code
        /// 3. Build Hierarchy (Linear construction)
        /// 4. Upload to GPU
        /// </summary>
        public void RebuildBVH()
        {
            int numObjects = _objects.Count;
            if (numObjects == 0) return;

            // 1. Resize Arrays
            if (_mortonKeys == null || _mortonKeys.Length < numObjects)
                _mortonKeys = new MortonEntry[numObjects];
            
            if (_sortedObjectIndices == null || _sortedObjectIndices.Length < numObjects)
                _sortedObjectIndices = new int[numObjects];

            // Internal nodes = N - 1. Total nodes = 2*N - 1. 
            // We'll allocate enough space.
            int maxNodes = numObjects * 2; 
            if (_bvhNodes == null || _bvhNodes.Length < maxNodes)
                _bvhNodes = new LBVHNode[maxNodes];

            // 2. Calculate Global Bounds (to normalize Morton codes)
            Vector3 globalMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 globalMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i < numObjects; i++)
            {
                var bMin = _objects[i].boundsMin;
                var bMax = _objects[i].boundsMax;
                globalMin = Vector3.Min(globalMin, bMin);
                globalMax = Vector3.Max(globalMax, bMax);
            }
            
            // Add margin to avoid edge cases
            globalMin -= Vector3.one * globalBoundsMargin;
            globalMax += Vector3.one * globalBoundsMargin;
            Vector3 range = globalMax - globalMin;
            // Prevent division by zero
            range.x = Mathf.Max(range.x, 0.001f);
            range.y = Mathf.Max(range.y, 0.001f);
            range.z = Mathf.Max(range.z, 0.001f);

            // 3. Compute Morton Codes
            for (int i = 0; i < numObjects; i++)
            {
                // Calculate centroid
                Vector3 center = (_objects[i].boundsMin + _objects[i].boundsMax) * 0.5f;
                
                // Normalize to [0, 1]
                Vector3 n = new Vector3(
                    (center.x - globalMin.x) / range.x,
                    (center.y - globalMin.y) / range.y,
                    (center.z - globalMin.z) / range.z
                );
                
                // Quantize to 10 bits [0, 1023]
                uint x = (uint)Mathf.Clamp(n.x * 1023.0f, 0, 1023);
                uint y = (uint)Mathf.Clamp(n.y * 1023.0f, 0, 1023);
                uint z = (uint)Mathf.Clamp(n.z * 1023.0f, 0, 1023);
                
                _mortonKeys[i] = new MortonEntry
                {
                    code = ExpandBits(x) | (ExpandBits(y) << 1) | (ExpandBits(z) << 2),
                    originalIndex = i
                };
            }

            // 4. Sort (Linearizes the objects along the Z-curve)
            // Note: For < 10k objects, Array.Sort is extremely fast on CPU.
            Array.Sort(_mortonKeys, 0, numObjects);

            // Fill the indirection array
            for(int i=0; i<numObjects; i++)
            {
                _sortedObjectIndices[i] = _mortonKeys[i].originalIndex;
            }

            // 5. Build Hierarchy
            _nodeCount = 0;
            GenerateHierarchy(0, numObjects - 1);

            // 6. Upload to GPU
            UpdateBuffers(numObjects);
        }

        // Expands a 10-bit integer into 30 bits by inserting 2 zeros after each bit.
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
            // Allocate new node
            int nodeIdx = _nodeCount++;
            var node = new LBVHNode();

            // Leaf Case
            if (first == last)
            {
                // Bitwise NOT to indicate leaf and store index
                // We point to the index in the sorted list (0..N-1), which the shader then uses to look up _ObjectIndices
                node.leftChild = ~first; 
                node.rightChild = -1; // Unused
                
                // Set bounds from the actual object
                int objIdx = _sortedObjectIndices[first];
                node.boundsMin = _objects[objIdx].boundsMin;
                node.boundsMax = _objects[objIdx].boundsMax;
                
                _bvhNodes[nodeIdx] = node;
                return nodeIdx;
            }

            // Internal Node Case: Find Split
            int split = FindSplit(first, last);

            int childA = GenerateHierarchy(first, split);
            int childB = GenerateHierarchy(split + 1, last);

            node.leftChild = childA;
            node.rightChild = childB;

            // Compute AABB union
            Vector3 minA = _bvhNodes[childA].boundsMin;
            Vector3 maxA = _bvhNodes[childA].boundsMax;
            Vector3 minB = _bvhNodes[childB].boundsMin;
            Vector3 maxB = _bvhNodes[childB].boundsMax;

            node.boundsMin = Vector3.Min(minA, minB);
            node.boundsMax = Vector3.Max(maxA, maxB);

            _bvhNodes[nodeIdx] = node;
            return nodeIdx;
        }

        /// <summary>
        /// Finds the split index that partitions the range [first, last] based on the highest differing bit in Morton codes.
        /// </summary>
        private int FindSplit(int first, int last)
        {
            uint firstCode = _mortonKeys[first].code;
            uint lastCode = _mortonKeys[last].code;

            // If codes are identical, split in the middle
            if (firstCode == lastCode)
            {
                return (first + last) >> 1;
            }

            // Calculate the highest differing bit between the first and last keys
            // This effectively finds the "plane" separating the volume
            int commonPrefix = CountLeadingZeros(firstCode ^ lastCode);

            // Use binary search to find where the next bit changes
            int split = first; // Initial guess
            int step = last - first;

            while (step > 1)
            {
                step = (step + 1) >> 1;
                int newSplit = split + step;

                if (newSplit < last)
                {
                    uint splitCode = _mortonKeys[newSplit].code;
                    int splitPrefix = CountLeadingZeros(firstCode ^ splitCode);
                    
                    // If the prefix matches the common prefix of the range, it belongs to the left side
                    if (splitPrefix > commonPrefix)
                    {
                        split = newSplit;
                    }
                }
            }

            return split;
        }

        private int CountLeadingZeros(uint x)
        {
            // Generic C# implementation for CLZ
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
            // 1. Objects Buffer
            if (SDFObjectBuffer == null || SDFObjectBuffer.count < numObjects)
            {
                SDFObjectBuffer?.Release();
                SDFObjectBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, numObjects, Marshal.SizeOf<SDFObject>());
            }
            SDFObjectBuffer.SetData(_objects);

            // 2. Indices Buffer (Sorted)
            if (ObjectIndexBuffer == null || ObjectIndexBuffer.count < numObjects)
            {
                ObjectIndexBuffer?.Release();
                ObjectIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, numObjects, sizeof(int));
            }
            ObjectIndexBuffer.SetData(_sortedObjectIndices, 0, 0, numObjects);

            // 3. BVH Nodes Buffer
            if (LBVHNodeBuffer == null || LBVHNodeBuffer.count < _nodeCount)
            {
                LBVHNodeBuffer?.Release();
                LBVHNodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _nodeCount, Marshal.SizeOf<LBVHNode>());
            }
            LBVHNodeBuffer.SetData(_bvhNodes, 0, 0, _nodeCount);
        }

        private void ReleaseBuffers()
        {
            SDFObjectBuffer?.Release();
            LBVHNodeBuffer?.Release();
            ObjectIndexBuffer?.Release();
        }
        
        private void OnDrawGizmos()
        {
            if (!drawDebugGizmos || _nodeCount == 0 || _bvhNodes == null) return;
            
            // Draw Root
            Gizmos.color = Color.cyan;
            DrawNodeRecursive(0, 0);
        }

        private void DrawNodeRecursive(int nodeIdx, int depth)
        {
            if (nodeIdx < 0 || nodeIdx >= _nodeCount) return;
            var node = _bvhNodes[nodeIdx];
            
            Vector3 size = node.boundsMax - node.boundsMin;
            Vector3 center = node.boundsMin + size * 0.5f;
            
            Gizmos.DrawWireCube(center, size);

            if (node.leftChild >= 0) // Internal Node
            {
                DrawNodeRecursive(node.leftChild, depth + 1);
                DrawNodeRecursive(node.rightChild, depth + 1);
            }
        }
        public SDFObject GetObject(int index)
        {
            if (index >= 0 && index < _objects.Count) return _objects[index];
            return default;
        }
    }
}