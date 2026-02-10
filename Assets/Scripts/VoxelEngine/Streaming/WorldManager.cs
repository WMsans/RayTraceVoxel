using UnityEngine;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using VoxelEngine.Core.Generators;
using VoxelEngine.Core.Editing;
using VoxelEngine.Physics;

namespace VoxelEngine.Core.Streaming
{
    [RequireComponent(typeof(VoxelVolumePool))]
    public class WorldManager : MonoBehaviour
    {
        public static WorldManager Instance { get; private set; }

        [Header("Configuration")]
        public int initialWorldSize = 1024;
        public int maxDepth = 4;
        public bool drawDebugGizmos = false;

        [Header("LOD Settings")]
        public Transform viewer;
        [Tooltip("Split if Distance < Size * SplitFactor")]
        public float splitFactor = 1.5f;
        [Tooltip("Merge if Distance > Size * MergeFactor")]
        public float mergeFactor = 1.8f;

        [Header("Culling Settings")]
        public Camera mainCamera;
        public float shadowDistance = 256f;
        public bool disableFrustumCulling = false;

        // --- Native Data ---
        private NativeList<OctreeNodeStruct> _nodeStructs;
        private NativeQueue<int> _splitQueue;
        private NativeQueue<int> _mergeQueue;
        private NativeList<int> _visibleIndices;
        private NativeList<int> _invisibleIndices;
        private NativeArray<BurstPlane> _burstPlanes;
        private Stack<int> _freeIndices;

        // --- Object Mapping ---
        // Maps struct index -> WorldOctreeNode object
        private List<WorldOctreeNode> _nodeObjects; 
        private WorldOctreeNode _rootNode;
        private VoxelVolumePool _pool;
        private Plane[] _frustumPlanes = new Plane[6];

        private void Awake()
        {
            if (Instance != null && Instance != this) Destroy(this);
            Instance = this;

            _nodeStructs = new NativeList<OctreeNodeStruct>(1000, Allocator.Persistent);
            _splitQueue = new NativeQueue<int>(Allocator.Persistent);
            _mergeQueue = new NativeQueue<int>(Allocator.Persistent);
            
            // Initialize with matching capacity to nodeStructs
            _visibleIndices = new NativeList<int>(1000, Allocator.Persistent);
            _invisibleIndices = new NativeList<int>(1000, Allocator.Persistent);
            
            _burstPlanes = new NativeArray<BurstPlane>(6, Allocator.Persistent);
            
            _nodeObjects = new List<WorldOctreeNode>(1000);
            _freeIndices = new Stack<int>();
        }

        private void Start()
        {
            if (VoxelPhysicsManager.Instance == null) gameObject.AddComponent<VoxelPhysicsManager>();
            _pool = GetComponent<VoxelVolumePool>();

            InitializePhysicsAndDepth();

            if (viewer == null && Camera.main != null) viewer = Camera.main.transform;

            // Create Root
            _rootNode = new WorldOctreeNode(Vector3.zero, initialWorldSize, 0, null);
            _rootNode.RequestGeneration(this);
        }

        private void InitializePhysicsAndDepth()
        {
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

            // Auto-configure MaxDepth based on voxel resolution
            if (VoxelEditManager.Instance != null && _pool != null && _pool.prefab != null)
            {
                float globalVoxelSize = VoxelEditManager.Instance.voxelSize;
                float resolution = _pool.prefab.resolution;
                float targetLeafSize = resolution * globalVoxelSize;

                if (targetLeafSize > 0)
                {
                    float ratio = initialWorldSize / targetLeafSize;
                    int calculatedDepth = Mathf.RoundToInt(Mathf.Log(ratio, 2));
                    if (calculatedDepth != maxDepth) maxDepth = calculatedDepth;
                }
            }
            
            if (physicsMan != null)
            {
                physicsMan.baseChunkSize = initialWorldSize / Mathf.Pow(2, maxDepth);
                physicsMan.viewer = this.viewer;
            }
        }

