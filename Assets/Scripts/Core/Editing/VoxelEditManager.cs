using System.Collections.Generic;
using UnityEngine;
using VoxelEngine.Core.Data; // For SVONode constants

namespace VoxelEngine.Core.Editing
{
    /// <summary>
    /// Phase 1: The Sparse Edit Database.
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