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
        
        public Vector3 WorldOrigin => transform.position;
        public float WorldSize { get; private set; }
        public Bounds WorldBounds
        {
            get
            {
                // Calculate AABB of the rotated/scaled volume
                float halfRes = resolution * 0.5f;
                Vector3 localCenter = new Vector3(halfRes, halfRes, halfRes);
                Vector3 localHalfExtents = new Vector3(halfRes, halfRes, halfRes);

                Vector3 worldCenter = transform.TransformPoint(localCenter);

                // Transform local extents axes to world space (handles rotation & scale)
                Vector3 worldAxisX = transform.TransformVector(new Vector3(localHalfExtents.x, 0, 0));
                Vector3 worldAxisY = transform.TransformVector(new Vector3(0, localHalfExtents.y, 0));
                Vector3 worldAxisZ = transform.TransformVector(new Vector3(0, 0, localHalfExtents.z));

                // Sum absolute components to get new AABB extents
                float x = Mathf.Abs(worldAxisX.x) + Mathf.Abs(worldAxisY.x) + Mathf.Abs(worldAxisZ.x);
                float y = Mathf.Abs(worldAxisX.y) + Mathf.Abs(worldAxisY.y) + Mathf.Abs(worldAxisZ.y);
                float z = Mathf.Abs(worldAxisX.z) + Mathf.Abs(worldAxisY.z) + Mathf.Abs(worldAxisZ.z);

                return new Bounds(worldCenter, new Vector3(x, y, z) * 2.0f);
            }
        }

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
        
        private int[] _allocatedPages;
        public int[] AllocatedPages => _allocatedPages;
        
        // --- Events ---
        public event Action OnRegenerationComplete;

        private void OnEnable() { VoxelVolumeRegistry.Register(this); }
        private void OnDisable() { VoxelVolumeRegistry.Unregister(this); }

        public void AssignMemorySlice(VoxelVolumePool pool, int nodeOffset, int payloadOffset, int brickOffset, int nodes, int bricks, int pageTableOffset, int[] pages)
        {
            _maxNodes = nodes;
            _maxBricks = bricks;
            _allocatedPages = pages;
            
            BufferManager = new SVOBufferManager(
                pool.GlobalNodeBuffer, nodeOffset,
                pool.GlobalPayloadBuffer, payloadOffset,
                pool.GlobalBrickDataBuffer, brickOffset,
                pool.GlobalPageTableBuffer, pageTableOffset
            );
        }

        public void OnPullFromPool(Vector3 worldOrigin, float size)
        {
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

        private void OnDrawGizmosSelected()
        {
            if (WorldSize > 0)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireCube(WorldBounds.center, WorldBounds.size);
            }
        }
    }
}
