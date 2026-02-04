using System.Collections.Generic;
using UnityEngine;
using VoxelEngine.Core.Data; // For SVONode constants

namespace VoxelEngine.Core.Editing
{
    /// <summary>
    /// The Sparse Edit Database.
    /// Manages the persistence of voxel edits (deltas) on the CPU.
    /// Stores edits hierarchically (Mipmapped) to support efficient LOD generation.
    /// </summary>
    public class VoxelEditManager : MonoSingleton<VoxelEditManager>
    {
        [Header("Global Configuration")]
        [Tooltip("The world-space size of a single voxel (matches the scale of Leaf nodes).")]
        public float voxelSize = 1.0f;
        
        // Configuration
        private const int MAX_LOD = 6; // Deep enough for most chunks
        private const float MAX_SDF_RANGE = 4.0f;
        private const uint MAT_PASSTHROUGH = 255; // Special flag: Voxel should be ignored (fallback to procedural)
        
        // Spatial Hashing Configuration
        // A Meta-Chunk contains META_CHUNK_DIM^3 Bricks.
        // If BrickSize=8, 64 Bricks = 512 Voxels.
        private const int META_CHUNK_DIM = 64; 

        // Key 1: Meta-Chunk Coordinate (Spatial Hash)
        // Key 2: Global Brick Coordinate (at LOD X resolution)
        // Value: The full voxel data for that brick (6x6x6 flattened = 216 uints)
        // _lodDatabases[0] is high-res, [1] is half-res, etc.
        private Dictionary<Vector3Int, Dictionary<Vector3Int, uint[]>>[] _lodDatabases;

        private int _totalEdits = 0;
        public int EditCount => _totalEdits;

        // Persistent Scratch Buffers
        private GraphicsBuffer _editInfoBuffer;
        private GraphicsBuffer _editVoxelBuffer;
        private int[] _infoArray;
        private uint[] _voxelArray;
        private int _currentBufferSize = 0;

        public GraphicsBuffer EditInfoBuffer => _editInfoBuffer;
        public GraphicsBuffer EditVoxelBuffer => _editVoxelBuffer;
        public int[] InfoArray => _infoArray;
        public uint[] VoxelArray => _voxelArray;

        public struct EditData
        {
            public Vector3Int Coordinate;
            public uint[] VoxelData;
        }

        protected override void Awake()
        {
            base.Awake();
            InitializeDatabases();
        }

        private void InitializeDatabases()
        {
            if (_lodDatabases == null)
            {
                _lodDatabases = new Dictionary<Vector3Int, Dictionary<Vector3Int, uint[]>>[MAX_LOD];
                for (int i = 0; i < MAX_LOD; i++)
                {
                    _lodDatabases[i] = new Dictionary<Vector3Int, Dictionary<Vector3Int, uint[]>>();
                }
                _totalEdits = 0;
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            _editInfoBuffer?.Release();
            _editVoxelBuffer?.Release();
        }

        public void PrepareGPUBuffers(int count)
        {
            if (count <= 0) return;

            if (_editInfoBuffer == null || _currentBufferSize < count)
            {
                _editInfoBuffer?.Release();
                _editVoxelBuffer?.Release();

                // Allocate with some headroom
                int newSize = Mathf.Max(64, Mathf.NextPowerOfTwo(count));
                _editInfoBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newSize, 16);
                _editVoxelBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newSize * SVONode.BRICK_VOXEL_COUNT, 4);
                
                _infoArray = new int[newSize * 4];
                _voxelArray = new uint[newSize * SVONode.BRICK_VOXEL_COUNT];
                
                _currentBufferSize = newSize;
            }
        }

        private List<EditData> _cachedEdits = new List<EditData>();

