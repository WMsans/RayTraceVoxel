using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Editing;
using System.Collections.Generic;

namespace VoxelEngine.Core.Rendering
{
    [RequireComponent(typeof(VoxelVolume))]
    public class VoxelLeafRenderer : MonoBehaviour
    {        
        public static HashSet<VoxelLeafRenderer> ActiveLeafRenderers = new HashSet<VoxelLeafRenderer>();

        [Header("Shaders")]
        public ComputeShader leafCompute;
        public Shader leafShader;

        [Header("Generation Settings")]
        public int maxInstances = 50000;
        public int targetMaterialId = 6; // Default for Leaves
        [Range(0f, 1f)] public float sdfThreshold = 0.8f;

        [Header("Visual Settings")]
        public float leafScale = 0.8f;
        public Color innerColor = new Color(0.05f, 0.2f, 0.05f);
        public Color outerColor = new Color(0.1f, 0.4f, 0.1f);

        [Header("Wind Settings")]
        public Texture2D windTexture;
        public float windSpeed = 0.5f;
        public float windStrength = 0.2f;

        // --- Buffers ---
        private ComputeBuffer _appendBuffer;
        private ComputeBuffer _argsBuffer;
        private uint[] _argsData = new uint[] { 0, 0, 0, 0, 0 };
        
        private VoxelVolume _volume;
        private Material _material;
        private Mesh _mesh; // Reusing the Cross-Quad mesh
        
        // Removed _lodScale usage for leaves as requested
        // private float _lodScale = 1.0f; 

        private void Awake()
        {
            _volume = GetComponent<VoxelVolume>();
            // Using the existing mesh generator - a cross-quad works well for "tufts" of leaves
            _mesh = GrassMeshGenerator.GenerateBlade(1f, 1f); 
            
            if (leafShader != null)
                _material = new Material(leafShader);
            
            _appendBuffer = new ComputeBuffer(maxInstances, 20, ComputeBufferType.Append);
            _argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        }

        private void OnEnable()
        {
            _volume.OnRegenerationComplete += OnVolumeRegenerated;
            ActiveLeafRenderers.Add(this);
        }

        private void OnDisable()
        {
            _volume.OnRegenerationComplete -= OnVolumeRegenerated;
            ActiveLeafRenderers.Remove(this);
        }

        private void OnDestroy()
        {
            _appendBuffer?.Release();
            _argsBuffer?.Release();
            if (_material) Destroy(_material);
            if (_mesh) Destroy(_mesh);
        }

        private void OnVolumeRegenerated()
        {
            if (leafCompute == null || !_volume.IsReady) return;

            // [CHANGE] LOD Scaling removed for leaves.
            // float currentVoxelSize = _volume.WorldSize / (float)_volume.Resolution;
            // float baseVoxelSize = (VoxelEditManager.Instance != null) ? VoxelEditManager.Instance.voxelSize : 1.0f;
            // _lodScale = Mathf.Max(1.0f, currentVoxelSize / baseVoxelSize);

            _appendBuffer.SetCounterValue(0);

            int kernel = leafCompute.FindKernel("GenerateLeaves");
            leafCompute.SetBuffer(kernel, "_NodeBuffer", _volume.NodeBuffer);
            leafCompute.SetBuffer(kernel, "_PayloadBuffer", _volume.PayloadBuffer);
            leafCompute.SetBuffer(kernel, "_BrickDataBuffer", _volume.BrickDataBuffer);
            leafCompute.SetBuffer(kernel, "_PageTableBuffer", _volume.BufferManager.PageTableBuffer); // New
            leafCompute.SetBuffer(kernel, "_LeafAppendBuffer", _appendBuffer);

            leafCompute.SetVector("_ChunkWorldOrigin", _volume.WorldOrigin);
            leafCompute.SetFloat("_ChunkWorldSize", _volume.WorldSize);
            leafCompute.SetInt("_GridSize", _volume.Resolution);
            
            leafCompute.SetInt("_NodeOffset", _volume.BufferManager.PageTableOffset); // Changed
            leafCompute.SetInt("_PayloadOffset", _volume.BufferManager.PageTableOffset); // Changed
            leafCompute.SetInt("_BrickOffset", _volume.BufferManager.BrickDataOffset);
            
            leafCompute.SetInt("_TargetMaterialId", targetMaterialId);
            leafCompute.SetFloat("_SdfThreshold", sdfThreshold);

            int groups = Mathf.CeilToInt((_volume.Resolution / 4.0f) / 4.0f);
            leafCompute.Dispatch(kernel, groups, groups, groups);
            
            // Set Args
            _argsData[0] = (uint)_mesh.GetIndexCount(0);
            _argsData[1] = 0; 
            _argsData[2] = (uint)_mesh.GetIndexStart(0);
            _argsData[3] = (uint)_mesh.GetBaseVertex(0);
            _argsData[4] = 0; 
            
            _argsBuffer.SetData(_argsData); 
            ComputeBuffer.CopyCount(_appendBuffer, _argsBuffer, 4); 
        }

        public void Draw(RasterCommandBuffer cmd)
        {
            if (_material == null || _argsBuffer == null || !_volume.gameObject.activeInHierarchy) return;

            _material.SetBuffer("_LeafInstanceBuffer", _appendBuffer);
            _material.SetColor("_BaseColor", innerColor);
            _material.SetColor("_TipColor", outerColor);
            
            // [CHANGE] Removed _lodScale multiplication. Leaves are now constant size.
            _material.SetFloat("_BladeHeight", leafScale); 
            
            _material.SetFloat("_WindSpeed", windSpeed);
            _material.SetFloat("_WindStrength", windStrength);
            if (windTexture != null) _material.SetTexture("_WindTex", windTexture);

            cmd.DrawMeshInstancedIndirect(_mesh, 0, _material, 0, _argsBuffer);
        }
    }
}