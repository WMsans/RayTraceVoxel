using System.Collections.Generic;
using UnityEngine;

namespace VoxelEngine.Core.Streaming
{
    public class VoxelVolumePool : MonoBehaviour
    {
        public static VoxelVolumePool Instance { get; private set; }

        [Header("Pool Settings")]
        public VoxelVolume prefab;
        public int poolSize = 100;
        public Transform poolContainer;

        [Header("Volume Memory Settings")]
        [Tooltip("Max nodes per volume. Reduce to save VRAM if poolSize is high.")]
        public int maxNodesPerVolume = 50000; 
        public int maxBricksPerVolume = 25000;

        private Queue<VoxelVolume> _pool = new Queue<VoxelVolume>();
        private List<VoxelVolume> _allInstances = new List<VoxelVolume>();

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            InitializePool();
        }

        private void InitializePool()
        {
            if (prefab == null) return;

            if (poolContainer == null) poolContainer = this.transform;

            Debug.Log($"VoxelVolumePool: Allocating {poolSize} volumes...");

            for (int i = 0; i < poolSize; i++)
            {
                VoxelVolume vol = Instantiate(prefab, poolContainer);
                vol.gameObject.name = $"Volume_Pool_{i}";
                
                // Allocate GPU Buffers immediately
                vol.InitializeForPool(maxNodesPerVolume, maxBricksPerVolume);
                
                // Disable GameObject (removes from Registry, stops rendering)
                vol.gameObject.SetActive(false);
                
                _pool.Enqueue(vol);
                _allInstances.Add(vol);
            }

            Debug.Log($"VoxelVolumePool: Allocation Complete.");
        }

        public VoxelVolume GetVolume(Vector3 position, float size)
        {
            if (_pool.Count == 0)
            {
                Debug.LogWarning("VoxelVolumePool: Empty! Consider increasing pool size or reducing LOD distance.");
                return null;
            }

            VoxelVolume vol = _pool.Dequeue();
            
            // Set Transform
            vol.transform.position = position;
            // Assuming Resolution is constant (e.g., 64), we scale the volume to match the Octree Node Size
            float scale = size / vol.Resolution; 
            vol.transform.localScale = Vector3.one * scale;

            // Activate (Calls Generate)
            vol.OnPullFromPool(position, size);

            return vol;
        }

        public void ReturnVolume(VoxelVolume vol)
        {
            if (vol == null) return;

            vol.OnReturnToPool();
            vol.transform.SetParent(poolContainer); 
            _pool.Enqueue(vol);
        }

        private void OnDestroy()
        {
            // Ensure all buffers are released when app closes
            foreach (var vol in _allInstances)
            {
                // Rely on Unity's OnDestroy in VoxelVolume to release buffers
            }
        }
    }
}