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

        /// <summary>
        /// Splits this node into 8 children.
        /// </summary>
        public void Subdivide()
        {
            if (!IsLeaf) return; // Already subdivided

            Children = new WorldOctreeNode[8];
            float quarterSize = Size * 0.25f; // Distance from center to child center
            float childSize = Size * 0.5f;

            for (int i = 0; i < 8; i++)
            {
                Vector3 childPos = Center + (ChildOffsets[i] * quarterSize);
                Children[i] = new WorldOctreeNode(childPos, childSize, Depth + 1, this);
            }
        }

        /// <summary>
        /// Removes all children, effectively making this node a leaf again.
        /// </summary>
        public void Merge()
        {
            if (IsLeaf) return;

            // Recursively clean up children
            foreach (var child in Children)
            {
                child.Merge(); // Ensure children merge their own descendants first
                child.DisableVolume(); // Destroy volume if it exists
            }

            Children = null;
        }

        // --- Volume Management ---

        /// <summary>
        /// Instantiates (or pools) a VoxelVolume for this node.
        /// </summary>
        /// <param name="prefab">The VoxelVolume prefab to spawn.</param>
        /// <param name="container">Transform parent for organization.</param>
        public void EnableVolume(VoxelVolume prefab, Transform container)
        {
            if (ActiveVolume != null) return; // Already active

            // Instantiate
            ActiveVolume = Object.Instantiate(prefab, container);
            
            // 1. Translation: Position the volume.
            // VoxelVolumes typically pivot at (0,0,0) (min corner) in their local space.
            // The Node is defined by Center. We calculate the Min Corner.
            Vector3 minCorner = Center - (Vector3.one * Size * 0.5f);
            ActiveVolume.transform.position = minCorner;

            // 2. Scale: Match the physical Size of the Node.
            // Assuming the VoxelVolume has a 'Resolution' (e.g., 64).
            // Default size is 64 units. We need to scale it to 'Size'.
            float scaleFactor = Size / ActiveVolume.Resolution;
            ActiveVolume.transform.localScale = Vector3.one * scaleFactor;

            ActiveVolume.name = $"Volume_D{Depth}_{Center}";
            
            // Note: If you have specific initialization logic (like setting data), do it here.
        }

        /// <summary>
        /// Destroys (or returns to pool) the active VoxelVolume.
        /// </summary>
        public void DisableVolume()
        {
            if (ActiveVolume == null) return;

            // In a real streaming scenario, use an ObjectPool here.
            Object.Destroy(ActiveVolume.gameObject);
            ActiveVolume = null;
        }
    }
}