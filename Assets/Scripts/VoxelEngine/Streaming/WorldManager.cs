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

            // Auto-configure MaxDepth to match Global Voxel Size
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

            // Calculate Target Leaf Size
            _targetLeafSize = initialWorldSize / Mathf.Pow(2, maxDepth);

            // This ensures the Physics Manager knows the base size for calculating dynamic stride
            if (physicsMan != null)
            {
                physicsMan.baseChunkSize = _targetLeafSize;
                // If viewer is null here, it will be found below, so we can re-assign if needed,
                // but usually assigning what we have is good.
                physicsMan.viewer = this.viewer;
            }

            if (viewer == null && Camera.main != null) 
            {
                viewer = Camera.main.transform;
                // Re-assign to physics manager just in case
                if (physicsMan != null) physicsMan.viewer = viewer;
            }
            
            _rootNode = new WorldOctreeNode(Vector3.zero, initialWorldSize, 0, null);
            _rootNode.EnableVolume(this.transform);
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
                
                // Enqueue all regenerated chunks for physics updates, regardless of LOD
                // The PhysicsManager will decide the appropriate resolution.
                VoxelPhysicsManager.Instance.Enqueue(vol);
            }
        }

        private void UpdateNodeLOD(WorldOctreeNode node, Vector3 viewerPosition)
        {
            float distance = Vector3.Distance(viewerPosition, node.Center);
            bool inFrustum = (mainCamera == null) || GeometryUtility.TestPlanesAABB(_frustumPlanes, node.Bounds);
            Vector3 closest = node.Bounds.ClosestPoint(viewerPosition);
            bool inShadowRange = (closest - viewerPosition).sqrMagnitude < (shadowDistance * shadowDistance);

            if (node.IsLeaf)
            {
                if (!inFrustum && !inShadowRange)
                {
                    if (node.ActiveVolume != null)
                    {
                        VoxelPhysicsManager.Instance.ClearCollider(node.ActiveVolume);
                        VoxelPhysicsManager.Instance.Remove(node.ActiveVolume);
                        node.DisableVolume();
                    }
                }
                else 
                {
                    if (node.ActiveVolume == null)
                    {
                        node.EnableVolume(this.transform);
                        // Register for Physics - PhysicsManager handles LOD resolution now
                        if (node.ActiveVolume != null)
                        {
                            VoxelPhysicsManager.Instance.Enqueue(node.ActiveVolume);
                        }
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
                child.EnableVolume(this.transform);
                // Register children for physics
                if (child.ActiveVolume != null)
                {
                    VoxelPhysicsManager.Instance.Enqueue(child.ActiveVolume);
                }
            }
            node.DisableVolume();
        }

        private void MergeNode(WorldOctreeNode node)
        {
            node.EnableVolume(this.transform);
            
            // Register parent (Low LOD) for physics
            // PhysicsManager will see it has a large WorldSize and use a high stride (low res mesh)
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

                Gizmos.color = new Color(1, 0, 0, 0.8f); 
                foreach (var b in _debugDirtyChunkBounds)
                {
                    Gizmos.DrawWireCube(b.center, b.size);
                }

                if (viewer != null)
                {
                    Gizmos.color = new Color(1, 1, 0, 0.3f);
                    Gizmos.DrawWireSphere(viewer.position, shadowDistance);
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