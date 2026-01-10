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
        public uint brickOffset;
        public Vector3 padding; 
    }

    public class VoxelVolumePool : MonoBehaviour
    {
        public static VoxelVolumePool Instance { get; private set; }

        [Header("Pool Settings")]
        public VoxelVolume prefab;
        public int poolSize = 100;
        public Transform poolContainer;

        [Header("Global Memory Settings")]
        public int maxNodesPerVolume = 50000; 
        public int maxBricksPerVolume = 25000; 

        public GraphicsBuffer GlobalNodeBuffer { get; private set; }
        public GraphicsBuffer GlobalPayloadBuffer { get; private set; }
        public GraphicsBuffer GlobalBrickBuffer { get; private set; }
        public GraphicsBuffer GlobalBrickMaterialBuffer { get; private set; }
        
        public GraphicsBuffer ChunkBuffer { get; private set; }
        private ChunkDef[] _chunkData;

        private Queue<VoxelVolume> _pool = new Queue<VoxelVolume>();
        private List<VoxelVolume> _activeVolumes = new List<VoxelVolume>();
        
        // This is what we pass to the GPU now
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
            int totalBrickVoxels = totalBricks * 216; // 6x6x6 padded bricks

            Debug.Log($"Allocating Global Voxel Memory: {totalNodes/1000}k Nodes, {totalBricks/1000}k Bricks.");

            GlobalNodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalNodes, Marshal.SizeOf<SVONode>());
            GlobalPayloadBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalNodes, Marshal.SizeOf<VoxelPayload>());
            
            GlobalBrickBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalBrickVoxels, sizeof(float));
            GlobalBrickMaterialBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalBrickVoxels, sizeof(uint));

            _chunkData = new ChunkDef[poolSize];
            ChunkBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, poolSize, Marshal.SizeOf<ChunkDef>());
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
                int brickOffset = i * maxBricksPerVolume * 216;
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
            
            // Initial update (will be overwritten by per-frame culling)
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

                // --- Frustum Culling ---
                if (cullingPlanes != null)
                {
                    // Check if AABB is within frustum
                    // If we passed 5 planes, the Far plane is ignored, preventing pop-in
                    if (!GeometryUtility.TestPlanesAABB(cullingPlanes, vol.WorldBounds))
                    {
                        continue; 
                    }
                }

                ChunkDef def = new ChunkDef();
                def.boundsMin = vol.WorldBounds.min;
                def.boundsMax = vol.WorldBounds.max;
                def.nodeOffset = (uint)vol.BufferManager.NodeOffset;
                def.payloadOffset = (uint)vol.BufferManager.PayloadOffset;
                def.brickOffset = (uint)vol.BufferManager.BrickOffset;
                
                _chunkData[writeIndex] = def;
                writeIndex++;
            }

            VisibleChunkCount = writeIndex;
            
            if (poolSize > 0)
            {
                // Upload only the visible data to the GPU (or all if buffer is fixed size, but we only iterate up to VisibleChunkCount in shader)
                ChunkBuffer.SetData(_chunkData);
            }
        }

        public int ActiveChunkCount => _activeVolumes.Count;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            GlobalNodeBuffer?.Release();
            GlobalPayloadBuffer?.Release();
            GlobalBrickBuffer?.Release();
            GlobalBrickMaterialBuffer?.Release();
            ChunkBuffer?.Release();
        }
    }
}