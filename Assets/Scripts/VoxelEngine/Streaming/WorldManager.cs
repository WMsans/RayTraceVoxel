using UnityEngine;
using System.Collections.Generic;
using VoxelEngine.Core.Generators;
using VoxelEngine.Core.Editing;
using VoxelEngine.Physics;

namespace VoxelEngine.Core.Streaming
{
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

        [Header("Culling Settings")]
        public Camera mainCamera;
        public float shadowDistance = 256f;
        public bool disableFrustumCulling = false; // Added debug option
        
        private WorldOctreeNode _rootNode;
        private VoxelVolumePool _pool;
        private float _targetLeafSize;
        private Plane[] _frustumPlanes = new Plane[6];
        
        private List<Bounds> _debugDirtyChunkBounds = new List<Bounds>();

        private void Start()
        {
            if (VoxelPhysicsManager.Instance == null)
            {
                gameObject.AddComponent<VoxelPhysicsManager>();
            }

            _pool = GetComponent<VoxelVolumePool>();

            // Auto-configure Physics Manager from Prefab if needed
            var physicsMan = VoxelPhysicsManager.Instance;
            if (physicsMan.physicsShader == null && _pool != null && _pool.prefab != null)
            {
                var baker = _pool.prefab.GetComponent<VoxelPhysicsBaker>();
                if (baker != null)
                {
                    physicsMan.physicsShader = baker.physicsShader;
                    physicsMan.stride = baker.stride;
                    physicsMan.maxVertices = baker.maxVertices;
                }
            }

            // Auto-configure MaxDepth
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
                        maxDepth = calculatedDepth;
                    }
                }
            }

            _targetLeafSize = initialWorldSize / Mathf.Pow(2, maxDepth);

            if (physicsMan != null)
            {
                physicsMan.baseChunkSize = _targetLeafSize;
                physicsMan.viewer = this.viewer;
            }

            if (viewer == null && Camera.main != null) 
            {
                viewer = Camera.main.transform;
                if (physicsMan != null) physicsMan.viewer = viewer;
            }
            
            _rootNode = new WorldOctreeNode(Vector3.zero, initialWorldSize, 0, null);
            _rootNode.RequestGeneration(this);
        }

        private void Update()
        {
            if (viewer != null)
            {
                if (mainCamera == null) mainCamera = Camera.main;
                if (mainCamera != null)
                {
                    GeometryUtility.CalculateFrustumPlanes(mainCamera, _frustumPlanes);
                }

                UpdateNodeLOD(_rootNode, viewer.position);
            }

            ProcessDirtyRegions();
        }

        private void ProcessDirtyRegions()
        {
            _debugDirtyChunkBounds.Clear();
            if (DynamicSDFManager.Instance == null) return;

            List<Bounds> dirtyRegions = DynamicSDFManager.Instance.GetAndClearDirtyRegions();
            if (dirtyRegions == null || dirtyRegions.Count == 0) return;

            // Simple brute force invalidation for now. 
            // Ideally we traverse the octree to find intersecting leaves.
            // Using Registry for simplicity but it only holds ACTIVE (Complex) volumes.
            // Hidden "Empty" or "Solid" nodes might need to become Complex.
            // For now, only updating active volumes.
            // *Improvement*: You would want to recursively check the octree nodes against bounds
            // and set State = Uninitialized to force re-audit.

            var activeVolumes = VoxelVolumeRegistry.Volumes;
            HashSet<VoxelVolume> volumesToUpdate = new HashSet<VoxelVolume>();

            for (int v = 0; v < activeVolumes.Count; v++)
            {
                VoxelVolume vol = activeVolumes[v];
                if (!vol.gameObject.activeInHierarchy) continue;

                for (int i = 0; i < dirtyRegions.Count; i++)
                {
                    if (vol.WorldBounds.Intersects(dirtyRegions[i]))
                    {
                        volumesToUpdate.Add(vol);
                        break; 
                    }
                }
            }

            foreach (var vol in volumesToUpdate)
            {
                _debugDirtyChunkBounds.Add(vol.WorldBounds);
                vol.Regenerate();
                VoxelPhysicsManager.Instance.Enqueue(vol);
            }
        }

        private void UpdateNodeLOD(WorldOctreeNode node, Vector3 viewerPosition)
        {
            float distance = Vector3.Distance(viewerPosition, node.Center);
            
            // Modified to respect disableFrustumCulling flag
            bool inFrustum = disableFrustumCulling || (mainCamera == null) || GeometryUtility.TestPlanesAABB(_frustumPlanes, node.Bounds);
            
            Vector3 closest = node.Bounds.ClosestPoint(viewerPosition);
            bool inShadowRange = (closest - viewerPosition).sqrMagnitude < (shadowDistance * shadowDistance);

            if (node.IsLeaf)
            {
                if (!inFrustum && !inShadowRange)
                {
                    // Cull: If active, disable. If not active, do nothing.
                    if (node.ActiveVolume != null)
                    {
                        VoxelPhysicsManager.Instance.ClearCollider(node.ActiveVolume);
                        VoxelPhysicsManager.Instance.Remove(node.ActiveVolume);
                        node.DisableVolume();
                    }
                }
                else 
                {
                    // Visible: Ensure content is generated
                    if (node.ActiveVolume == null)
                    {
                        // Use the new State flow
                        if (node.State == NodeState.Uninitialized)
                        {
                            node.RequestGeneration(this);
                        }
                        // If State is Empty or Solid, we do nothing (invisible).
                        // If State is Pending, we wait.
                        
                        // Register for Physics if we just got a volume
                        if (node.ActiveVolume != null)
                        {
                            VoxelPhysicsManager.Instance.Enqueue(node.ActiveVolume);
                        }
                    }
                    else if (node.State == NodeState.Active)
                    {
                        // Ensure it's active in physics if we came back into view
                        // The PhysicsManager handles deduplication
                        VoxelPhysicsManager.Instance.Enqueue(node.ActiveVolume);
                    }
                }

                if (node.Depth < maxDepth && distance < (node.Size * splitFactor))
                {
                    if (inFrustum || inShadowRange)
                    {
                        SplitNode(node);
                    }
                }
            }
            else // Branch
            {
                bool shouldMerge = distance > (node.Size * mergeFactor) || (!inFrustum && !inShadowRange);
                
                if (shouldMerge)
                {
                    MergeNode(node);
                }
                else
                {
                    foreach (var child in node.Children)
                    {
                        UpdateNodeLOD(child, viewerPosition);
                    }
                }
            }
        }

        private void SplitNode(WorldOctreeNode node)
        {
            node.Subdivide();
            foreach (var child in node.Children)
            {
                child.RequestGeneration(this);
                if (child.ActiveVolume != null)
                {
                    VoxelPhysicsManager.Instance.Enqueue(child.ActiveVolume);
                }
            }
            node.DisableVolume();
        }

        private void MergeNode(WorldOctreeNode node)
        {
            node.RequestGeneration(this);
            
            if (node.ActiveVolume != null)
            {
                VoxelPhysicsManager.Instance.Enqueue(node.ActiveVolume);
            }

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
        
        private void OnDrawGizmos()
        {
            if (drawDebugGizmos)
            {
                if (_rootNode != null) DrawNodeGizmos(_rootNode);
                // ... dirty regions ...
            }
        }

        private void DrawNodeGizmos(WorldOctreeNode node)
        {
            if (node.IsLeaf)
            {
                // Color code by state
                switch (node.State)
                {
                    case NodeState.Active: Gizmos.color = Color.green; break;
                    case NodeState.Empty: Gizmos.color = new Color(0, 1, 0, 0.1f); break;
                    case NodeState.Solid: Gizmos.color = new Color(0.5f, 0.2f, 0, 0.5f); break;
                    case NodeState.Pending: Gizmos.color = Color.yellow; break;
                    default: Gizmos.color = Color.grey; break;
                }

                if (node.State == NodeState.Active || drawDebugGizmos)
                {
                    Gizmos.DrawWireCube(node.Center, Vector3.one * node.Size);
                }
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