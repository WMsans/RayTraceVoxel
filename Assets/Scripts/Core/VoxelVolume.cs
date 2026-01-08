using System;
using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine.Core.Buffers;
using VoxelEngine.Core.Generators;
using VoxelEngine.Core.Interfaces;
using VoxelEngine.Core.Serialization;

namespace VoxelEngine.Core
{
    public class VoxelVolume : MonoBehaviour, IVoxelStorage
    {
        [Header("Settings")]
        public ComputeShader svoCompute;
        public int resolution = 64;
        
        // Settings injected by Pool
        private int _maxNodes;
        private int _maxBricks;

        private SVOBufferManager _bufferManager;

        // IVoxelStorage Implementation
        public GraphicsBuffer NodeBuffer => _bufferManager?.NodeBuffer;
        public GraphicsBuffer PayloadBuffer => _bufferManager?.PayloadBuffer;
        public GraphicsBuffer BrickBuffer => _bufferManager?.BrickBuffer;
        public GraphicsBuffer BrickMaterialBuffer => _bufferManager?.BrickMaterialBuffer;
        public GraphicsBuffer CounterBuffer => _bufferManager?.CounterBuffer;
        
        public int Resolution => resolution;
        public int MaxNodes => _maxNodes;
        public int MaxBricks => _maxBricks;
        public bool IsReady => _bufferManager != null && _bufferManager.NodeBuffer != null;

        // --- Pooling Lifecycle ---

        /// <summary>
        /// Called ONCE by the pool at startup to allocate GPU memory.
        /// </summary>
        public void InitializeForPool(int nodes, int bricks)
        {
            _maxNodes = nodes;
            _maxBricks = bricks;
            _bufferManager = new SVOBufferManager(_maxNodes, _maxBricks);
        }

        /// <summary>
        /// Called when the volume is taken from the pool to represent a chunk.
        /// </summary>
        public void OnPullFromPool(Vector3 worldOrigin, float size)
        {
            // Reset state
            _bufferManager.ResetCounters();
            
            // Register for rendering
            this.gameObject.SetActive(true);
            
            // Generate Data
            Generate(worldOrigin, size);
        }

        /// <summary>
        /// Called when returned to the pool.
        /// </summary>
        public void OnReturnToPool()
        {
            this.gameObject.SetActive(false);
            // We don't release buffers here; we keep them for the next user.
        }

        private void Generate(Vector3 worldOrigin, float scale)
        {
            if (svoCompute == null) return;
            // Pass the World Origin so the SDF knows where this chunk is in the universe
            SVOGenerator.Build(svoCompute, _bufferManager, resolution, worldOrigin, scale);
        }

        private void OnEnable()
        {
            // Only register if we have valid buffers (prevents error on prefab)
            if (IsReady) VoxelVolumeRegistry.Register(this);
        }

        private void OnDisable()
        {
            VoxelVolumeRegistry.Unregister(this);
        }

        private void OnDestroy()
        {
            _bufferManager?.Dispose();
        }
        
        // --- Persistence wrappers ---
        public void Save(string filePath, Action<bool> onComplete = null) => VoxelDataSerializer.Save(this, filePath, onComplete);
        public void Load(string filePath) => VoxelDataSerializer.Load(this, filePath);
    }
}