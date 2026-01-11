using System.Collections.Generic;
using UnityEngine;
using System.Runtime.InteropServices;

namespace VoxelEngine.Core.Data
{
    /// <summary>
    /// Manages the registry of VoxelDefinitions and packs them into GPU-compatible formats
    /// (TextureArrays and ComputeBuffers).
    /// </summary>
    [CreateAssetMenu(fileName = "VoxelDefinitionManager", menuName = "Voxel/Voxel Definition Manager")]
    public class VoxelDefinitionManager : ScriptableObjectSingleton<VoxelDefinitionManager>
    {
        [Header("Configuration")]
        [Tooltip("The resolution for all textures in the arrays. Textures will be resized to this.")]
        public int textureResolution = 256;
        
        [Tooltip("List of Voxel Definitions. The order determines the ID (index 0 is usually Air/Empty).")]
        public List<VoxelDefinition> definitions = new List<VoxelDefinition>();

        [Header("GPU Data")]
        public Texture2DArray albedoTextureArray;
        public Texture2DArray normalTextureArray;
        public Texture2DArray maskTextureArray;

        [SerializeField, HideInInspector]
        private List<VoxelTypeGPU> _packedGpuData = new List<VoxelTypeGPU>();
        
        private GraphicsBuffer _voxelMaterialBuffer;
        public GraphicsBuffer VoxelMaterialBuffer
        {
            get
            {
                // Initialize if buffer is missing but data exists
                if (_voxelMaterialBuffer == null && _packedGpuData != null && _packedGpuData.Count > 0)
                {
                    Initialize();
                }
                return _voxelMaterialBuffer;
            }
        }

        private void OnDisable()
        {
            if (_voxelMaterialBuffer != null)
            {
                _voxelMaterialBuffer.Release();
                _voxelMaterialBuffer = null;
            }
        }

        public void Initialize()
        {
            if (_packedGpuData == null || _packedGpuData.Count == 0)
            {
                Debug.LogWarning("VoxelDefinitionManager: No packed GPU data found. Please pack the atlas in the editor.");
                return;
            }

            // Clean up old data
            if (_voxelMaterialBuffer != null) _voxelMaterialBuffer.Release();

            // --- Upload Buffer ---
            // Ensure the buffer is allocated for the full count of the packed data
            _voxelMaterialBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _packedGpuData.Count, Marshal.SizeOf<VoxelTypeGPU>());
            _voxelMaterialBuffer.SetData(_packedGpuData);

            Debug.Log($"VoxelDefinitionManager: Initialized. {_packedGpuData.Count} GPU Entries.");
        }

        /// <summary>
        /// Called by the Editor script to set the packed data.
        /// </summary>
        public void SetPackedData(List<VoxelTypeGPU> gpuData, Texture2DArray albedo, Texture2DArray normal, Texture2DArray mask)
        {
            _packedGpuData = gpuData;
            albedoTextureArray = albedo;
            normalTextureArray = normal;
            maskTextureArray = mask;

            // FIX: Force re-initialization of the buffer so the new definitions are uploaded to the GPU immediately.
            // This ensures the buffer expands to fit the new gpuData count.
            Initialize();
        }
    }
}