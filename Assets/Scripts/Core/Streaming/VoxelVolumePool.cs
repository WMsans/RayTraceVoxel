using System.Collections.Generic;
using UnityEngine;
using System.Runtime.InteropServices;
using VoxelEngine.Core.Buffers;
using VoxelEngine.Core.Data;

namespace VoxelEngine.Core.Streaming
{
    public struct ChunkDef
    {
        public Vector3 boundsMin;
        public uint nodeOffset;
        public Vector3 boundsMax;
        public uint payloadOffset;
        public uint brickDataOffset; // Changed
        public Vector3 padding; 
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
        
        // Merged Buffer
        public GraphicsBuffer GlobalBrickDataBuffer { get; private set; }
        
        public GraphicsBuffer ChunkBuffer { get; private set; }

        // --- TLAS Buffers ---
        public GraphicsBuffer TLASGridBuffer { get; private set; }
        public GraphicsBuffer TLASChunkIndexBuffer { get; private set; }
        public Vector3 TLASBoundsMin { get; private set; }
        public Vector3 TLASBoundsMax { get; private set; }
        public int TLASResolution = 16;
        
        private ChunkDef[] _chunkData;
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
            int totalNodes = poolSize * maxNodesPerVolume;
            int totalBricks = poolSize * maxBricksPerVolume; 
            int totalBrickVoxels = totalBricks * SVONode.BRICK_VOXEL_COUNT;

            // Allocation size significantly reduced
            Debug.Log($"Allocating Global Voxel Memory: {totalNodes/1000}k Nodes, {totalBricks/1000}k Bricks. BrickData: {totalBrickVoxels * 4 / 1024 / 1024} MB");

            GlobalNodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalNodes, Marshal.SizeOf<SVONode>());
            GlobalPayloadBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalNodes, Marshal.SizeOf<VoxelPayload>());
            
            // Single Buffer (stride 4 bytes)
            GlobalBrickDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalBrickVoxels, sizeof(uint));

            _chunkData = new ChunkDef[poolSize];
            ChunkBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, poolSize, Marshal.SizeOf<ChunkDef>());

            // Initialize TLAS Buffers with dummy data
            int tlasSize = TLASResolution * TLASResolution * TLASResolution;
            TLASGridBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, tlasSize, 8); // 2 uints
            TLASChunkIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1024, 4); // Initial size
        }

        private void InitializePool()
        {
            if (prefab == null) return;
            if (poolContainer == null) poolContainer = this.transform;

            for (int i = 0; i < poolSize; i++)
            {
                VoxelVolume vol = Instantiate(prefab, poolContainer);
                vol.gameObject.name = $"Volume_Pool_{i}";
                int nodeOffset = i * maxNodesPerVolume;
                int payloadOffset = i * maxNodesPerVolume;
                int brickOffset = i * maxBricksPerVolume * SVONode.BRICK_VOXEL_COUNT;
                
                vol.AssignMemorySlice(this, nodeOffset, payloadOffset, brickOffset, maxNodesPerVolume, maxBricksPerVolume);
                vol.gameObject.SetActive(false);
                _pool.Enqueue(vol);
            }
        }

        public VoxelVolume GetVolume(Vector3 position, float size)
        {
            if (_pool.Count == 0) return null;
            VoxelVolume vol = _pool.Dequeue();
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
                def.boundsMax = vol.WorldBounds.max;
                def.nodeOffset = (uint)vol.BufferManager.NodeOffset;
                def.payloadOffset = (uint)vol.BufferManager.PayloadOffset;
                def.brickDataOffset = (uint)vol.BufferManager.BrickDataOffset; // Changed
                
                _chunkData[writeIndex] = def;
                writeIndex++;
            }
            VisibleChunkCount = writeIndex;
            if (poolSize > 0) ChunkBuffer.SetData(_chunkData);
            
            ComputeTLAS(writeIndex);
        }

        private void ComputeTLAS(int activeCount)
        {
            if (activeCount == 0) return;

            // 1. Compute Scene Bounds
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i < activeCount; i++)
            {
                min = Vector3.Min(min, _chunkData[i].boundsMin);
                max = Vector3.Max(max, _chunkData[i].boundsMax);
            }
            
            // Padding
            min -= Vector3.one * 0.1f;
            max += Vector3.one * 0.1f;
            
            TLASBoundsMin = min;
            TLASBoundsMax = max;
            
            int res = TLASResolution;
            int totalCells = res * res * res;
            
            // Check buffer size
            if (TLASGridBuffer == null || TLASGridBuffer.count != totalCells)
            {
                TLASGridBuffer?.Release();
                TLASGridBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalCells, 8);
            }
            
            // Optimisation: use static arrays or pool these to avoid GC if frame rate is high
            int[] cellCounts = new int[totalCells];
            
            Vector3 worldSize = max - min;
            // Prevent division by zero
            worldSize.x = Mathf.Max(worldSize.x, 0.001f);
            worldSize.y = Mathf.Max(worldSize.y, 0.001f);
            worldSize.z = Mathf.Max(worldSize.z, 0.001f);
            
            Vector3 cellSize = new Vector3(worldSize.x / res, worldSize.y / res, worldSize.z / res);
            
            // Pass 1: Count
            for (int i = 0; i < activeCount; i++)
            {
                var c = _chunkData[i];
                Vector3 minCellF = (c.boundsMin - min);
                Vector3 maxCellF = (c.boundsMax - min);
                
                Vector3Int minCell = new Vector3Int(
                    Mathf.Clamp((int)(minCellF.x / cellSize.x), 0, res - 1),
                    Mathf.Clamp((int)(minCellF.y / cellSize.y), 0, res - 1),
                    Mathf.Clamp((int)(minCellF.z / cellSize.z), 0, res - 1)
                );
                
                Vector3Int maxCell = new Vector3Int(
                    Mathf.Clamp((int)(maxCellF.x / cellSize.x), 0, res - 1),
                    Mathf.Clamp((int)(maxCellF.y / cellSize.y), 0, res - 1),
                    Mathf.Clamp((int)(maxCellF.z / cellSize.z), 0, res - 1)
                );
                
                for (int z = minCell.z; z <= maxCell.z; z++)
                for (int y = minCell.y; y <= maxCell.y; y++)
                for (int x = minCell.x; x <= maxCell.x; x++)
                {
                    int idx = z * res * res + y * res + x;
                    cellCounts[idx]++;
                }
            }
            
            // Prefix Sum
            uint[] tlasCells = new uint[totalCells * 2]; // { offset, count }
            int currentOffset = 0;
            for (int i = 0; i < totalCells; i++)
            {
                tlasCells[i * 2] = (uint)currentOffset;
                tlasCells[i * 2 + 1] = (uint)cellCounts[i];
                currentOffset += cellCounts[i];
            }
            
            int totalIndices = currentOffset;
            int[] chunkIndices = new int[totalIndices];
            
            // Fill Offsets (temp array to track current write position)
            int[] fillOffsets = new int[totalCells];
            for (int i = 0; i < totalCells; i++) fillOffsets[i] = (int)tlasCells[i * 2];
            
            // Pass 2: Fill
            for (int i = 0; i < activeCount; i++)
            {
                var c = _chunkData[i];
                Vector3 minCellF = (c.boundsMin - min);
                Vector3 maxCellF = (c.boundsMax - min);
                
                Vector3Int minCell = new Vector3Int(
                    Mathf.Clamp((int)(minCellF.x / cellSize.x), 0, res - 1),
                    Mathf.Clamp((int)(minCellF.y / cellSize.y), 0, res - 1),
                    Mathf.Clamp((int)(minCellF.z / cellSize.z), 0, res - 1)
                );
                
                Vector3Int maxCell = new Vector3Int(
                    Mathf.Clamp((int)(maxCellF.x / cellSize.x), 0, res - 1),
                    Mathf.Clamp((int)(maxCellF.y / cellSize.y), 0, res - 1),
                    Mathf.Clamp((int)(maxCellF.z / cellSize.z), 0, res - 1)
                );

                for (int z = minCell.z; z <= maxCell.z; z++)
                for (int y = minCell.y; y <= maxCell.y; y++)
                for (int x = minCell.x; x <= maxCell.x; x++)
                {
                    int idx = z * res * res + y * res + x;
                    chunkIndices[fillOffsets[idx]] = i;
                    fillOffsets[idx]++;
                }
            }
            
            TLASGridBuffer.SetData(tlasCells);
            
            if (TLASChunkIndexBuffer == null || TLASChunkIndexBuffer.count < totalIndices)
            {
                TLASChunkIndexBuffer?.Release();
                // Grow with some buffer to avoid frequent realloc
                TLASChunkIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(totalIndices, 1024) * 2, 4);
            }
            TLASChunkIndexBuffer.SetData(chunkIndices);
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
        }
    }
}