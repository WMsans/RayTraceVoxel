using System.Collections.Generic;
using UnityEngine;
using VoxelEngine.Core.Data; // For SVONode constants

namespace VoxelEngine.Core.Editing
{
    public class VoxelEditManager : MonoSingleton<VoxelEditManager>
    {
        [Header("Global Configuration")]
        [Tooltip("The world-space size of a single voxel (matches the scale of Leaf nodes).")]
        public float voxelSize = 1.0f;

        /// <summary>
        /// Holds the raw data for a single 4x4x4 brick (plus padding).
        /// Total size: 6x6x6 = 216 uints.
        /// </summary>
        [System.Serializable]
        public struct CompressedBrick
        {
            public uint[] data;

            public CompressedBrick(uint[] data)
            {
                this.data = data;
            }
        }

        // The "Database" of modified bricks.
        // Key: Global Brick Coordinate (x, y, z)
        private Dictionary<Vector3Int, CompressedBrick> _modifiedBricks = new Dictionary<Vector3Int, CompressedBrick>();

        /// <summary>
        /// Stores or updates a brick in the persistent storage.
        /// </summary>
        /// <param name="brickIndex">The global index of the brick.</param>
        /// <param name="data">The raw 216-uint array extracted from the GPU.</param>
        public void StoreBrick(Vector3Int brickIndex, uint[] data)
        {
            if (data == null || data.Length != SVONode.BRICK_VOXEL_COUNT)
            {
                Debug.LogError($"[VoxelEditManager] Invalid brick data length. Expected {SVONode.BRICK_VOXEL_COUNT}, got {data?.Length}");
                return;
            }

            if (_modifiedBricks.ContainsKey(brickIndex))
            {
                _modifiedBricks[brickIndex] = new CompressedBrick(data);
            }
            else
            {
                _modifiedBricks.Add(brickIndex, new CompressedBrick(data));
            }
        }

        /// <summary>
        /// Converts a world position into a Global Brick Index.
        /// </summary>
        public Vector3Int GetGlobalBrickIndex(Vector3 worldPos)
        {
            // Calculate the size of a brick in world units (4 * Scale)
            float brickWorldSize = SVONode.BRICK_SIZE * voxelSize;

            return new Vector3Int(
                Mathf.FloorToInt(worldPos.x / brickWorldSize),
                Mathf.FloorToInt(worldPos.y / brickWorldSize),
                Mathf.FloorToInt(worldPos.z / brickWorldSize)
            );
        }

        /// <summary>
        /// Retrieves all stored bricks that intersect with the given Chunk bounds.
        /// Optimized to iterate over sparse edits rather than the dense volume.
        /// </summary>
        public List<KeyValuePair<Vector3Int, CompressedBrick>> GetEditsInChunk(Bounds chunkBounds)
        {
            var results = new List<KeyValuePair<Vector3Int, CompressedBrick>>();

            // 1. Calculate the range of Brick Indices that this chunk covers
            Vector3Int minBrick = GetGlobalBrickIndex(chunkBounds.min);
            
            // Use a slight offset for max to ensure we don't include the neighbor brick 
            // if the bounds land exactly on the edge.
            Vector3 maxPos = chunkBounds.max - Vector3.one * (voxelSize * 0.01f);
            Vector3Int maxBrick = GetGlobalBrickIndex(maxPos);

            // 2. Iterate through stored edits (Sparse check)
            // This is much faster than checking every potential brick coordinate for large LOD chunks
            foreach (var kvp in _modifiedBricks)
            {
                Vector3Int idx = kvp.Key;
                if (idx.x >= minBrick.x && idx.x <= maxBrick.x &&
                    idx.y >= minBrick.y && idx.y <= maxBrick.y &&
                    idx.z >= minBrick.z && idx.z <= maxBrick.z)
                {
                    results.Add(kvp);
                }
            }

            return results;
        }

        /// <summary>
        /// Helper to check if a specific brick exists.
        /// </summary>
        public bool TryGetBrick(Vector3Int index, out CompressedBrick brick)
        {
            return _modifiedBricks.TryGetValue(index, out brick);
        }
        
        /// <summary>
        /// Debug Visualization of stored bricks.
        /// </summary>
        private void OnDrawGizmos()
        {
            if (_modifiedBricks.Count > 0)
            {
                Gizmos.color = new Color(1, 0.5f, 0, 0.5f); // Orange
                float brickWorldSize = SVONode.BRICK_SIZE * voxelSize;
                Vector3 size = Vector3.one * brickWorldSize;

                foreach (var kvp in _modifiedBricks)
                {
                    Vector3 center = (Vector3)kvp.Key * brickWorldSize + size * 0.5f;
                    Gizmos.DrawWireCube(center, size * 0.9f);
                }
            }
        }
    }
}