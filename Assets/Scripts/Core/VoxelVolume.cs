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
        public GraphicsBuffer BrickNormalBuffer => BufferManager?.BrickNormalBuffer;
        public GraphicsBuffer CounterBuffer => BufferManager?.CounterBuffer;
        
        public int Resolution => resolution;
        public int MaxNodes => _maxNodes;
        public int MaxBricks => _maxBricks;
        public bool IsReady => BufferManager != null;

        // --- FIX: Register with the system so WorldManager can find us ---
        private void OnEnable()
        {
            VoxelVolumeRegistry.Register(this);
        }

        private void OnDisable()
        {
            VoxelVolumeRegistry.Unregister(this);
        }
        // ----------------------------------------------------------------

        public void AssignMemorySlice(VoxelVolumePool pool, int nodeOffset, int payloadOffset, int brickOffset, int nodes, int bricks)
        {
            _maxNodes = nodes;
            _maxBricks = bricks;
            
            BufferManager = new SVOBufferManager(
                pool.GlobalNodeBuffer, nodeOffset,
                pool.GlobalPayloadBuffer, payloadOffset,
                pool.GlobalBrickBuffer, pool.GlobalBrickMaterialBuffer, pool.GlobalBrickNormalBuffer, brickOffset
            );
        }

        public void OnPullFromPool(Vector3 worldOrigin, float size)
        {
            WorldOrigin = worldOrigin;
            WorldSize = size;

            BufferManager.ResetCounters();
            this.gameObject.SetActive(true); // Triggers OnEnable -> Registry.Register
            Regenerate();
        }

        public void OnReturnToPool()
        {
            this.gameObject.SetActive(false); // Triggers OnDisable -> Registry.Unregister
        }

        // Renamed from Generate to Regenerate and made public for Phase 4 Update Loop
        public void Regenerate()
        {
            if (svoCompute == null || !IsReady) return;
            SVOGenerator.Build(svoCompute, BufferManager, resolution, WorldOrigin, WorldSize);
        }

        // Persistence (Simplified for now)
        public void Save(string filePath, Action<bool> onComplete = null) => VoxelDataSerializer.Save(this, filePath, onComplete);
        public void Load(string filePath) => VoxelDataSerializer.Load(this, filePath);
        
        private void OnDestroy() { BufferManager?.Dispose(); }
    }
}