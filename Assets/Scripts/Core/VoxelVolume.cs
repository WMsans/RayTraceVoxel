using System;
using UnityEngine;
using VoxelEngine.Core.Buffers;
using VoxelEngine.Core.Generators;
using VoxelEngine.Core.Interfaces;
using VoxelEngine.Core.Serialization;
using VoxelEngine.Core.Streaming;

namespace VoxelEngine.Core
{
    public class VoxelVolume : MonoBehaviour, IVoxelStorage
    {
        [Header("Settings")]
        public ComputeShader svoCompute;
        public int resolution = 64;
        
        // Managed by Pool
        public SVOBufferManager BufferManager { get; private set; }
        private int _maxNodes;
        private int _maxBricks;
        
        // Runtime State
        public Vector3 WorldOrigin { get; private set; }
        public float WorldSize { get; private set; }
        public Bounds WorldBounds => new Bounds(WorldOrigin + Vector3.one * WorldSize * 0.5f, Vector3.one * WorldSize);

        // IVoxelStorage Implementation
        public GraphicsBuffer NodeBuffer => BufferManager?.NodeBuffer;
        public GraphicsBuffer PayloadBuffer => BufferManager?.PayloadBuffer;
        public GraphicsBuffer BrickBuffer => BufferManager?.BrickBuffer;
        public GraphicsBuffer BrickMaterialBuffer => BufferManager?.BrickMaterialBuffer;
        public GraphicsBuffer CounterBuffer => BufferManager?.CounterBuffer;
        
        public int Resolution => resolution;
        public int MaxNodes => _maxNodes;
        public int MaxBricks => _maxBricks;
        public bool IsReady => BufferManager != null;

        public void AssignMemorySlice(VoxelVolumePool pool, int nodeOffset, int payloadOffset, int brickOffset, int nodes, int bricks)
        {
            _maxNodes = nodes;
            _maxBricks = bricks;
            
            BufferManager = new SVOBufferManager(
                pool.GlobalNodeBuffer, nodeOffset,
                pool.GlobalPayloadBuffer, payloadOffset,
                pool.GlobalBrickBuffer, pool.GlobalBrickMaterialBuffer, brickOffset
            );
        }

        public void OnPullFromPool(Vector3 worldOrigin, float size)
        {
            WorldOrigin = worldOrigin;
            WorldSize = size;

            BufferManager.ResetCounters();
            this.gameObject.SetActive(true);
            Generate();
        }

        public void OnReturnToPool()
        {
            this.gameObject.SetActive(false);
        }

        private void Generate()
        {
            if (svoCompute == null) return;
            SVOGenerator.Build(svoCompute, BufferManager, resolution, WorldOrigin, WorldSize);
        }

        // Persistence (Simplified for now)
        public void Save(string filePath, Action<bool> onComplete = null) => VoxelDataSerializer.Save(this, filePath, onComplete);
        public void Load(string filePath) => VoxelDataSerializer.Load(this, filePath);
        
        private void OnDestroy() { BufferManager?.Dispose(); }
    }
}