        private void Update()
        {
            if (viewer == null) return;
            if (mainCamera == null) mainCamera = Camera.main;

            // 1. Prepare Data
            if (mainCamera != null)
            {
                GeometryUtility.CalculateFrustumPlanes(mainCamera, _frustumPlanes);
                for (int i = 0; i < 6; i++) _burstPlanes[i] = _frustumPlanes[i];
            }

            // --- CRITICAL FIX: Ensure capacity BEFORE job schedule ---
            // NativeList.AddNoResize requires capacity to exist.
            int requiredCapacity = _nodeStructs.Length;
            if (_visibleIndices.Capacity < requiredCapacity) _visibleIndices.SetCapacity(requiredCapacity);
            if (_invisibleIndices.Capacity < requiredCapacity) _invisibleIndices.SetCapacity(requiredCapacity);

            _splitQueue.Clear();
            _mergeQueue.Clear();
            _visibleIndices.Clear();
            _invisibleIndices.Clear();

            // 2. Run Job
            var job = new OctreeTraversalJob
            {
                nodes = _nodeStructs.AsArray(),
                planes = _burstPlanes,
                viewerPos = viewer.position,
                shadowDistanceSq = shadowDistance * shadowDistance,
                splitFactor = splitFactor,
                mergeFactor = mergeFactor,
                maxDepth = maxDepth,
                cullEnabled = !disableFrustumCulling,
                splitQueue = _splitQueue.AsParallelWriter(),
                mergeQueue = _mergeQueue.AsParallelWriter(),
                visibleNodes = _visibleIndices.AsParallelWriter(),
                invisibleNodes = _invisibleIndices.AsParallelWriter()
            };

            JobHandle handle = job.Schedule(_nodeStructs.Length, 64);
            handle.Complete();

            // 3. Apply Results
            ApplyLODChanges();
            ApplyVisibility();
            
            ProcessDirtyRegions();
        }

        private void ApplyLODChanges()
        {
            // SPLITS
            while (_splitQueue.TryDequeue(out int index))
            {
                if (IsValidIndex(index))
                {
                    _nodeObjects[index].Subdivide();
                }
            }

            // MERGES
            while (_mergeQueue.TryDequeue(out int index))
            {
                if (IsValidIndex(index))
                {
                    _nodeObjects[index].Merge();
                }
            }
        }

        private void ApplyVisibility()
        {
            // Visible Nodes
            for (int i = 0; i < _visibleIndices.Length; i++)
            {
                int idx = _visibleIndices[i];
                if (!IsValidIndex(idx)) continue;

                var node = _nodeObjects[idx];
                
                if (node.IsLeaf)
                {
                    // If it's a leaf, ensure content is generated
                    if (node.ActiveVolume == null && node.State == NodeState.Uninitialized)
                    {
                         node.RequestGeneration(this);
                    }
                }
                else
                {
                    // [FIX]: Overlap Issue
                    // If this parent is subdivided (IsLeaf == false), we MUST ensure ALL children 
                    // are generated so the parent can deactivate. 
                    if (!node.AreChildrenReady && node.Children != null)
                    {
                        for (int c = 0; c < node.Children.Length; c++)
                        {
                            var child = node.Children[c];
                            // [FIX] Only request generation for LEAF children. 
                            // Branch children handle their own generation.
                            if (child.IsLeaf && child.State == NodeState.Uninitialized)
                            {
                                child.RequestGeneration(this);
                            }
                        }
                    }
                }

                // Physics & Enabling
                if (node.ActiveVolume != null)
                {                    
                    bool shouldBeActive = node.IsLeaf || !node.AreChildrenReady;

                    if (shouldBeActive) 
                    {
                        if (!node.ActiveVolume.gameObject.activeSelf) 
                            node.ActiveVolume.gameObject.SetActive(true);
                        VoxelPhysicsManager.Instance.Enqueue(node.ActiveVolume);
                    }
                    else
                    {
                        // Hidden (occluded by higher detail children)
                        if (node.ActiveVolume.gameObject.activeSelf) 
                            node.ActiveVolume.gameObject.SetActive(false);
                        
                        // Remove physics to prevent ghost collisions from hidden LoD
                        VoxelPhysicsManager.Instance.Remove(node.ActiveVolume);
                    }
                }
            }

            // Invisible Nodes (Culling)
            for (int i = 0; i < _invisibleIndices.Length; i++)
            {
                int idx = _invisibleIndices[i];
                if (!IsValidIndex(idx)) continue;

                var node = _nodeObjects[idx];
                
                // If we are invisible, we can aggressively clean up physics
                if (node.ActiveVolume != null)
                {
                    // Don't destroy content, just cull physics/rendering
                    VoxelPhysicsManager.Instance.Remove(node.ActiveVolume);
                    
                    // Optional: Disable GameObject to stop rendering cost
                    if (node.ActiveVolume.gameObject.activeSelf)
                        node.ActiveVolume.gameObject.SetActive(false);
                }
            }
        }