        /// <summary>
        /// Retrieves all edits that intersect with the given world bounds for a specific LOD level.
        /// Uses Spatial Hashing to minimize checks.
        /// </summary>
        /// <param name="bounds">The world bounds to query.</param>
        /// <param name="lodLevel">The LOD level to retrieve edits for.</param>
        public List<EditData> GetEdits(Bounds bounds, int lodLevel = 0)
        {
            _cachedEdits.Clear();
            if (lodLevel < 0 || lodLevel >= MAX_LOD) return _cachedEdits;

            // Calculate dimensions at this LOD
            float currentVoxelSize = voxelSize * Mathf.Pow(2, lodLevel);
            float brickWorldSize = SVONode.BRICK_SIZE * currentVoxelSize;
            float metaChunkWorldSize = brickWorldSize * META_CHUNK_DIM;
            Vector3 brickSizeVec = Vector3.one * brickWorldSize;

            // Determine relevant Meta-Chunks
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;

            int minMetaX = Mathf.FloorToInt(min.x / metaChunkWorldSize);
            int minMetaY = Mathf.FloorToInt(min.y / metaChunkWorldSize);
            int minMetaZ = Mathf.FloorToInt(min.z / metaChunkWorldSize);

            int maxMetaX = Mathf.FloorToInt(max.x / metaChunkWorldSize);
            int maxMetaY = Mathf.FloorToInt(max.y / metaChunkWorldSize);
            int maxMetaZ = Mathf.FloorToInt(max.z / metaChunkWorldSize);

            var db = _lodDatabases[lodLevel];

            // Iterate only relevant Meta-Chunks
            for (int z = minMetaZ; z <= maxMetaZ; z++)
            {
                for (int y = minMetaY; y <= maxMetaY; y++)
                {
                    for (int x = minMetaX; x <= maxMetaX; x++)
                    {
                        Vector3Int metaCoord = new Vector3Int(x, y, z);
                        if (db.TryGetValue(metaCoord, out var bucket))
                        {
                            // Check bricks within this bucket
                            foreach (var kvp in bucket)
                            {
                                Vector3 brickOrigin = new Vector3(kvp.Key.x, kvp.Key.y, kvp.Key.z) * brickWorldSize;
                                Bounds brickBounds = new Bounds(brickOrigin + (brickSizeVec * 0.5f), brickSizeVec);

                                if (bounds.Intersects(brickBounds))
                                {
                                    _cachedEdits.Add(new EditData { Coordinate = kvp.Key, VoxelData = kvp.Value });
                                }
                            }
                        }
                    }
                }
            }
            return _cachedEdits;
        }

        /// <summary>
        /// Registers a delta (edit) for a specific brick at LOD 0 and propagates it up the hierarchy.
        /// </summary>
        /// <param name="coord">The global brick coordinate (ChunkOrigin / BrickSize) at LOD 0.</param>
        /// <param name="data">The 216 integers representing the packed voxels in the brick.</param>
        public void RegisterEdit(Vector3Int coord, uint[] data)
        {
            if (data == null || data.Length != SVONode.BRICK_VOXEL_COUNT)
            {
                Debug.LogError($"[GlobalVoxelEditManager] Invalid edit data. Expected {SVONode.BRICK_VOXEL_COUNT} uints.");
                return;
            }
            
            InitializeDatabases();

            // 1. Store LOD 0
            SetBrickData(0, coord, (uint[])data.Clone());

            // 2. Propagate Up
            PropagateEdit(coord, 0);
        }

