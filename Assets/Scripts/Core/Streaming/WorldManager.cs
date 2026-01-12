using UnityEngine;

namespace VoxelEngine.Core.Streaming
{
    // Require the pool to be present
    [RequireComponent(typeof(VoxelVolumePool))]
    public class WorldManager : MonoBehaviour
    {
        [Header("Configuration")]
        public int initialWorldSize = 1024;
        public int maxDepth = 4;
        public bool drawDebugGizmos = false;

        [Header("LOD Settings")]
        public Transform viewer;
        [Tooltip("Split if Distance < Size * SplitFactor")]
        public float splitFactor = 1.5f;
        [Tooltip("Merge if Distance > Size * MergeFactor. Must be > SplitFactor to prevent flickering.")]
        public float mergeFactor = 1.8f;
        
        private WorldOctreeNode _rootNode;
        private VoxelVolumePool _pool;

        private void Start()
        {
            _pool = GetComponent<VoxelVolumePool>();

            // Auto-find viewer if not assigned (usually Main Camera)
            if (viewer == null && Camera.main != null) 
                viewer = Camera.main.transform;
            
            // Initialize Root Node at (0,0,0)
            _rootNode = new WorldOctreeNode(Vector3.zero, initialWorldSize, 0, null);

            // Initially enable the root volume
            _rootNode.EnableVolume(this.transform);
        }

        private void Update()
        {
            if (viewer == null) return;
            
            // Run the LOD Logic
            UpdateNodeLOD(_rootNode, viewer.position);
        }

        /// <summary>
        /// Recursive function to traverse the tree and apply Split/Merge logic.
        /// </summary>
        private void UpdateNodeLOD(WorldOctreeNode node, Vector3 viewerPosition)
        {
            float distance = Vector3.Distance(viewerPosition, node.Center);

            if (node.IsLeaf)
            {
                // --- SPLIT CHECK ---
                // 1. Can we go deeper? (Depth < maxDepth)
                // 2. Are we close enough? (Distance < Size * Factor)
                if (node.Depth < maxDepth && distance < (node.Size * splitFactor))
                {
                    SplitNode(node);
                }
            }
            else // Node is a Branch (has children)
            {
                // --- MERGE CHECK ---
                // 1. Are we far enough? (Distance > Size * Factor)
                if (distance > (node.Size * mergeFactor))
                {
                    MergeNode(node);
                }
                else
                {
                    // If we don't merge, we must check the children
                    foreach (var child in node.Children)
                    {
                        UpdateNodeLOD(child, viewerPosition);
                    }
                }
            }
        }

        private void SplitNode(WorldOctreeNode node)
        {
            // 1. Create child nodes (CPU logic)
            node.Subdivide();

            // 2. Acquire 8 VoxelVolumes from the pool for the new children
            foreach (var child in node.Children)
            {
                // This triggers GetVolume -> OnPullFromPool -> Generate(SDF)
                child.EnableVolume(this.transform);
            }

            // 3. Hide/Return the parent VoxelVolume
            // We no longer need the low-res parent since high-res children are now active
            node.DisableVolume();
        }

        private void MergeNode(WorldOctreeNode node)
        {
            // 1. Acquire 1 VoxelVolume for the parent (Low LOD)
            // This generates the low-resolution representation of the large area
            node.EnableVolume(this.transform);

            // 2. Hide/Return the 8 child VoxelVolumes and destroy child nodes
            // WorldOctreeNode.Merge() recursively calls DisableVolume() on children
            node.Merge();
        }

        private void OnDestroy()
        {
            if (_rootNode != null)
            {
                _rootNode.Merge();
                _rootNode.DisableVolume();
            }
        }
        
        // Debug Gizmos to visualize the octree
        private void OnDrawGizmos()
        {
            if (drawDebugGizmos && _rootNode != null)
            {
                DrawNodeGizmos(_rootNode);
            }
        }

        private void DrawNodeGizmos(WorldOctreeNode node)
        {
            if (node.IsLeaf)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireCube(node.Center, Vector3.one * node.Size);
            }
            else
            {
                if (node.Children != null)
                {
                    foreach (var child in node.Children)
                        DrawNodeGizmos(child);
                }
            }
        }
    }
}