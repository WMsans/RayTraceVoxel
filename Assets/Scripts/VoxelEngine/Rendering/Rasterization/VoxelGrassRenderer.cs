using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule; // [Fix] Required for RasterCommandBuffer
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Editing; // Required for VoxelEditManager
using System.Collections.Generic;

namespace VoxelEngine.Core.Rendering
{
    [RequireComponent(typeof(VoxelVolume))]
    public class VoxelGrassRenderer : MonoBehaviour
    {
        // --- Static Registry for Render Pass ---
        public static HashSet<VoxelGrassRenderer> ActiveRenderers = new HashSet<VoxelGrassRenderer>();

        [Header("Shaders")]
        public ComputeShader grassCompute;
        public Shader grassShader;

        [Header("Generation Settings")]
        public int maxInstances = 100000;
        public int targetMaterialId = 4; 
        [Range(0f, 1f)] public float sdfThreshold = 0.5f;
        [Range(-1f, 1f)] public float normalYThreshold = 0.5f;

        [Header("Visual Settings")]
        public float bladeWidth = 0.2f;
        public float bladeHeight = 1.0f;
        public Color baseColor = new Color(0.1f, 0.3f, 0.1f);
        public Color tipColor = new Color(0.4f, 0.6f, 0.2f);

        [Header("Wind Settings")]
        public Texture2D windTexture;
        public float windSpeed = 1.0f;
        public float windStrength = 0.5f;
        public float windFrequency = 0.1f;
        public Vector2 windDirection = new Vector2(1f, 0.5f);

        // --- Buffers ---
        private ComputeBuffer _grassAppendBuffer;
        private ComputeBuffer _indirectArgsBuffer;
        private uint[] _argsData = new uint[] { 0, 0, 0, 0, 0 };
        
        // --- References ---
        private VoxelVolume _volume;
        private Material _grassMaterial;
        private Mesh _grassMesh;
        private Bounds _renderBounds;
        
        // --- LOD State ---
        private float _lodScale = 1.0f;

        private void Awake()
        {
            _volume = GetComponent<VoxelVolume>();
            _grassMesh = GrassMeshGenerator.GenerateBlade(1f, 1f); 
            
            if (grassShader != null)
                _grassMaterial = new Material(grassShader);
            
            InitializeBuffers();
        }

        private void OnEnable()
        {
            _volume.OnRegenerationComplete += OnVolumeRegenerated;
            ActiveRenderers.Add(this);
        }

        private void OnDisable()
        {
            _volume.OnRegenerationComplete -= OnVolumeRegenerated;
            ActiveRenderers.Remove(this);
        }

        private void OnDestroy()
        {
            ReleaseBuffers();
            if (_grassMaterial) Destroy(_grassMaterial);
            if (_grassMesh) Destroy(_grassMesh);
        }

        private void InitializeBuffers()
        {
            _grassAppendBuffer = new ComputeBuffer(maxInstances, 20, ComputeBufferType.Append);
            _indirectArgsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        }

        private void ReleaseBuffers()
        {
            _grassAppendBuffer?.Release();
            _indirectArgsBuffer?.Release();
        }

