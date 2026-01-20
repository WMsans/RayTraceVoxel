using System; 
using System.Collections.Generic; 
using UnityEngine; 
using UnityEngine.Rendering;
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

        // --- Voxel Grid Data ---
        [Header("Voxel Grid Atlas")]
        public ComputeShader textureWriterCompute; // [ASSIGN THIS IN INSPECTOR]
        public int atlasResolution = 32; // Resolution of a single chunk (32x32x32)
        public int maxAtlasChunks = 16;  // How many chunks to store in the atlas
        
        // Changed to RenderTexture to allow Compute Shader writes
        private RenderTexture _chunkAtlas;
        public RenderTexture ChunkAtlas => _chunkAtlas;
        private int _allocatedChunks = 0;
        
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

        protected override void Awake()
        {
            base.Awake();
            InitializeAtlas();
        }

        private void InitializeAtlas()
        {
            if (_chunkAtlas != null) _chunkAtlas.Release();

            // Create a 3D RenderTexture
            _chunkAtlas = new RenderTexture(atlasResolution, atlasResolution, 0, RenderTextureFormat.RHalf);
            _chunkAtlas.dimension = TextureDimension.Tex3D;
            _chunkAtlas.volumeDepth = atlasResolution * maxAtlasChunks;
            _chunkAtlas.enableRandomWrite = true; // Required for Compute Shader
            _chunkAtlas.wrapMode = TextureWrapMode.Clamp;
            _chunkAtlas.filterMode = FilterMode.Bilinear;
            _chunkAtlas.name = "SDF_Chunk_Atlas";
            _chunkAtlas.Create();
            
            // Clear to "Empty" (Positive SDF)
            if (textureWriterCompute != null)
            {
                int kernel = textureWriterCompute.FindKernel("ClearAtlas");
                textureWriterCompute.SetTexture(kernel, "_OutputTexture", _chunkAtlas);
                textureWriterCompute.SetFloat("_ClearValue", 4.0f); // Max SDF
                
                // Dispatch over entire atlas
                int totalZ = atlasResolution * maxAtlasChunks;
                textureWriterCompute.Dispatch(kernel, 
                    Mathf.CeilToInt(atlasResolution/8f), 
                    Mathf.CeilToInt(atlasResolution/8f), 
                    Mathf.CeilToInt(totalZ/8f));
            }

            _allocatedChunks = 0;
        }

        /// <summary>
        /// Registers a raw voxel grid into the atlas and returns the chunk index.
        /// Returns -1 if atlas is full.
        /// </summary>
        public int RegisterVoxelGrid(uint[] packedData, int resolution)
        {
            if (_allocatedChunks >= maxAtlasChunks)
            {
                Debug.LogWarning("DynamicSDFManager: Atlas Full! Cannot register new Voxel Grid.");
                return -1;
            }
            
            if (textureWriterCompute == null)
            {
                Debug.LogError("DynamicSDFManager: TextureWriter Compute Shader not assigned!");
                return -1;
            }

            int index = _allocatedChunks++;
            
            // 1. Prepare Data for GPU (Convert packed uint to float SDF)
            // We use a float buffer to upload only the R channel data
            int totalVoxels = atlasResolution * atlasResolution * atlasResolution;
            float[] sdfValues = new float[totalVoxels];
            float maxSdf = 4.0f; 

            // Assuming 'resolution' matches 'atlasResolution' for now
            // Or simple linear mapping if packedData matches size
            for (int i = 0; i < totalVoxels; i++)
            {
                if (i < packedData.Length)
                {
                    uint val = packedData[i];
                    uint sdfInt = (val >> 8) & 0xFF;
                    float normalizedSDF = (sdfInt / 255.0f) * 2.0f - 1.0f; // -1 to 1
                    sdfValues[i] = normalizedSDF * maxSdf;
                }
                else
                {
                    sdfValues[i] = maxSdf; // Empty
                }
            }

            // 2. Upload to ComputeBuffer
            ComputeBuffer uploadBuffer = new ComputeBuffer(totalVoxels, sizeof(float));
            uploadBuffer.SetData(sdfValues);

            // 3. Dispatch Compute to write into 3D Texture
            int kernel = textureWriterCompute.FindKernel("WriteSDFChunk");
            textureWriterCompute.SetBuffer(kernel, "_InputBuffer", uploadBuffer);
            textureWriterCompute.SetTexture(kernel, "_OutputTexture", _chunkAtlas);
            textureWriterCompute.SetInts("_WriteOffset", new int[] { 0, 0, index * atlasResolution });
            textureWriterCompute.SetInt("_Resolution", atlasResolution);
            
            int groups = Mathf.CeilToInt(atlasResolution / 8.0f);
            textureWriterCompute.Dispatch(kernel, groups, groups, groups);

            // Cleanup
            uploadBuffer.Dispose();

            return index;
        }

        public void RegisterObject(SDFObject obj)
        {
            _objects.Add(obj);
            AddDirtyRegion(obj);
            _isDirty = true;
            if (!rebuildEveryFrame) RebuildBVH(); 
        }

        public void UpdateObject(int index, SDFObject obj)
        {
            if (index >= 0 && index < _objects.Count)
            {
                SDFObject oldObj = _objects[index];
                if (IsSame(oldObj, obj)) return;

                AddDirtyRegion(oldObj);
                _objects[index] = obj;
                AddDirtyRegion(obj);
                _isDirty = true;
            }
        }

        public void RemoveObjectAt(int index)
        {
            if (index >= 0 && index < _objects.Count)
            {
                AddDirtyRegion(_objects[index]);
                _objects.RemoveAt(index);
                _isDirty = true;
                if (_objects.Count == 0) ReleaseBuffers();
            }
        }

        public int FindClosestObject(Vector3 position, float radius)
        {
            int bestIndex = -1;
            float minSqrDst = radius * radius;
            for (int i = 0; i < _objects.Count; i++)
            {
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
            size += Vector3.one * 2.0f; 
            _dirtyRegions.Add(new Bounds(center, size));
        }

        private void OnDisable() => ReleaseBuffers();

        private void Update()
        {
            // Bind Atlas Globally so Compute Shaders can find it
            if (_chunkAtlas != null)
            {
                Shader.SetGlobalTexture("_SDFChunkAtlas", _chunkAtlas);
                Shader.SetGlobalVector("_SDFChunkAtlasParams", new Vector4(atlasResolution, maxAtlasChunks, 0, 0));
            }

            if (rebuildEveryFrame && _isDirty)
            {
                RebuildBVH();
                _isDirty = false;
            }
        }

        public void RebuildBVH()
        {
            int numObjects = _objects.Count;
            if (numObjects == 0) return;

            if (_mortonKeys == null || _mortonKeys.Length < numObjects) _mortonKeys = new MortonEntry[numObjects];
            if (_sortedObjectIndices == null || _sortedObjectIndices.Length < numObjects) _sortedObjectIndices = new int[numObjects];
            if (_bvhNodes == null || _bvhNodes.Length < numObjects * 2) _bvhNodes = new LBVHNode[numObjects * 2];

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

            Array.Sort(_mortonKeys, 0, numObjects);
            for(int i=0; i<numObjects; i++) _sortedObjectIndices[i] = _mortonKeys[i].originalIndex;

            _nodeCount = 0;
            GenerateHierarchy(0, numObjects - 1);

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
                node.leftChild = ~first; 
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
            if (_chunkAtlas != null) { _chunkAtlas.Release(); _chunkAtlas = null; }
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