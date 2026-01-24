using UnityEngine;
using System.Collections.Generic;
using VoxelEngine.Core.Generators; // For DynamicSDFManager
using VoxelEngine.Core.Editing; // For VoxelEditManager

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
        
        // --- ADDED: Debug List to visualize dirty chunks ---
        private List<Bounds> _debugDirtyChunkBounds = new List<Bounds>();

        private void Start()
        {
            _pool = GetComponent<VoxelVolumePool>();

            // [FIX] Auto-configure MaxDepth to match Global Voxel Size
            // We need the Leaf Node Voxel Size to equal VoxelEditManager.voxelSize (1.0)
            // Leaf Node Size = Resolution * GlobalVoxelSize
            // Octree Depth N Size = InitialWorldSize / 2^N
            // EQUATION: InitialWorldSize / 2^N = Resolution * GlobalVoxelSize
            // 2^N = InitialWorldSize / (Resolution * GlobalVoxelSize)
            // N = Log2(InitialWorldSize / (Resolution * GlobalVoxelSize))
            
            if (VoxelEditManager.Instance != null && _pool != null && _pool.prefab != null)
            {
                float globalVoxelSize = VoxelEditManager.Instance.voxelSize;
                float resolution = _pool.prefab.resolution;
                
                float targetLeafSize = resolution * globalVoxelSize;
                
                if (targetLeafSize > 0)
                {
                    float ratio = initialWorldSize / targetLeafSize;
                    int calculatedDepth = Mathf.RoundToInt(Mathf.Log(ratio, 2));
                    
                    if (calculatedDepth != maxDepth)
                    {
                        Debug.Log($"[WorldManager] Auto-adjusting MaxDepth from {maxDepth} to {calculatedDepth} to support editing (Target Leaf Size: {targetLeafSize}).");
                        maxDepth = calculatedDepth;
                    }
                }
            }

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
            if (viewer != null)
            {
                // Run the LOD Logic
                UpdateNodeLOD(_rootNode, viewer.position);
            }

            // Process Cache Invalidation
            ProcessDirtyRegions();
        }

        /// <summary>
        /// Checks for dirty SDF regions and regenerates affected VoxelVolumes.
        /// </summary>
        private void ProcessDirtyRegions()
        {
            // Clear visualization from previous frame
            _debugDirtyChunkBounds.Clear();

            if (DynamicSDFManager.Instance == null) return;

            // 1. Get dirty regions (this clears the list in the manager)
            List<Bounds> dirtyRegions = DynamicSDFManager.Instance.GetAndClearDirtyRegions();
            
            if (dirtyRegions == null || dirtyRegions.Count == 0) return;

            // 2. Get all currently active volumes
            var activeVolumes = VoxelVolumeRegistry.Volumes;
            
            // Optimization: Use a HashSet to avoid regenerating the same volume twice if it overlaps multiple dirty regions
            HashSet<VoxelVolume> volumesToUpdate = new HashSet<VoxelVolume>();

            // 3. Find Intersections
            // OPTIMIZATION: Loop through Active Volumes first. 
            // If a volume intersects ANY dirty region, mark it and stop checking that volume.
            for (int v = 0; v < activeVolumes.Count; v++)
            {
                VoxelVolume vol = activeVolumes[v];
                if (!vol.gameObject.activeInHierarchy) continue;

                for (int i = 0; i < dirtyRegions.Count; i++)
                {
                    if (vol.WorldBounds.Intersects(dirtyRegions[i]))
                    {
                        volumesToUpdate.Add(vol);
                        break; // Stop checking other regions for this volume
                    }
                }
            }

            // 4. Trigger Regeneration
            foreach (var vol in volumesToUpdate)
            {
                // Cache for visualization
                _debugDirtyChunkBounds.Add(vol.WorldBounds);
                vol.Regenerate();
            }
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
            if (drawDebugGizmos)
            {
                if (_rootNode != null) DrawNodeGizmos(_rootNode);

                // --- ADDED: Draw Dirty Chunks (RED) ---
                Gizmos.color = new Color(1, 0, 0, 0.8f); 
                foreach (var b in _debugDirtyChunkBounds)
                {
                    Gizmos.DrawWireCube(b.center, b.size);
                }
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