        private void PropagateEdit(Vector3Int childCoord, int currentLevel)
        {
            if (currentLevel >= MAX_LOD - 1) return;

            int nextLevel = currentLevel + 1;
            
            // Calculate Parent Coordinate (integer division by 2)
            Vector3Int parentCoord = new Vector3Int(
                Mathf.FloorToInt(childCoord.x / 2.0f),
                Mathf.FloorToInt(childCoord.y / 2.0f),
                Mathf.FloorToInt(childCoord.z / 2.0f)
            );

            // Create/Update Parent Brick
            uint[] parentData = new uint[SVONode.BRICK_VOXEL_COUNT];
            bool parentHasAnyData = false;
            
            // Loop over Parent Brick Voxels (including padding)
            // Dimensions: 6x6x6
            for (int z = 0; z < SVONode.BRICK_STORAGE_SIZE; z++)
            {
                for (int y = 0; y < SVONode.BRICK_STORAGE_SIZE; y++)
                {
                    for (int x = 0; x < SVONode.BRICK_STORAGE_SIZE; x++)
                    {
                        // Logical position relative to Parent Brick Origin (in Parent Voxel Units)
                        // Range: -1 to 4 (since PADDING is 1)
                        int logicalX = x - SVONode.BRICK_PADDING;
                        int logicalY = y - SVONode.BRICK_PADDING;
                        int logicalZ = z - SVONode.BRICK_PADDING;

                        // Map to Child Coordinate System (LOD N)
                        // Each Parent Voxel covers 2x2x2 Child Voxels
                        int childStartX = logicalX * 2;
                        int childStartY = logicalY * 2;
                        int childStartZ = logicalZ * 2;

                        // Accumulators for downsampling
                        float accSDF = 0;
                        Vector3 accNorm = Vector3.zero;
                        Dictionary<uint, int> matCounts = new Dictionary<uint, int>();
                        int validSampleCount = 0;

                        // Sample the 2x2x2 block in the children
                        for (int cz = 0; cz < 2; cz++)
                        {
                            for (int cy = 0; cy < 2; cy++)
                            {
                                for (int cx = 0; cx < 2; cx++)
                                {
                                    int globalChildX = childStartX + cx;
                                    int globalChildY = childStartY + cy;
                                    int globalChildZ = childStartZ + cz;

                                    // Determine which Child Brick this falls into
                                    // The Parent Brick starts at parentCoord * 2 (in Child Coords)
                                    // Relative to Parent Origin, we are at (globalChildX, ...)
                                    
                                    // Global Child Block Coordinate relative to the "Base" child (parentCoord * 2)
                                    int childBlockOffX = Mathf.FloorToInt(globalChildX / (float)SVONode.BRICK_SIZE);
                                    int childBlockOffY = Mathf.FloorToInt(globalChildY / (float)SVONode.BRICK_SIZE);
                                    int childBlockOffZ = Mathf.FloorToInt(globalChildZ / (float)SVONode.BRICK_SIZE);

                                    // Handle negative coordinates (padding area might dip into previous block)
                                    if (globalChildX < 0) childBlockOffX = -1;
                                    if (globalChildY < 0) childBlockOffY = -1;
                                    if (globalChildZ < 0) childBlockOffZ = -1;

                                    Vector3Int targetChildCoord = parentCoord * 2 + new Vector3Int(childBlockOffX, childBlockOffY, childBlockOffZ);
                                    
                                    // Local voxel index within that child brick
                                    int localChildVoxelX = (globalChildX - (childBlockOffX * SVONode.BRICK_SIZE)) + SVONode.BRICK_PADDING;
                                    int localChildVoxelY = (globalChildY - (childBlockOffY * SVONode.BRICK_SIZE)) + SVONode.BRICK_PADDING;
                                    int localChildVoxelZ = (globalChildZ - (childBlockOffZ * SVONode.BRICK_SIZE)) + SVONode.BRICK_PADDING;

                                    // Sample
                                    float s_sdf = MAX_SDF_RANGE;
                                    Vector3 s_norm = Vector3.up;
                                    uint s_mat = MAT_PASSTHROUGH;

                                    // Use Helper
                                    if (TryGetBrickData(currentLevel, targetChildCoord, out uint[] childData))
                                    {
                                        int flatIdx = localChildVoxelZ * SVONode.BRICK_STORAGE_SIZE * SVONode.BRICK_STORAGE_SIZE +
                                                      localChildVoxelY * SVONode.BRICK_STORAGE_SIZE +
                                                      localChildVoxelX;
                                        
                                        if (flatIdx >= 0 && flatIdx < childData.Length)
                                        {
                                            UnpackVoxelData(childData[flatIdx], out s_sdf, out s_norm, out s_mat);
                                        }
                                    }

                                    // Only accumulate if not passthrough
                                    if (s_mat != MAT_PASSTHROUGH)
                                    {
                                        accSDF += s_sdf;
                                        accNorm += s_norm;
                                        if (!matCounts.ContainsKey(s_mat)) matCounts[s_mat] = 0;
                                        matCounts[s_mat]++;
                                        validSampleCount++;
                                    }
                                }
                            }
                        }

                        // Store Result
                        int parentFlatIdx = z * SVONode.BRICK_STORAGE_SIZE * SVONode.BRICK_STORAGE_SIZE +
                                            y * SVONode.BRICK_STORAGE_SIZE +
                                            x;

                        if (validSampleCount > 0)
                        {
                            // Average
                            float avgSDF = accSDF / validSampleCount;
                            Vector3 avgNorm = accNorm.normalized;
                            
                            // Dominant Material
                            uint domMat = 1; // Default fallback
                            int maxCount = -1;
                            foreach(var kvp in matCounts)
                            {
                                if (kvp.Value > maxCount) { maxCount = kvp.Value; domMat = kvp.Key; }
                            }
                            
                            parentData[parentFlatIdx] = PackVoxelData(avgSDF, avgNorm, domMat);
                            parentHasAnyData = true;
                        }
                        else
                        {
                            // No valid children -> Passthrough
                            parentData[parentFlatIdx] = PackVoxelData(MAX_SDF_RANGE, Vector3.up, MAT_PASSTHROUGH);
                        }
                    }
                }
            }

            // Save Parent
            if (parentHasAnyData)
            {
                SetBrickData(nextLevel, parentCoord, parentData);
                // Recurse
                PropagateEdit(parentCoord, nextLevel);
            }
            else
            {
                // Remove if empty
                RemoveBrickData(nextLevel, parentCoord);
            }
        }

        /// <summary>
        /// Checks if a specific brick has been modified at LOD 0.
        /// </summary>
        public bool HasEdit(Vector3Int coord)
        {
            if (_lodDatabases == null || _lodDatabases.Length == 0) return false;
            return TryGetBrickData(0, coord, out _);
        }

        /// <summary>
        /// Clears all stored edits.
        /// </summary>
        public void Clear()
        {
            if (_lodDatabases != null)
            {
                foreach (var db in _lodDatabases) db.Clear();
            }
            _totalEdits = 0;
        }

        // --- Spatial Hashing Helpers ---

