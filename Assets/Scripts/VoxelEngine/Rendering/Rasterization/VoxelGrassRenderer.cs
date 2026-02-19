using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule; 
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Editing; 
using System.Collections.Generic;

namespace VoxelEngine.Core.Rendering
{
    [RequireComponent(typeof(VoxelVolume))]
    public class VoxelGrassRenderer : MonoBehaviour
    {
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
        [Header("Cel Shading")]
        [Range(1, 10)] public int celSteps = 3;
        [Range(0.0f, 1.0f)] public float shadowBrightness = 0.2f;

        [Header("Wind Settings")]
        public Texture2D windTexture;
        public float windSpeed = 1.0f;
        public float windStrength = 0.5f;
        public float windFrequency = 0.1f;
        public Vector2 windDirection = new Vector2(1f, 0.5f);

        private ComputeBuffer _grassAppendBuffer;
        private ComputeBuffer _indirectArgsBuffer;
        private uint[] _argsData = new uint[] { 0, 0, 0, 0, 0 };
        private bool _isDirty = true;
        
        private static Plane[] _frustumPlanes = new Plane[6];
        private static int _lastPlaneFrame = -1;

        private VoxelVolume _volume;
        private Material _grassMaterial;
        private Mesh _grassMesh;
        public Bounds _renderBounds; // Made public for external culling if needed
        
        private float _lodScale = 1.0f;

        private void Awake()
        {
            _volume = GetComponent<VoxelVolume>();
            _grassMesh = GrassMeshGenerator.GenerateBlade(1f, 1f); 
            
            if (grassShader != null)
                _grassMaterial = new Material(grassShader);
        }

        private void OnEnable()
        {
            _volume.OnRegenerationComplete += Refresh;
            ActiveRenderers.Add(this);
        }

        private void OnDisable()
        {
            _volume.OnRegenerationComplete -= Refresh;
            ActiveRenderers.Remove(this);
            ReleaseBuffers();
        }

        private void OnDestroy()
        {
            ReleaseBuffers();
            if (_grassMaterial) Destroy(_grassMaterial);
            if (_grassMesh) Destroy(_grassMesh);
        }

        private void InitializeBuffers()
        {
            if (_grassAppendBuffer != null) return;
            _grassAppendBuffer = new ComputeBuffer(maxInstances, 20, ComputeBufferType.Append);
            _indirectArgsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        }

        private void ReleaseBuffers()
        {
            _grassAppendBuffer?.Release();
            _grassAppendBuffer = null;
            _indirectArgsBuffer?.Release();
            _indirectArgsBuffer = null;
        }

        private void Update()
        {
            // [UPDATED] Keep render bounds fresh for culling systems
            _renderBounds = _volume.WorldBounds;

            if (Time.frameCount != _lastPlaneFrame)
            {
                if (Camera.main != null)
                {
                    GeometryUtility.CalculateFrustumPlanes(Camera.main, _frustumPlanes);
                    _lastPlaneFrame = Time.frameCount;
                }
            }

            bool visible = GeometryUtility.TestPlanesAABB(_frustumPlanes, _volume.WorldBounds);

            if (visible)
            {
                if (_grassAppendBuffer == null)
                {
                    InitializeBuffers();
                    _isDirty = true;
                }

                if (_isDirty && _volume.IsReady)
                {
                    DispatchGeneration();
                    _isDirty = false;
                }
            }
            else
            {
                if (_grassAppendBuffer != null)
                {
                    ReleaseBuffers();
                }
            }
        }

        public void Refresh()
        {
            _isDirty = true;
        }

        private void DispatchGeneration()
        {
            if (grassCompute == null || !_volume.IsReady || _grassAppendBuffer == null) return;
            
            float currentVoxelSize = _volume.WorldSize / (float)_volume.Resolution;
            float baseVoxelSize = 1.0f;
            
            if (VoxelEditManager.Instance != null) 
                baseVoxelSize = VoxelEditManager.Instance.voxelSize;

            _lodScale = Mathf.Max(1.0f, currentVoxelSize / baseVoxelSize);

            _grassAppendBuffer.SetCounterValue(0);

            int kernel = grassCompute.FindKernel("GenerateGrass");
            grassCompute.SetBuffer(kernel, "_NodeBuffer", _volume.NodeBuffer);
            grassCompute.SetBuffer(kernel, "_PayloadBuffer", _volume.PayloadBuffer);
            grassCompute.SetBuffer(kernel, "_BrickDataBuffer", _volume.BrickDataBuffer);
            grassCompute.SetBuffer(kernel, "_PageTableBuffer", _volume.BufferManager.PageTableBuffer);
            grassCompute.SetBuffer(kernel, "_GrassAppendBuffer", _grassAppendBuffer);

            // [UPDATED] World Origin not needed in compute anymore (it produces local coords)
            // grassCompute.SetVector("_ChunkWorldOrigin", _volume.WorldOrigin); 
            
            grassCompute.SetFloat("_ChunkWorldSize", _volume.WorldSize);
            grassCompute.SetInt("_GridSize", _volume.Resolution);
            
            grassCompute.SetInt("_NodeOffset", _volume.BufferManager.PageTableOffset);
            grassCompute.SetInt("_PayloadOffset", _volume.BufferManager.PageTableOffset);
            grassCompute.SetInt("_BrickOffset", _volume.BufferManager.BrickDataOffset);
            
            grassCompute.SetInt("_TargetMaterialId", targetMaterialId);
            grassCompute.SetFloat("_SdfThreshold", sdfThreshold);
            grassCompute.SetFloat("_NormalYThreshold", normalYThreshold);

            int groups = Mathf.CeilToInt((_volume.Resolution / 4.0f) / 4.0f);
            grassCompute.Dispatch(kernel, groups, groups, groups);
            
            _argsData[0] = (uint)_grassMesh.GetIndexCount(0);
            _argsData[1] = 0; 
            _argsData[2] = (uint)_grassMesh.GetIndexStart(0);
            _argsData[3] = (uint)_grassMesh.GetBaseVertex(0);
            _argsData[4] = 0; 
            
            _indirectArgsBuffer.SetData(_argsData); 

            ComputeBuffer.CopyCount(_grassAppendBuffer, _indirectArgsBuffer, 4); 
            
            _renderBounds = _volume.WorldBounds;
        }

        public void Draw(RasterCommandBuffer cmd)
        {
            if (_grassMaterial == null || _indirectArgsBuffer == null || !_volume.gameObject.activeInHierarchy) return;

            // [UPDATED] Pass the dynamic transform matrix
            _grassMaterial.SetMatrix("_ObjectToWorld", _volume.transform.localToWorldMatrix);

            _grassMaterial.SetBuffer("_GrassInstanceBuffer", _grassAppendBuffer);
            _grassMaterial.SetColor("_BaseColor", baseColor);
            _grassMaterial.SetColor("_TipColor", tipColor);
            
            _grassMaterial.SetFloat("_BladeWidth", bladeWidth);
            _grassMaterial.SetFloat("_BladeHeight", bladeHeight);
            
            _grassMaterial.SetFloat("_CelSteps", celSteps);
            _grassMaterial.SetFloat("_ShadowBrightness", shadowBrightness);
            
            _grassMaterial.SetFloat("_WindSpeed", windSpeed);
            _grassMaterial.SetFloat("_WindStrength", windStrength);
            _grassMaterial.SetFloat("_WindFrequency", windFrequency);
            _grassMaterial.SetVector("_WindDirection", windDirection);

            if (windTexture != null)
                _grassMaterial.SetTexture("_WindTex", windTexture);

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