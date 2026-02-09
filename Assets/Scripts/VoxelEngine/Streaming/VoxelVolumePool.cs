using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Runtime.InteropServices;
using VoxelEngine.Core.Buffers;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Memory;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace VoxelEngine.Core.Streaming
{
    public struct ChunkDef
    {
        public Vector3 boundsMin;
        public uint nodeOffset;
        public Vector3 boundsMax;
        public uint payloadOffset;
        public uint brickDataOffset; 
        public Vector3 padding; 
        public Matrix4x4 worldToLocal;
        public Matrix4x4 localToWorld;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TLASCell
    {
        public uint offset;
        public uint count;
    }

    public enum AuditResultType { Retry, Empty, Solid, Complex }

    public struct AuditResult
    {
        public AuditResultType type;
        public VoxelVolume volume; // Only valid if Complex
    }

    public class VoxelVolumePool : MonoBehaviour
    {
        public static VoxelVolumePool Instance { get; private set; }
        public VoxelVolume prefab;
        public int poolSize = 100;
        public int transientPoolSize = 5; // Reserve for auditing
        public Transform poolContainer;
        public int maxNodesPerVolume = 50000; 
        public int maxBricksPerVolume = 25000; 

        public GraphicsBuffer GlobalNodeBuffer { get; private set; }
        public GraphicsBuffer GlobalPayloadBuffer { get; private set; }
        public GraphicsBuffer GlobalBrickDataBuffer { get; private set; }
        public GraphicsBuffer GlobalPageTableBuffer { get; private set; } 
        
        public GraphicsBuffer ChunkBuffer { get; private set; }

        // --- TLAS Buffers ---
        public GraphicsBuffer TLASGridBuffer { get; private set; }
        public GraphicsBuffer TLASChunkIndexBuffer { get; private set; }
        public Vector3 TLASBoundsMin { get; private set; }
        public Vector3 TLASBoundsMax { get; private set; }
        public int TLASResolution = 16;
        
        // Native Arrays for Burst
        private NativeArray<ChunkDef> _chunkData;
        private NativeArray<TLASCell> _tlasGrid;
        private NativeArray<int> _tlasIndices;
        private const int MAX_TLAS_INDICES = 262144; 

        private Queue<VoxelVolume> _pool = new Queue<VoxelVolume>();
        private Queue<VoxelVolume> _transientPool = new Queue<VoxelVolume>(); // Transient Auditor Pool

        private List<VoxelVolume> _activeVolumes = new List<VoxelVolume>();
        private List<VoxelVolume> _visibleVolumes = new List<VoxelVolume>();
        public IReadOnlyList<VoxelVolume> VisibleVolumes => _visibleVolumes;
        
        private PhysicalPageAllocator _nodeAllocator; 
        private VoxelMemoryAllocator _pageTableAllocator; 
        private VoxelMemoryAllocator _brickAllocator;
        
        public int VisibleChunkCount => _visibleVolumes.Count;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            InitializeGlobalBuffers();
            InitializePool();
            InitializeTransientPool();
        }

        private void InitializeGlobalBuffers()
        {
            int totalNodes = (poolSize + transientPoolSize) * maxNodesPerVolume;
            int pageSize = SVONode.PAGE_SIZE;
            if (totalNodes % pageSize != 0) totalNodes = ((totalNodes / pageSize) + 1) * pageSize;
            
            int totalPages = totalNodes / pageSize;

            int totalBricks = (poolSize + transientPoolSize) * maxBricksPerVolume; 
            int totalBrickVoxels = totalBricks * SVONode.BRICK_VOXEL_COUNT;

            Debug.Log($"Allocating Global Voxel Memory: {totalNodes/1000}k Nodes ({totalPages} Pages), {totalBricks/1000}k Bricks. BrickData: {totalBrickVoxels * 4 / 1024 / 1024} MB");

            GlobalNodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalNodes, Marshal.SizeOf<SVONode>());
            GlobalPayloadBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalNodes, Marshal.SizeOf<VoxelPayload>());
            GlobalBrickDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalBrickVoxels, sizeof(uint));
            GlobalPageTableBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalPages, sizeof(uint));
            
            _nodeAllocator = new PhysicalPageAllocator(totalPages, pageSize);
            _pageTableAllocator = new VoxelMemoryAllocator(totalPages); 
            _brickAllocator = new VoxelMemoryAllocator(totalBrickVoxels);

            ChunkBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, poolSize, Marshal.SizeOf<ChunkDef>());
            
            // Native Allocations
            _chunkData = new NativeArray<ChunkDef>(poolSize, Allocator.Persistent);
            _tlasGrid = new NativeArray<TLASCell>(TLASResolution * TLASResolution * TLASResolution, Allocator.Persistent);
            _tlasIndices = new NativeArray<int>(MAX_TLAS_INDICES, Allocator.Persistent);

            // Initialize TLAS Buffers
            int tlasSize = TLASResolution * TLASResolution * TLASResolution;
            TLASGridBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, tlasSize, Marshal.SizeOf<TLASCell>());
            TLASChunkIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MAX_TLAS_INDICES, 4); 
        }

        private void InitializePool()
        {
            if (prefab == null) return;
            if (poolContainer == null) poolContainer = this.transform;

            for (int i = 0; i < poolSize; i++)
            {
                VoxelVolume vol = Instantiate(prefab, poolContainer);
                vol.gameObject.name = $"Volume_Pool_{i}";
                vol.gameObject.SetActive(false);
                _pool.Enqueue(vol);
            }
        }

        private void InitializeTransientPool()
        {
            // Transient volumes hold onto their memory permanently to avoid allocation churn
            for (int i = 0; i < transientPoolSize; i++)
            {
                CreateTransientVolume();
            }
        }

        private bool CreateTransientVolume()
        {
            VoxelVolume vol = null;
            
            // Prefer taking from main pool to keep hierarchy clean, otherwise instantiate
            if (_pool.Count > 0)
            {
                vol = _pool.Dequeue();
            }
            else
            {
                vol = Instantiate(prefab, poolContainer);
            }
            
            vol.gameObject.name = $"Volume_Transient";
            vol.gameObject.SetActive(false);

            // Pre-allocate memory
            if (AllocateVolumeMemory(maxNodesPerVolume, maxBricksPerVolume, out int[] pages, out int ptOffset, out int brickOffset))
            {
                vol.AssignMemorySlice(this, 0, 0, brickOffset, maxNodesPerVolume, maxBricksPerVolume, ptOffset, pages);
                vol.IsTransient = true;
                _transientPool.Enqueue(vol);
                return true;
            }
            else
            {
                // [FIX] Removed LogError to prevent spam, this is a valid state when full.
                // Cleanup if we pulled from pool
                if (vol != null) 
                {
                    if (_pool != null) _pool.Enqueue(vol); 
                    else Destroy(vol.gameObject);
                }
                return false;
            }
        }

        private void EnsureTransientPool()
        {
            while (_transientPool.Count < transientPoolSize)
            {
                // If we fail to create one (Memory Full), stop trying for now.
                if (!CreateTransientVolume()) break; 
            }
        }

        private bool AllocateVolumeMemory(int requestedNodes, int requestedBricks, out int[] pages, out int pageTableOffset, out int brickOffset)
        {
            pages = null; pageTableOffset = -1; brickOffset = -1;
            int pageSize = SVONode.PAGE_SIZE;
            int pagesNeeded = Mathf.CeilToInt((float)requestedNodes / pageSize);
            
            if (!_nodeAllocator.Allocate(pagesNeeded, out pages)) return false;
            
            if (!_pageTableAllocator.Allocate(pagesNeeded, out pageTableOffset))
            {
                _nodeAllocator.Free(pages);
                return false;
            }
            
            // Update Page Table
            int[] pageTableData = new int[pagesNeeded];
            for (int i = 0; i < pagesNeeded; i++) pageTableData[i] = pages[i] * pageSize;
            GlobalPageTableBuffer.SetData(pageTableData, 0, pageTableOffset, pagesNeeded);

            int brickVoxels = requestedBricks * SVONode.BRICK_VOXEL_COUNT;
            if (!_brickAllocator.Allocate(brickVoxels, out brickOffset))
            {
                _nodeAllocator.Free(pages);
                _pageTableAllocator.Free(pageTableOffset, pagesNeeded);
                return false;
            }

            return true;
        }

        // --- Audit / Transient Logic ---

        public void AuditChunk(Vector3 position, float size, int resolution, Action<AuditResult> onComplete)
        {
            if (_transientPool.Count == 0)
            {
                EnsureTransientPool();

                if (_transientPool.Count == 0)
                {
                    // Retry next frame
                    onComplete?.Invoke(new AuditResult { type = AuditResultType.Retry });
                    return;
                }
            }

            VoxelVolume vol = _transientPool.Dequeue();
            // Configure transient volume
            vol.transform.position = position;
            vol.transform.localScale = Vector3.one * (size / resolution);
            vol.resolution = resolution;
            
            // Run Generation
            vol.OnPullFromPool(position, size, false);
            
            // Hide to prevent flash
            vol.gameObject.SetActive(false);
            
            StartCoroutine(AuditRoutine(vol, position, size, resolution, onComplete));
        }

        private IEnumerator AuditRoutine(VoxelVolume vol, Vector3 pos, float size, int res, Action<AuditResult> onComplete)
        {
            // 1. Read Counters
            var req = AsyncGPUReadback.Request(vol.CounterBuffer);
            yield return new WaitUntil(() => req.done);

            if (req.hasError)
            {
                ReturnTransient(vol);
                onComplete?.Invoke(new AuditResult { type = AuditResultType.Retry });
                yield break;
            }

            var data = req.GetData<uint>();
            int nodeCount = (int)data[0];
            int payloadCount = (int)data[1];
            int brickVoxelCount = (int)data[2];

            // Case A: Empty
            if (nodeCount == 0)
            {
                ReturnTransient(vol);
                onComplete?.Invoke(new AuditResult { type = AuditResultType.Empty });
            }
            // Case B: Solid (Heuristic: 1 Node usually means Root only, and if not empty, it's solid)
            else if (nodeCount == 1)
            {
                ReturnTransient(vol);
                onComplete?.Invoke(new AuditResult { type = AuditResultType.Solid });
            }
            // Case B2: Complex but Empty (Structure without content)
            // Fixes issue where empty chunks with slight node structure took up volumes
            else if (payloadCount == 0 && brickVoxelCount == 0)
            {
                ReturnTransient(vol);
                onComplete?.Invoke(new AuditResult { type = AuditResultType.Empty });
            }
            // Case C: Complex / Surface
            else
            {
                // FIX: Promote the Transient Volume to Permanent immediately.
                // Do NOT attempt to read back nodes/payloads and copy them.
                // The data is paged/scattered in global memory, so linear readback fails (returns zeros).
                // By promoting the volume, we keep the valid memory allocation.

                vol.IsTransient = false;
                vol.gameObject.name = $"Volume_Active_{pos}";
                
                // Add to active set
                _activeVolumes.Add(vol);
                UpdateChunkBuffer(null, default, 0f); // Ensure it gets into the render buffer
                
                // Ensure it's visible now that it's ready
                vol.gameObject.SetActive(true);

                // IMPORTANT: We used up a transient volume, so we must replace it
                CreateTransientVolume(); 

                onComplete?.Invoke(new AuditResult { type = AuditResultType.Complex, volume = vol });
            }
        }

        private void ReturnTransient(VoxelVolume vol)
        {
            vol.OnReturnToPool();
            _transientPool.Enqueue(vol);
        }

        // --- Main Pool Logic ---

        public VoxelVolume GetVolume(Vector3 position, float size, int requestedNodes = -1, int requestedBricks = -1, int resolution = -1, bool generateEmpty = false, bool skipGeneration = false)
        {
            if (_pool.Count == 0) return null;
            
            if (requestedNodes < 0) requestedNodes = maxNodesPerVolume;
            if (requestedBricks < 0) requestedBricks = maxBricksPerVolume;
            
            if (AllocateVolumeMemory(requestedNodes, requestedBricks, out int[] pages, out int ptOffset, out int brickOffset))
            {
                VoxelVolume vol = _pool.Dequeue();
                vol.AssignMemorySlice(this, 0, 0, brickOffset, requestedNodes, requestedBricks, ptOffset, pages);

                // Configure volume
                vol.transform.position = position;
                float scale = size / vol.Resolution; 
                if (resolution > 0)
                {
                    vol.resolution = resolution;
                    scale = size / resolution;
                }
                vol.transform.localScale = Vector3.one * scale;
                
                // Pass flags
                vol.OnPullFromPool(position, size, generateEmpty, skipGeneration);
                
                _activeVolumes.Add(vol);
                UpdateChunkBuffer(null, default, 0f);
                return vol;
            }
            else
            {
                Debug.LogWarning("VoxelVolumePool: Failed to allocate pages for GetVolume.");
                return null;
            }
        }

        public void ReturnVolume(VoxelVolume vol)
        {
            if (vol == null) return;
            
            // Whether it came from GetVolume or Promotion, it's in _activeVolumes
            if (_activeVolumes.Remove(vol))
            {
                if (vol.IsReady)
                {
                    _nodeAllocator.Free(vol.AllocatedPages);
                    _pageTableAllocator.Free(vol.BufferManager.PageTableOffset, vol.AllocatedPages.Length);
                    _brickAllocator.Free(vol.BufferManager.BrickDataOffset, vol.MaxBricks * SVONode.BRICK_VOXEL_COUNT);
                }
                
                vol.OnReturnToPool();

                MeshCollider meshCollider = vol.meshCol;
                if (meshCollider != null) meshCollider.sharedMesh = null;
                var MeshFilter = vol.meshFil;
                if(MeshFilter != null) MeshFilter.mesh = null;

                vol.transform.SetParent(poolContainer); 
                _pool.Enqueue(vol);
                UpdateChunkBuffer(null, default, 0f);

                EnsureTransientPool();
            }
        }

        public void UpdateVisibility(Plane[] cullingPlanes, Vector3 viewerPos = default, float shadowDistance = 0f)
        {
            UpdateChunkBuffer(cullingPlanes, viewerPos, shadowDistance);
        }

        private void UpdateChunkBuffer(Plane[] cullingPlanes, Vector3 viewerPos, float shadowDistance)
        {
            _visibleVolumes.Clear();
            int writeIndex = 0;
            float shadowDistSqr = shadowDistance * shadowDistance;

            for (int i = 0; i < _activeVolumes.Count; i++)
            {
                var vol = _activeVolumes[i];
                if (cullingPlanes != null)
                {
                    bool inFrustum = GeometryUtility.TestPlanesAABB(cullingPlanes, vol.WorldBounds);
                    bool inShadowRange = false;

                    if (!inFrustum && shadowDistance > 0)
                    {
                        Vector3 closest = vol.WorldBounds.ClosestPoint(viewerPos);
                        inShadowRange = (closest - viewerPos).sqrMagnitude < shadowDistSqr;
                    }

                    if (!inFrustum && !inShadowRange) continue; 
                }

                _visibleVolumes.Add(vol);
                ChunkDef def = new ChunkDef();
                def.boundsMin = vol.WorldBounds.min;
                // Note: PageTableOffset acts as the "nodeOffset" for Paged SVO shaders
                def.nodeOffset = (uint)vol.BufferManager.PageTableOffset; 
                def.boundsMax = vol.WorldBounds.max;
                def.payloadOffset = (uint)vol.BufferManager.PageTableOffset; 
                def.brickDataOffset = (uint)vol.BufferManager.BrickDataOffset;
                def.worldToLocal = vol.transform.worldToLocalMatrix;
                def.localToWorld = vol.transform.localToWorldMatrix;
                
                _chunkData[writeIndex] = def;
                writeIndex++;
            }
            
            if (poolSize > 0 && _visibleVolumes.Count > 0)
            {
                ChunkBuffer.SetData(_chunkData, 0, 0, _visibleVolumes.Count);
                ComputeTLAS(_visibleVolumes.Count);
            }
        }

        private void ComputeTLAS(int activeCount)
        {
            if (activeCount == 0) return;

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i < activeCount; i++)
            {
                var c = _chunkData[i];
                min = Vector3.Min(min, c.boundsMin);
                max = Vector3.Max(max, c.boundsMax);
            }
            
            min -= Vector3.one * 0.1f;
            max += Vector3.one * 0.1f;
            
            TLASBoundsMin = min;
            TLASBoundsMax = max;
            
            NativeReference<int> totalIndicesCount = new NativeReference<int>(Allocator.TempJob);

            ComputeTLASJob job = new ComputeTLASJob
            {
                chunks = _chunkData,
                chunkCount = activeCount,
                boundsMin = min,
                boundsMax = max,
                resolution = TLASResolution,
                grid = _tlasGrid,
                chunkIndices = _tlasIndices,
                totalIndicesCount = totalIndicesCount
            };

            job.Schedule().Complete();

            int count = totalIndicesCount.Value;
            totalIndicesCount.Dispose();

            if (TLASChunkIndexBuffer.count < count)
            {
                TLASChunkIndexBuffer.Release();
                TLASChunkIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(count, MAX_TLAS_INDICES * 2), 4);
            }

            TLASGridBuffer.SetData(_tlasGrid);
            TLASChunkIndexBuffer.SetData(_tlasIndices, 0, 0, count);
        }

        [BurstCompile]
        struct ComputeTLASJob : IJob
        {
            [ReadOnly] public NativeArray<ChunkDef> chunks;
            public int chunkCount;
            public float3 boundsMin;
            public float3 boundsMax;
            public int resolution;
            
            public NativeArray<TLASCell> grid;
            [WriteOnly] public NativeArray<int> chunkIndices;
            public NativeReference<int> totalIndicesCount;

            public void Execute()
            {
                int totalCells = resolution * resolution * resolution;
                float3 worldSize = boundsMax - boundsMin;
                worldSize = math.max(worldSize, new float3(0.001f));
                float3 cellSize = worldSize / resolution;
                
                for (int i = 0; i < totalCells; i++)
                {
                    grid[i] = new TLASCell { offset = 0, count = 0 };
                }

                for (int i = 0; i < chunkCount; i++)
                {
                    ChunkDef c = chunks[i];
                    float3 minCellF = ((float3)c.boundsMin - boundsMin);
                    float3 maxCellF = ((float3)c.boundsMax - boundsMin);
                    
                    int3 minCell = math.clamp((int3)(minCellF / cellSize), 0, resolution - 1);
                    int3 maxCell = math.clamp((int3)(maxCellF / cellSize), 0, resolution - 1);
                    
                    for (int z = minCell.z; z <= maxCell.z; z++)
                    for (int y = minCell.y; y <= maxCell.y; y++)
                    for (int x = minCell.x; x <= maxCell.x; x++)
                    {
                        int idx = z * resolution * resolution + y * resolution + x;
                        var cell = grid[idx];
                        cell.count++;
                        grid[idx] = cell;
                    }
                }

                uint currentOffset = 0;
                for (int i = 0; i < totalCells; i++)
                {
                    var cell = grid[i];
                    cell.offset = currentOffset;
                    grid[i] = cell;
                    currentOffset += cell.count;
                }
                
                totalIndicesCount.Value = (int)currentOffset;
                
                for (int i = 0; i < chunkCount; i++)
                {
                    ChunkDef c = chunks[i];
                    float3 minCellF = ((float3)c.boundsMin - boundsMin);
                    float3 maxCellF = ((float3)c.boundsMax - boundsMin);
                    
                    int3 minCell = math.clamp((int3)(minCellF / cellSize), 0, resolution - 1);
                    int3 maxCell = math.clamp((int3)(maxCellF / cellSize), 0, resolution - 1);
                    
                    for (int z = minCell.z; z <= maxCell.z; z++)
                    for (int y = minCell.y; y <= maxCell.y; y++)
                    for (int x = minCell.x; x <= maxCell.x; x++)
                    {
                        int idx = z * resolution * resolution + y * resolution + x;
                        var cell = grid[idx];
                        
                        if (cell.offset < chunkIndices.Length)
                        {
                            chunkIndices[(int)cell.offset] = i;
                        }
                        
                        cell.offset++; 
                        grid[idx] = cell;
                    }
                }
                
                for (int i = 0; i < totalCells; i++)
                {
                    var cell = grid[i];
                    cell.offset -= cell.count; 
                    grid[i] = cell;
                }
            }
        }

        public int ActiveChunkCount => _activeVolumes.Count;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            GlobalNodeBuffer?.Release();
            GlobalPayloadBuffer?.Release();
            GlobalBrickDataBuffer?.Release();
            ChunkBuffer?.Release();
            TLASGridBuffer?.Release();
            TLASChunkIndexBuffer?.Release();
            
            if (_chunkData.IsCreated) _chunkData.Dispose();
            if (_tlasGrid.IsCreated) _tlasGrid.Dispose();
            if (_tlasIndices.IsCreated) _tlasIndices.Dispose();
        }
    }
}