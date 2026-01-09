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
        public int maxBricksPerVolume = 25000; // x64 voxels

        // --- Monolithic Global Buffers ---
        public GraphicsBuffer GlobalNodeBuffer { get; private set; }
        public GraphicsBuffer GlobalPayloadBuffer { get; private set; }
        public GraphicsBuffer GlobalBrickBuffer { get; private set; }
        public GraphicsBuffer GlobalBrickMaterialBuffer { get; private set; }
        
        // --- TLAS (Chunk Map) ---
        public GraphicsBuffer ChunkBuffer { get; private set; }
        private ChunkDef[] _chunkData;

        private Queue<VoxelVolume> _pool = new Queue<VoxelVolume>();
        private List<VoxelVolume> _activeVolumes = new List<VoxelVolume>();

        private void Awake()
        {
            // Singleton: Handle duplicates safely
            if (Instance != null && Instance != this)
            {
                Destroy(this); 
                return; 
            }
            Instance = this;
            
            InitializeGlobalBuffers();
            InitializePool();
        }

        private void InitializeGlobalBuffers()
        {
            int totalNodes = poolSize * maxNodesPerVolume;
            int totalBricks = poolSize * maxBricksPerVolume; // Bricks (not voxels)
            int totalBrickVoxels = totalBricks * 64;

            Debug.Log($"Allocating Global Voxel Memory: {totalNodes/1000}k Nodes, {totalBricks/1000}k Bricks.");

            GlobalNodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalNodes, Marshal.SizeOf<SVONode>());
            GlobalPayloadBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalNodes, Marshal.SizeOf<VoxelPayload>()); // 1:1 worst case
            
            GlobalBrickBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalBrickVoxels, sizeof(float));
            GlobalBrickMaterialBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalBrickVoxels, sizeof(uint));

            // TLAS Buffer
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
                
                // Assign Slice of Global Memory
                int nodeOffset = i * maxNodesPerVolume;
                int payloadOffset = i * maxNodesPerVolume;
                int brickOffset = i * maxBricksPerVolume * 64; // *64 for raw index

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
            
            UpdateChunkBuffer();
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
                UpdateChunkBuffer();
            }
        }

        public void UpdateChunkBuffer()
        {
            // Rebuild the TLAS data
            // We clear it first (conceptually) by setting scale 0 or something, but essentially we just rewrite valid ones
            // Actually, the shader loops 0..Count. 
            // We need to pack the active ones at the start or pass a count.
            
            // To simplify, we will just write ALL active volumes to the array and update the count uniform later.
            
            for (int i = 0; i < _activeVolumes.Count; i++)
            {
                var vol = _activeVolumes[i];
                ChunkDef def = new ChunkDef();
                
                Vector3 center = vol.transform.position;
                float size = vol.Resolution * vol.transform.localScale.x;
                Vector3 extents = Vector3.one * size * 0.5f;

                def.boundsMin = vol.WorldBounds.min;
                def.boundsMax = vol.WorldBounds.max;
                
                def.nodeOffset = (uint)vol.BufferManager.NodeOffset;
                def.payloadOffset = (uint)vol.BufferManager.PayloadOffset;
                def.brickOffset = (uint)vol.BufferManager.BrickOffset;
                
                _chunkData[i] = def;
            }
            
            ChunkBuffer.SetData(_chunkData);
        }

        // Accessor for the Raytracer
        public int ActiveChunkCount => _activeVolumes.Count;

        private void OnDestroy()
        {
            // Clear singleton if this instance is the owner
            if (Instance == this) Instance = null;

            GlobalNodeBuffer?.Release();
            GlobalPayloadBuffer?.Release();
            GlobalBrickBuffer?.Release();
            GlobalBrickMaterialBuffer?.Release();
            ChunkBuffer?.Release();
        }
    }
}