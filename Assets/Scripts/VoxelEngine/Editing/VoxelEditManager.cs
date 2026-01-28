using System.Collections.Generic;
using UnityEngine;
using VoxelEngine.Core.Data; // For SVONode constants

namespace VoxelEngine.Core.Editing
{
    /// <summary>
    /// The Sparse Edit Database.
    /// Manages the persistence of voxel edits (deltas) on the CPU.
    /// Stores edits at the highest resolution (LOD 0) to ensure consistency across LOD levels.
    /// </summary>
    public class VoxelEditManager : MonoSingleton<VoxelEditManager>
    {
        [Header("Global Configuration")]
        [Tooltip("The world-space size of a single voxel (matches the scale of Leaf nodes).")]
        public float voxelSize = 1.0f;
        // Key: Global Brick Coordinate (at LOD 0 resolution)
        // Value: The full voxel data for that brick (6x6x6 flattened = 216 uints)
        // Stored as uint[] because it matches the packed GPU data format.
        private Dictionary<Vector3Int, uint[]> _sparseDatabase = new Dictionary<Vector3Int, uint[]>();

        public int EditCount => _sparseDatabase.Count;

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
        /// Retrieves all edits that intersect with the given world bounds.
        /// </summary>
        /// <param name="bounds">The world bounds to query.</param>
        /// <returns>A list of EditData containing the coordinate and voxel data.</returns>
        public List<EditData> GetEdits(Bounds bounds)
        {
            _cachedEdits.Clear();
            float brickWorldSize = SVONode.BRICK_SIZE * voxelSize;
            Vector3 brickSizeVec = Vector3.one * brickWorldSize;

            foreach (var kvp in _sparseDatabase)
            {
                Vector3 brickOrigin = new Vector3(kvp.Key.x, kvp.Key.y, kvp.Key.z) * brickWorldSize;
                Bounds brickBounds = new Bounds(brickOrigin + (brickSizeVec * 0.5f), brickSizeVec);

                if (bounds.Intersects(brickBounds))
                {
                    _cachedEdits.Add(new EditData { Coordinate = kvp.Key, VoxelData = kvp.Value });
                }
            }
            return _cachedEdits;
        }

        /// <summary>
        /// Registers a delta (edit) for a specific brick.
        /// Overwrites any existing edit for this coordinate.
        /// </summary>
        /// <param name="coord">The global coordinate of the brick (ChunkOrigin / BrickSize).</param>
        /// <param name="data">The 216 integers representing the packed voxels in the brick.</param>
        public void RegisterEdit(Vector3Int coord, uint[] data)
        {
            if (data == null || data.Length != SVONode.BRICK_VOXEL_COUNT)
            {
                Debug.LogError($"[GlobalVoxelEditManager] Invalid edit data. Expected {SVONode.BRICK_VOXEL_COUNT} uints.");
                return;
            }

            // Clone the array to ensure the database owns the data (persisting it on CPU)
            // This protects against the source array being reused by buffers or other operations.
            if (_sparseDatabase.ContainsKey(coord))
            {
                _sparseDatabase[coord] = (uint[])data.Clone();
            }
            else
            {
                _sparseDatabase.Add(coord, (uint[])data.Clone());
            }
        }

        /// <summary>
        /// Tries to retrieve an existing edit for a brick at the given coordinate.
        /// </summary>
        /// <param name="coord">The global brick coordinate.</param>
        /// <param name="data">The retrieved compressed voxel data, or null if not found.</param>
        /// <returns>True if an edit exists, false otherwise.</returns>
        public bool TryGetEdit(Vector3Int coord, out uint[] data)
        {
            return _sparseDatabase.TryGetValue(coord, out data);
        }

        /// <summary>
        /// Checks if a specific brick has been modified.
        /// </summary>
        public bool HasEdit(Vector3Int coord)
        {
            return _sparseDatabase.ContainsKey(coord);
        }

        /// <summary>
        /// Clears all stored edits.
        /// </summary>
        public void Clear()
        {
            _sparseDatabase.Clear();
        }

        /// <summary>
        /// Helper to convert a World Position to the Global Brick Coordinate (LOD 0).
        /// This ensures all edits map to the same grid regardless of the current chunk's LOD.
        /// </summary>
        public Vector3Int GetBrickCoordinate(Vector3 worldPos)
        {            
            // Calculate size of a brick in world units at LOD 0
            float brickSizeWorld = SVONode.BRICK_SIZE * voxelSize;
            
            // FloorToInt handles the coordinate mapping
            return Vector3Int.FloorToInt(worldPos / brickSizeWorld);
        }
    }
}