        private Vector3Int GetMetaChunkCoord(Vector3Int brickCoord)
        {
            // Helper for floor division
            return new Vector3Int(
                Mathf.FloorToInt(brickCoord.x / (float)META_CHUNK_DIM),
                Mathf.FloorToInt(brickCoord.y / (float)META_CHUNK_DIM),
                Mathf.FloorToInt(brickCoord.z / (float)META_CHUNK_DIM)
            );
        }

        private bool TryGetBrickData(int lod, Vector3Int brickCoord, out uint[] data)
        {
            data = null;
            if (lod < 0 || lod >= MAX_LOD) return false;

            var metaCoord = GetMetaChunkCoord(brickCoord);
            if (_lodDatabases[lod].TryGetValue(metaCoord, out var bucket))
            {
                return bucket.TryGetValue(brickCoord, out data);
            }
            return false;
        }

        private void SetBrickData(int lod, Vector3Int brickCoord, uint[] data)
        {
            if (lod < 0 || lod >= MAX_LOD) return;

            var metaCoord = GetMetaChunkCoord(brickCoord);
            var db = _lodDatabases[lod];

            if (!db.TryGetValue(metaCoord, out var bucket))
            {
                bucket = new Dictionary<Vector3Int, uint[]>();
                db.Add(metaCoord, bucket);
            }

            // Track count only for LOD 0
            if (lod == 0 && !bucket.ContainsKey(brickCoord))
            {
                _totalEdits++;
            }

            bucket[brickCoord] = data;
        }

        private void RemoveBrickData(int lod, Vector3Int brickCoord)
        {
            if (lod < 0 || lod >= MAX_LOD) return;

            var metaCoord = GetMetaChunkCoord(brickCoord);
            var db = _lodDatabases[lod];

            if (db.TryGetValue(metaCoord, out var bucket))
            {
                if (bucket.ContainsKey(brickCoord))
                {
                    bucket.Remove(brickCoord);
                    if (lod == 0) _totalEdits--;

                    // Clean up empty buckets
                    if (bucket.Count == 0)
                    {
                        db.Remove(metaCoord);
                    }
                }
            }
        }

        /// <summary>
        /// Helper to convert a World Position to the Global Brick Coordinate (LOD 0).
        /// </summary>
        public Vector3Int GetBrickCoordinate(Vector3 worldPos)
        {            
            float brickSizeWorld = SVONode.BRICK_SIZE * voxelSize;
            return Vector3Int.FloorToInt(worldPos / brickSizeWorld);
        }

        // --- Packing Helpers (Replicated from HLSL) ---

        private static uint PackVoxelData(float sdf, Vector3 normal, uint materialID)
        {
            uint mat = materialID & 0xFF;
            float normalizedSDF = Mathf.Clamp(sdf / MAX_SDF_RANGE, -1.0f, 1.0f);
            uint sdfInt = (uint)((normalizedSDF * 0.5f + 0.5f) * 255.0f);
            uint norm = PackNormalOct(normal);
            return mat | (sdfInt << 8) | (norm << 16);
        }

        private static void UnpackVoxelData(uint data, out float sdf, out Vector3 normal, out uint materialID)
        {
            materialID = data & 0xFF;
            uint sdfInt = (data >> 8) & 0xFF;
            float normalizedSDF = (sdfInt / 255.0f) * 2.0f - 1.0f;
            sdf = normalizedSDF * MAX_SDF_RANGE;
            normal = UnpackNormalOct(data >> 16);
        }

        private static uint PackNormalOct(Vector3 n)
        {
            // Avoid div by zero
            float sum = Mathf.Abs(n.x) + Mathf.Abs(n.y) + Mathf.Abs(n.z);
            if (sum < 1e-5f) return 0; // Default

            n /= sum;
            Vector2 oct = n.z >= 0 ? new Vector2(n.x, n.y) : (Vector2.one - new Vector2(Mathf.Abs(n.y), Mathf.Abs(n.x))) * new Vector2(n.x >= 0 ? 1 : -1, n.y >= 0 ? 1 : -1);
            
            uint x = (uint)(Mathf.Clamp01(oct.x * 0.5f + 0.5f) * 255.0f);
            uint y = (uint)(Mathf.Clamp01(oct.y * 0.5f + 0.5f) * 255.0f);
            
            return x | (y << 8);
        }

        private static Vector3 UnpackNormalOct(uint p)
        {
            float x = ((p & 0xFF) / 255.0f);
            float y = (((p >> 8) & 0xFF) / 255.0f);
            
            Vector2 oct = new Vector2(x, y) * 2.0f - Vector2.one;
            Vector3 n = new Vector3(oct.x, oct.y, 1.0f - Mathf.Abs(oct.x) - Mathf.Abs(oct.y));
            float t = Mathf.Clamp01(-n.z);
            
            n.x += n.x >= 0 ? -t : t;
            n.y += n.y >= 0 ? -t : t;
            
            return n.normalized;
        }
    }
}