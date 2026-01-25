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
        
        public GraphicsBuffer ChunkBuffer { get; private set; }

        // --- Paging ---
        public const int PAGE_SIZE = 2048;
        public const int PAGE_SHIFT = 11; // 2^11 = 2048
        
        public GraphicsBuffer GlobalNodePageTable { get; private set; }
        private NativeArray<uint> _pageTableData; // Maps Virtual Page ID -> Physical Node Offset
        private Stack<int> _freePhysicalPages; // Stores Physical Page Indices
        private int _totalPhysicalPages;

        // --- Memory Allocators ---
        private VoxelMemoryAllocator _nodeAllocator; // Virtual Allocator
        private VoxelMemoryAllocator _brickAllocator; // Physical Allocator (Bricks not paged yet)

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
        private const int MAX_TLAS_INDICES = 262144; // 256k ints (~1MB)

        private Queue<VoxelVolume> _pool = new Queue<VoxelVolume>();
        private List<VoxelVolume> _activeVolumes = new List<VoxelVolume>();
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
            int nodesPerPage = PAGE_SIZE;
            int maxPagesPerVolume = Mathf.CeilToInt((float)maxNodesPerVolume / nodesPerPage);
            int totalPhysicalPagesNeeded = poolSize * maxPagesPerVolume;
            
            // Allocate enough physical memory for aligned pages
            int totalNodes = totalPhysicalPagesNeeded * nodesPerPage;
            
            int totalBricks = poolSize * maxBricksPerVolume; 
            int totalBrickVoxels = totalBricks * SVONode.BRICK_VOXEL_COUNT;

            Debug.Log($"Allocating Global Voxel Memory: {totalNodes/1000}k Nodes, {totalBricks/1000}k Bricks. BrickData: {totalBrickVoxels * 4 / 1024 / 1024} MB");

            GlobalNodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalNodes, Marshal.SizeOf<SVONode>());
            GlobalPayloadBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalNodes, Marshal.SizeOf<VoxelPayload>());
            GlobalBrickDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalBrickVoxels, sizeof(uint));

            ChunkBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, poolSize, Marshal.SizeOf<ChunkDef>());
            
            // --- Paging Initialization ---
            _totalPhysicalPages = totalNodes / PAGE_SIZE;
            int virtualNodes = totalNodes * 2; // Overcommit Virtual Space to reduce fragmentation
            int virtualPages = virtualNodes / PAGE_SIZE;

            GlobalNodePageTable = new GraphicsBuffer(GraphicsBuffer.Target.Structured, virtualPages, sizeof(uint));
            _pageTableData = new NativeArray<uint>(virtualPages, Allocator.Persistent);
            
            _freePhysicalPages = new Stack<int>(_totalPhysicalPages);
            for (int i = _totalPhysicalPages - 1; i >= 0; i--) 
            {
                _freePhysicalPages.Push(i); // Push Page Index (0...N)
            }

            // Initialize Allocators
            _nodeAllocator = new VoxelMemoryAllocator(virtualNodes);
            _brickAllocator = new VoxelMemoryAllocator(totalBricks);

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
                // No longer assigning memory slice here. Memory is allocated on demand.
                vol.gameObject.SetActive(false);
                _pool.Enqueue(vol);
            }
        }

        public VoxelVolume GetVolume(Vector3 position, float size, int nodeLimit = -1, int brickLimit = -1)
        {
            if (_pool.Count == 0) 
            {
                Debug.LogWarning("VoxelVolumePool: Pool empty!");
                return null;
            }

            int nodesToAlloc = (nodeLimit > 0) ? nodeLimit : maxNodesPerVolume;
            int bricksToAlloc = (brickLimit > 0) ? brickLimit : maxBricksPerVolume;

            // Align to Page Size to prevent virtual page sharing conflicts
            int alignedNodes = Mathf.CeilToInt((float)nodesToAlloc / PAGE_SIZE) * PAGE_SIZE;

            // 1. Allocate Virtual Address Space
            if (!_nodeAllocator.Allocate(alignedNodes, out int nodeVirtualOffset))
            {
                Debug.LogError("VoxelVolumePool: Failed to allocate Virtual Nodes!");
                return null;
            }

            // 2. Allocate Physical Pages
            int startPage = nodeVirtualOffset / PAGE_SIZE;
            int endPage = (nodeVirtualOffset + alignedNodes - 1) / PAGE_SIZE;
            int pagesNeeded = endPage - startPage + 1;

            if (_freePhysicalPages.Count < pagesNeeded)
            {
                _nodeAllocator.Free(nodeVirtualOffset, alignedNodes);
                Debug.LogError($"VoxelVolumePool: Out of Physical Pages! Need {pagesNeeded}, Have {_freePhysicalPages.Count}");
                return null;
            }

            // Map Pages
            for (int i = 0; i < pagesNeeded; i++)
            {
                int physPageIdx = _freePhysicalPages.Pop();
                uint physNodeOffset = (uint)(physPageIdx * PAGE_SIZE);
                _pageTableData[startPage + i] = physNodeOffset;
            }
            
            // Upload Page Table (Optimize: only upload changed range)
            GlobalNodePageTable.SetData(_pageTableData, startPage, startPage, pagesNeeded);

            // 3. Allocate Bricks (Contiguous Physical)
            if (!_brickAllocator.Allocate(bricksToAlloc, out int brickOffset))
            {
                // Rollback Nodes
                for (int i = 0; i < pagesNeeded; i++) 
                {
                    uint physOffset = _pageTableData[startPage + i];
                    _freePhysicalPages.Push((int)(physOffset / PAGE_SIZE));
                }
                _nodeAllocator.Free(nodeVirtualOffset, alignedNodes);
                
                Debug.LogError("VoxelVolumePool: Failed to allocate Bricks!");
                return null;
            }

            VoxelVolume vol = _pool.Dequeue();
            
            // Assign Memory
            int brickDataOffset = brickOffset * SVONode.BRICK_VOXEL_COUNT;
            
            vol.AssignMemorySlice(this, nodeVirtualOffset, nodeVirtualOffset, brickDataOffset, alignedNodes, bricksToAlloc);

            vol.transform.position = position;
            float scale = size / vol.Resolution; 
            vol.transform.localScale = Vector3.one * scale;
            vol.OnPullFromPool(position, size);
            _activeVolumes.Add(vol);
            UpdateChunkBuffer(null);
            return vol;
        }

        public void ReturnVolume(VoxelVolume vol)
        {
            if (vol == null) return;
            if (_activeVolumes.Remove(vol))
            {
                // Free Memory
                if (vol.BufferManager != null)
                {
                    int nodeVirtualOffset = vol.BufferManager.NodeOffset;
                    int nodesAllocated = vol.MaxNodes; // This stores the aligned size now

                    // Unmap Pages
                    int startPage = nodeVirtualOffset / PAGE_SIZE;
                    int endPage = (nodeVirtualOffset + nodesAllocated - 1) / PAGE_SIZE;
                    int pagesUsed = endPage - startPage + 1;

                    for (int i = 0; i < pagesUsed; i++)
                    {
                        uint physOffset = _pageTableData[startPage + i];
                        _freePhysicalPages.Push((int)(physOffset / PAGE_SIZE));
                    }

                    _nodeAllocator.Free(nodeVirtualOffset, nodesAllocated);
                    
                    int brickIndex = vol.BufferManager.BrickDataOffset / SVONode.BRICK_VOXEL_COUNT;
                    _brickAllocator.Free(brickIndex, vol.MaxBricks);
                }

                vol.OnReturnToPool();
                vol.ReleaseMemory();

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
            // Note: Parallelizing this loop is hard because it accesses Unity Objects (VoxelVolume)
            for (int i = 0; i < _activeVolumes.Count; i++)
            {
                var vol = _activeVolumes[i];
                if (cullingPlanes != null)
                {
                    if (!GeometryUtility.TestPlanesAABB(cullingPlanes, vol.WorldBounds)) continue; 
                }

                ChunkDef def = new ChunkDef();
                def.boundsMin = vol.WorldBounds.min;
                def.boundsMax = vol.WorldBounds.max;
                def.nodeOffset = (uint)vol.BufferManager.NodeOffset;
                def.payloadOffset = (uint)vol.BufferManager.PayloadOffset;
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

            // 1. Compute Scene Bounds (Main thread for simplicity or jobify)
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
            
            // Output index count
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

            // Check buffer resize
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
                
                // Clear grid
                for (int i = 0; i < totalCells; i++)
                {
                    grid[i] = new TLASCell { offset = 0, count = 0 };
                }

                // Pass 1: Count
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

                // Prefix Sum
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
                        
                        // Safety check
                        if (cell.offset < chunkIndices.Length)
                        {
                            chunkIndices[(int)cell.offset] = i;
                        }
                        
                        cell.offset++; // Increment cursor
                        grid[idx] = cell;
                    }
                }
                
                // Restore offsets
                for (int i = 0; i < totalCells; i++)
                {
                    var cell = grid[i];
                    cell.offset -= cell.count; // Backtrack to start
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
            
            GlobalNodePageTable?.Release();
            
            if (_chunkData.IsCreated) _chunkData.Dispose();
            if (_tlasGrid.IsCreated) _tlasGrid.Dispose();
            if (_tlasIndices.IsCreated) _tlasIndices.Dispose();
            if (_pageTableData.IsCreated) _pageTableData.Dispose();
        }
    }
}