        private void OnVolumeRegenerated()
        {
            if (grassCompute == null || !_volume.IsReady) return;
            
            // --- 0. Calculate LOD Scale ---
            // Lower LOD (further chunks) have larger voxels.
            // We scale the grass up to compensate for the lower density (fewer voxels per area).
            float currentVoxelSize = _volume.WorldSize / (float)_volume.Resolution;
            float baseVoxelSize = 1.0f;
            
            if (VoxelEditManager.Instance != null) 
                baseVoxelSize = VoxelEditManager.Instance.voxelSize;

            // Ratio of current voxel size to base size. 
            // e.g., Base=1.0, Current=2.0 (LOD1) -> Scale=2.0
            _lodScale = Mathf.Max(1.0f, currentVoxelSize / baseVoxelSize);

            // 1. Reset Counter
            _grassAppendBuffer.SetCounterValue(0);

            // 2. Dispatch Compute
            int kernel = grassCompute.FindKernel("GenerateGrass");
            grassCompute.SetBuffer(kernel, "_NodeBuffer", _volume.NodeBuffer);
            grassCompute.SetBuffer(kernel, "_PayloadBuffer", _volume.PayloadBuffer);
            grassCompute.SetBuffer(kernel, "_BrickDataBuffer", _volume.BrickDataBuffer);
            grassCompute.SetBuffer(kernel, "_PageTableBuffer", _volume.BufferManager.PageTableBuffer); // New
            grassCompute.SetBuffer(kernel, "_GrassAppendBuffer", _grassAppendBuffer);

            grassCompute.SetVector("_ChunkWorldOrigin", _volume.WorldOrigin);
            grassCompute.SetFloat("_ChunkWorldSize", _volume.WorldSize);
            grassCompute.SetInt("_GridSize", _volume.Resolution);
            
            grassCompute.SetInt("_NodeOffset", _volume.BufferManager.PageTableOffset); // Changed
            grassCompute.SetInt("_PayloadOffset", _volume.BufferManager.PageTableOffset); // Changed
            grassCompute.SetInt("_BrickOffset", _volume.BufferManager.BrickDataOffset);
            
            grassCompute.SetInt("_TargetMaterialId", targetMaterialId);
            grassCompute.SetFloat("_SdfThreshold", sdfThreshold);
            grassCompute.SetFloat("_NormalYThreshold", normalYThreshold);

            int groups = Mathf.CeilToInt((_volume.Resolution / 4.0f) / 4.0f);
            grassCompute.Dispatch(kernel, groups, groups, groups);
            
            // 3. Set Mesh Data FIRST (Index Count, Start Index, etc.)
            // We update the CPU array and upload it. Index [1] (Instance Count) is 0 here.
            _argsData[0] = (uint)_grassMesh.GetIndexCount(0);
            _argsData[1] = 0; // Placeholder, will be filled by CopyCount
            _argsData[2] = (uint)_grassMesh.GetIndexStart(0);
            _argsData[3] = (uint)_grassMesh.GetBaseVertex(0);
            _argsData[4] = 0; // Start Instance
            
            _indirectArgsBuffer.SetData(_argsData); 

            // 4. Copy Instance Count SECOND
            // This takes the count from the AppendBuffer and writes it into byte offset 4 
            // (which corresponds to the uint at index 1) of the indirect args buffer.
            ComputeBuffer.CopyCount(_grassAppendBuffer, _indirectArgsBuffer, 4); 
            
            _renderBounds = _volume.WorldBounds;
        }

        // --- Render Called by RenderFeature ---
        public void Draw(RasterCommandBuffer cmd)
        {
            if (_grassMaterial == null || _indirectArgsBuffer == null || !_volume.gameObject.activeInHierarchy) return;

            // Update Material Properties
            _grassMaterial.SetBuffer("_GrassInstanceBuffer", _grassAppendBuffer);
            _grassMaterial.SetColor("_BaseColor", baseColor);
            _grassMaterial.SetColor("_TipColor", tipColor);
            
            // [LOD Logic] Scale blade dimensions by the LOD scale calculated in OnVolumeRegenerated
            _grassMaterial.SetFloat("_BladeWidth", bladeWidth * _lodScale);
            _grassMaterial.SetFloat("_BladeHeight", bladeHeight * _lodScale);
            
            _grassMaterial.SetFloat("_WindSpeed", windSpeed);
            _grassMaterial.SetFloat("_WindStrength", windStrength);
            _grassMaterial.SetFloat("_WindFrequency", windFrequency);
            _grassMaterial.SetVector("_WindDirection", windDirection);

            if (windTexture != null)
                _grassMaterial.SetTexture("_WindTex", windTexture);

            // Issue Draw Call via RasterCommandBuffer
            cmd.DrawMeshInstancedIndirect(
                _grassMesh,
                0,
                _grassMaterial,
                0,
                _indirectArgsBuffer
            );
        }
    }
}