        private bool IsValidIndex(int index)
        {
            return index >= 0 && index < _nodeObjects.Count && _nodeObjects[index] != null;
        }

        // --- Node Pool Management (Called by WorldOctreeNode) ---

        public int RegisterNode(WorldOctreeNode node, Vector3 center, float size, int depth)
        {
            int index;
            if (_freeIndices.Count > 0)
            {
                index = _freeIndices.Pop();
                _nodeObjects[index] = node;
                _nodeStructs[index] = new OctreeNodeStruct 
                { 
                    center = center, size = size, depth = depth, isLeaf = true, isOccupied = true 
                };
            }
            else
            {
                index = _nodeObjects.Count;
                _nodeObjects.Add(node);
                _nodeStructs.Add(new OctreeNodeStruct 
                { 
                    center = center, size = size, depth = depth, isLeaf = true, isOccupied = true 
                });
            }
            return index;
        }

        public void UnregisterNode(int index)
        {
            if (index < 0 || index >= _nodeObjects.Count) return;

            _nodeObjects[index] = null;
            
            // Mark struct as unoccupied so Job skips it
            var s = _nodeStructs[index];
            s.isOccupied = false;
            _nodeStructs[index] = s;

            _freeIndices.Push(index);
        }

        public void UpdateNodeStruct(int index, bool isLeaf)
        {
            if (index < 0 || index >= _nodeStructs.Length) return;
            var s = _nodeStructs[index];
            s.isLeaf = isLeaf;
            _nodeStructs[index] = s;
        }

        // --- Dirty Regions (unchanged mostly) ---
        private void ProcessDirtyRegions()
        {
            if (DynamicSDFManager.Instance == null) return;
            List<Bounds> dirtyRegions = DynamicSDFManager.Instance.GetAndClearDirtyRegions();
            if (dirtyRegions == null || dirtyRegions.Count == 0) return;

            foreach (var vol in VoxelVolumeRegistry.Volumes)
            {
                if (!vol.gameObject.activeInHierarchy) continue;
                for (int i = 0; i < dirtyRegions.Count; i++)
                {
                    if (vol.WorldBounds.Intersects(dirtyRegions[i]))
                    {
                        vol.Regenerate();
                        VoxelPhysicsManager.Instance.Enqueue(vol);
                        break;
                    }
                }
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_nodeStructs.IsCreated) _nodeStructs.Dispose();
            if (_splitQueue.IsCreated) _splitQueue.Dispose();
            if (_mergeQueue.IsCreated) _mergeQueue.Dispose();
            if (_visibleIndices.IsCreated) _visibleIndices.Dispose();
            if (_invisibleIndices.IsCreated) _invisibleIndices.Dispose();
            if (_burstPlanes.IsCreated) _burstPlanes.Dispose();
            
            if (_rootNode != null)
            {
                _rootNode.Merge();
                _rootNode.DisableVolume();
            }
        }

        private void OnDrawGizmos()
        {
            if (drawDebugGizmos && _nodeStructs.IsCreated)
            {
                Gizmos.color = Color.green;
                foreach (var node in _nodeStructs)
                {
                    if (node.isOccupied && node.isLeaf)
                        Gizmos.DrawWireCube(node.center, Vector3.one * node.size);
                }
            }
        }
    }
}