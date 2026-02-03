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

    public class VoxelVolumePool : MonoBehaviour
    {
        public static VoxelVolumePool Instance { get; private set; }
        public VoxelVolume prefab;
        public int poolSize = 100;
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
        private List<VoxelVolume> _activeVolumes = new List<VoxelVolume>();
        
        private PhysicalPageAllocator _nodeAllocator; 
        private VoxelMemoryAllocator _pageTableAllocator; 
        private VoxelMemoryAllocator _brickAllocator;
        
        public int VisibleChunkCount { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            InitializeGlobalBuffers();
            InitializePool();
        }

        private void InitializeGlobalBuffers()
        {
            int totalNodes = poolSize * maxNodesPerVolume;
            // Ensure totalNodes is multiple of PAGE_SIZE for safety
            int pageSize = SVONode.PAGE_SIZE;
            if (totalNodes % pageSize != 0) totalNodes = ((totalNodes / pageSize) + 1) * pageSize;
            
            int totalPages = totalNodes / pageSize;

            int totalBricks = poolSize * maxBricksPerVolume; 
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

        public VoxelVolume GetVolume(Vector3 position, float size, int requestedNodes = -1, int requestedBricks = -1, int resolution = -1, bool generateEmpty = false)
        {
            if (_pool.Count == 0) return null;
            
            if (requestedNodes < 0) requestedNodes = maxNodesPerVolume;
            if (requestedBricks < 0) requestedBricks = maxBricksPerVolume;
            
            int pageSize = SVONode.PAGE_SIZE;
            int pagesNeeded = Mathf.CeilToInt((float)requestedNodes / pageSize);
            
            // Attempt Allocation
            if (!_nodeAllocator.Allocate(pagesNeeded, out int[] pages))
            {
                Debug.LogWarning("VoxelVolumePool: Failed to allocate pages.");
                return null;
            }
            
            if (!_pageTableAllocator.Allocate(pagesNeeded, out int pageTableOffset))
            {
                Debug.LogWarning("VoxelVolumePool: Failed to allocate page table entries.");
                _nodeAllocator.Free(pages);
                return null;
            }
            
            // Update Page Table Buffer
            int[] pageTableData = new int[pagesNeeded];
            for (int i = 0; i < pagesNeeded; i++)
            {
                pageTableData[i] = pages[i] * pageSize; 
            }
            GlobalPageTableBuffer.SetData(pageTableData, 0, pageTableOffset, pagesNeeded);

            int brickVoxels = requestedBricks * SVONode.BRICK_VOXEL_COUNT;
            if (!_brickAllocator.Allocate(brickVoxels, out int brickOffset))
            {
                Debug.LogWarning("VoxelVolumePool: Failed to allocate bricks.");
                _nodeAllocator.Free(pages);
                _pageTableAllocator.Free(pageTableOffset, pagesNeeded);
                return null;
            }

            VoxelVolume vol = _pool.Dequeue();
            vol.AssignMemorySlice(this, 0, 0, brickOffset, requestedNodes, requestedBricks, pageTableOffset, pages);

            // Configure volume
            vol.transform.position = position;
            float scale = size / vol.Resolution; 
            
            // Apply requested resolution if valid
            if (resolution > 0)
            {
                vol.resolution = resolution;
                scale = size / resolution;
            }
            
            vol.transform.localScale = Vector3.one * scale;
            
            // Pass empty flag
            vol.OnPullFromPool(position, size, generateEmpty);
            
            _activeVolumes.Add(vol);
            UpdateChunkBuffer(null);
            return vol;
        }

        public void ReturnVolume(VoxelVolume vol)
        {
            if (vol == null) return;
            if (_activeVolumes.Remove(vol))
            {
                if (vol.IsReady)
                {
                    _nodeAllocator.Free(vol.AllocatedPages);
                    _pageTableAllocator.Free(vol.BufferManager.PageTableOffset, vol.AllocatedPages.Length);
                    _brickAllocator.Free(vol.BufferManager.BrickDataOffset, vol.MaxBricks * SVONode.BRICK_VOXEL_COUNT);
                }
                
                vol.OnReturnToPool();
                vol.transform.SetParent(poolContainer); 
                _pool.Enqueue(vol);
                UpdateChunkBuffer(null);
            }
        }

        public void UpdateVisibility(Plane[] cullingPlanes)
        {
            UpdateChunkBuffer(cullingPlanes);
        }

        private void UpdateChunkBuffer(Plane[] cullingPlanes)
        {
            int writeIndex = 0;
            for (int i = 0; i < _activeVolumes.Count; i++)
            {
                var vol = _activeVolumes[i];
                if (cullingPlanes != null)
                {
                    if (!GeometryUtility.TestPlanesAABB(cullingPlanes, vol.WorldBounds)) continue; 
                }

                ChunkDef def = new ChunkDef();
                def.boundsMin = vol.WorldBounds.min;
                def.nodeOffset = (uint)vol.BufferManager.PageTableOffset; 
                def.boundsMax = vol.WorldBounds.max;
                def.payloadOffset = (uint)vol.BufferManager.PageTableOffset; 
                def.brickDataOffset = (uint)vol.BufferManager.BrickDataOffset;
                def.worldToLocal = vol.transform.worldToLocalMatrix;
                def.localToWorld = vol.transform.localToWorldMatrix;
                
                _chunkData[writeIndex] = def;
                writeIndex++;
            }
            VisibleChunkCount = writeIndex;
            
            if (poolSize > 0 && VisibleChunkCount > 0)
            {
                ChunkBuffer.SetData(_chunkData, 0, 0, VisibleChunkCount);
                ComputeTLAS(writeIndex);
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
                Debug.LogWarning($"TLAS Indices overflow: {count} > {TLASChunkIndexBuffer.count}. Increasing buffer size.");
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