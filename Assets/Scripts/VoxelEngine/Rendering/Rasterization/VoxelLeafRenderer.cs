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
        public int targetMaterialId = 6; 
        [Range(0f, 1f)] public float sdfThreshold = 0.8f;

        [Header("Visual Settings")]
        public float leafScale = 0.8f;
        public Color innerColor = new Color(0.05f, 0.2f, 0.05f);
        public Color outerColor = new Color(0.1f, 0.4f, 0.1f);
        [Header("Cel Shading")]
        [Range(1, 10)] public int celSteps = 3;
        [Range(0.0f, 1.0f)] public float shadowBrightness = 0.2f;

        [Header("Wind Settings")]
        public Texture2D windTexture;
        public float windSpeed = 0.5f;
        public float windStrength = 0.2f;

        private ComputeBuffer _appendBuffer;
        private ComputeBuffer _argsBuffer;
        private uint[] _argsData = new uint[] { 0, 0, 0, 0, 0 };
        private bool _isDirty = true;
        
        private static Plane[] _frustumPlanes = new Plane[6];
        private static int _lastPlaneFrame = -1;

        private VoxelVolume _volume;
        private Material _material;
        private Mesh _mesh; 
        public Bounds _renderBounds; // Made public

        public struct LeafInstance
        {
            public Vector3 position;
            public uint packedNormal; 
            public uint packedData;   
        }

        private void Awake()
        {
            _volume = GetComponent<VoxelVolume>();
            _mesh = GrassMeshGenerator.GenerateBlade(1f, 1f); 
            
            if (leafShader != null)
                _material = new Material(leafShader);
        }

        private void OnEnable()
        {
            _volume.OnRegenerationComplete += Refresh;
            ActiveLeafRenderers.Add(this);
        }

        private void OnDisable()
        {
            _volume.OnRegenerationComplete -= Refresh;
            ActiveLeafRenderers.Remove(this);
            ReleaseBuffers();
        }

        private void OnDestroy()
        {
            ReleaseBuffers();
            if (_material) Destroy(_material);
            if (_mesh) Destroy(_mesh);
        }

        private void InitializeBuffers()
        {
            if (_appendBuffer != null) return;
            _appendBuffer = new ComputeBuffer(maxInstances, 20, ComputeBufferType.Append);
            _argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        }

        private void ReleaseBuffers()
        {
            _appendBuffer?.Release();
            _appendBuffer = null;
            _argsBuffer?.Release();
            _argsBuffer = null;
        }

        private void Update()
        {
            // [UPDATED] Update bounds for culling
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
                if (_appendBuffer == null)
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
                if (_appendBuffer != null)
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
            if (leafCompute == null || !_volume.IsReady || _appendBuffer == null) return;

            _appendBuffer.SetCounterValue(0);

            int kernel = leafCompute.FindKernel("GenerateLeaves");
            leafCompute.SetBuffer(kernel, "_NodeBuffer", _volume.NodeBuffer);
            leafCompute.SetBuffer(kernel, "_PayloadBuffer", _volume.PayloadBuffer);
            leafCompute.SetBuffer(kernel, "_BrickDataBuffer", _volume.BrickDataBuffer);
            leafCompute.SetBuffer(kernel, "_PageTableBuffer", _volume.BufferManager.PageTableBuffer); 
            leafCompute.SetBuffer(kernel, "_LeafAppendBuffer", _appendBuffer);

            // [UPDATED] Remove World Origin
            // leafCompute.SetVector("_ChunkWorldOrigin", _volume.WorldOrigin);
            leafCompute.SetFloat("_ChunkWorldSize", _volume.WorldSize);
            leafCompute.SetInt("_GridSize", _volume.Resolution);
            
            leafCompute.SetInt("_NodeOffset", _volume.BufferManager.PageTableOffset);
            leafCompute.SetInt("_PayloadOffset", _volume.BufferManager.PageTableOffset);
            leafCompute.SetInt("_BrickOffset", _volume.BufferManager.BrickDataOffset);
            
            leafCompute.SetInt("_TargetMaterialId", targetMaterialId);
            leafCompute.SetFloat("_SdfThreshold", sdfThreshold);

            int groups = Mathf.CeilToInt((_volume.Resolution / 4.0f) / 4.0f);
            leafCompute.Dispatch(kernel, groups, groups, groups);
            
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

            // [UPDATED] Pass Transform Matrix
            _material.SetMatrix("_ObjectToWorld", _volume.transform.localToWorldMatrix);

            _material.SetBuffer("_LeafInstanceBuffer", _appendBuffer);
            _material.SetColor("_BaseColor", innerColor);
            _material.SetColor("_TipColor", outerColor);
            
            _material.SetFloat("_BladeHeight", leafScale); 
            
            _material.SetFloat("_CelSteps", celSteps);
            _material.SetFloat("_ShadowBrightness", shadowBrightness);

            _material.SetFloat("_WindSpeed", windSpeed);
            _material.SetFloat("_WindStrength", windStrength);
            if (windTexture != null) _material.SetTexture("_WindTex", windTexture);

            cmd.DrawMeshInstancedIndirect(_mesh, 0, _material, 0, _argsBuffer);
        }
    }
}