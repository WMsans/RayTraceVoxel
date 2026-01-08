using UnityEngine;
using VoxelEngine.Core;

namespace VoxelEngine.Core.Streaming
{
    /// <summary>
    /// Represents a node in the infinite world octree (CPU-side).
    /// Manages spatial data and holds a reference to a physical VoxelVolume if active.
    /// </summary>
    public class WorldOctreeNode
    {
        // --- Properties ---
        public Vector3 Center { get; private set; }
        public float Size { get; private set; }
        public int Depth { get; private set; }
        
        // --- Hierarchy ---
        public WorldOctreeNode Parent { get; private set; }
        public WorldOctreeNode[] Children { get; private set; }
        public bool IsLeaf => Children == null;

        // --- Payload ---
        /// <summary>
        /// The active VoxelVolume MonoBehaviour managed by this node (if Leaf).
        /// </summary>
        public VoxelVolume ActiveVolume { get; private set; }

        // --- Constants ---
        // Normalized offsets for 8 octants (x, y, z)
        private static readonly Vector3[] ChildOffsets = new Vector3[]
        {
            new Vector3(-1, -1, -1), new Vector3(1, -1, -1),
            new Vector3(-1, 1, -1),  new Vector3(1, 1, -1),
            new Vector3(-1, -1, 1),  new Vector3(1, -1, 1),
            new Vector3(-1, 1, 1),   new Vector3(1, 1, 1)
        };

        public WorldOctreeNode(Vector3 center, float size, int depth, WorldOctreeNode parent)
        {
            Center = center;
            Size = size;
            Depth = depth;
            Parent = parent;
        }

        public void Subdivide()
        {
            if (!IsLeaf) return;
            Children = new WorldOctreeNode[8];
            float quarterSize = Size * 0.25f;
            float childSize = Size * 0.5f;

            for (int i = 0; i < 8; i++)
            {
                Vector3 childPos = Center + (ChildOffsets[i] * quarterSize);
                Children[i] = new WorldOctreeNode(childPos, childSize, Depth + 1, this);
            }
        }

        public void Merge()
        {
            if (IsLeaf) return;
            foreach (var child in Children)
            {
                child.Merge(); 
                child.DisableVolume();
            }
            Children = null;
        }

        // --- Volume Management (UPDATED) ---

        public void EnableVolume(Transform container)
        {
            if (ActiveVolume != null) return; 

            if (VoxelVolumePool.Instance == null)
            {
                Debug.LogError("WorldOctreeNode: Pool not found!");
                return;
            }

            // Calculate Min Corner for the Volume origin
            Vector3 minCorner = Center - (Vector3.one * Size * 0.5f);
            
            // Request from Pool
            ActiveVolume = VoxelVolumePool.Instance.GetVolume(minCorner, Size);
            
            if (ActiveVolume != null)
            {
                ActiveVolume.name = $"Volume_D{Depth}_{Center}";
            }
        }

        public void DisableVolume()
        {
            if (ActiveVolume == null) return;

            // Return to Pool
            if (VoxelVolumePool.Instance != null)
            {
                VoxelVolumePool.Instance.ReturnVolume(ActiveVolume);
            }
            
            ActiveVolume = null;
        }
    }
}