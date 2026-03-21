using UnityEngine;
using UnityEngine.VFX;
using VoxelEngine.Core.Data;

namespace VoxelEngine.Core.Effects
{
    public class VoxelVFXManager : MonoBehaviour
    {
        public static VoxelVFXManager Instance { get; private set; }

        [Header("VFX Configuration")]
        [SerializeField] private VisualEffect debrisVFXPrefab;
        [SerializeField] private int poolSize = 3;

        private VisualEffect[] _vfxPool;
        private int _poolIndex;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            
            InitializePool();
        }

        private void InitializePool()
        {
            _vfxPool = new VisualEffect[poolSize];
            for (int i = 0; i < poolSize; i++)
            {
                _vfxPool[i] = Instantiate(debrisVFXPrefab, transform);
                _vfxPool[i].Stop();
            }
        }

        public void SpawnDebris(Vector3 position, float radius, int materialId)
        {
            VisualEffect vfx = _vfxPool[_poolIndex];
            _poolIndex = (_poolIndex + 1) % poolSize;

            vfx.transform.position = position;
            vfx.SetFloat("Radius", radius);
            
            int textureIndex = VoxelDefinitionManager.Instance != null 
                ? VoxelDefinitionManager.Instance.GetAlbedoTextureIndex(materialId) 
                : 0;
            vfx.SetInt("MaterialID", textureIndex);
            
            vfx.Reinit();
            Debug.Log("On Spawn");
            vfx.Play();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
