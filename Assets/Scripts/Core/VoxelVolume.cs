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
        
        public SVOBufferManager BufferManager { get; private set; }
        private int _maxNodes;
        private int _maxBricks;
        
        public Vector3 WorldOrigin { get; private set; }
        public float WorldSize { get; private set; }
        public Bounds WorldBounds => new Bounds(WorldOrigin + Vector3.one * WorldSize * 0.5f, Vector3.one * WorldSize);

        public GraphicsBuffer NodeBuffer => BufferManager?.NodeBuffer;
        public GraphicsBuffer PayloadBuffer => BufferManager?.PayloadBuffer;
        public GraphicsBuffer BrickDataBuffer => BufferManager?.BrickDataBuffer; // Merged
        public GraphicsBuffer CounterBuffer => BufferManager?.CounterBuffer;
        
        // Compat getters if needed, otherwise IVoxelStorage updated
        public GraphicsBuffer BrickBuffer => null; 
        public GraphicsBuffer BrickMaterialBuffer => null;
        public GraphicsBuffer BrickNormalBuffer => null;

        public int Resolution => resolution;
        public int MaxNodes => _maxNodes;
        public int MaxBricks => _maxBricks;
        public bool IsReady => BufferManager != null;
        
        // --- Events ---
        public event Action OnRegenerationComplete;

        private void OnEnable() { VoxelVolumeRegistry.Register(this); }
        private void OnDisable() { VoxelVolumeRegistry.Unregister(this); }

        public void AssignMemorySlice(VoxelVolumePool pool, int nodeOffset, int payloadOffset, int brickOffset, int nodes, int bricks)
        {
            _maxNodes = nodes;
            _maxBricks = bricks;
            
            BufferManager = new SVOBufferManager(
                pool.GlobalNodeBuffer, nodeOffset,
                pool.GlobalPayloadBuffer, payloadOffset,
                pool.GlobalBrickDataBuffer, brickOffset // Single Buffer
            );
        }

        public void OnPullFromPool(Vector3 worldOrigin, float size)
        {
            WorldOrigin = worldOrigin;
            WorldSize = size;
            BufferManager.ResetCounters();
            this.gameObject.SetActive(true);
            Regenerate();
        }

        public void OnReturnToPool() { this.gameObject.SetActive(false); }

        public void Regenerate()
        {
            if (svoCompute == null || !IsReady) return;
            BufferManager.ResetCounters(); 
            SVOGenerator.Build(svoCompute, BufferManager, resolution, WorldOrigin, WorldSize);
            
            // Notify listeners (e.g. GrassRenderer)
            OnRegenerationComplete?.Invoke();
        }

        public void Save(string filePath, Action<bool> onComplete = null) => VoxelDataSerializer.Save(this, filePath, onComplete);
        public void Load(string filePath) => VoxelDataSerializer.Load(this, filePath);
        
        private void OnDestroy() { BufferManager?.Dispose(); }
    }
}