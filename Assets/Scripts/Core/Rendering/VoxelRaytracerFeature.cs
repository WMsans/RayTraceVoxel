using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using VoxelEngine.Core;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Streaming;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelRaytracerFeature : ScriptableRendererFeature
    {
        public enum QualityLevel
        {
            High,
            Low,
            Custom
        }

        [System.Serializable]
        public class Settings
        {
            public ComputeShader raytraceShader;
            public Shader compositeShader;
            public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingSkybox;

            [Header("Quality")]
            public QualityLevel qualityLevel = QualityLevel.High;
            [Tooltip("Render scale used when Quality Level is set to Custom.")]
            [Range(0.1f, 1.0f)]
            public float renderScale = 1.0f;
            public int iterations = 128;
            public int marchSteps = 64;
            
            [Header("LOD Settings")]
            [Tooltip("Multiplies the pixel size estimate. Higher values (10-100) force LODs to appear closer.")]
            [Range(1.0f, 200.0f)] 
            public float lodBias = 1.0f;

            [Header("Culling")]
            [Tooltip("If true, chunks beyond the Camera's Far Clip Plane will be hidden. Disable this to see distant voxel terrain.")]
            public bool useCameraFarPlane = false; 

            [Header("Dithering")]
            public Texture2D blueNoiseTexture;
        }

        public Settings settings = new Settings();
        private VoxelRaytracerPass _pass;
        private Material _compositeMaterial;

        public static Vector2 MousePosition;
        public static GraphicsBuffer RaycastHitBuffer;

        public override void Create()
        {
            _pass = new VoxelRaytracerPass(settings);
            if (settings.compositeShader != null)
                _compositeMaterial = new Material(settings.compositeShader);
            else
                _compositeMaterial = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/Universal Render Pipeline/Blit"));
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (settings.raytraceShader == null) return;
            if (VoxelVolumePool.Instance == null) return;

            // Update settings live
            _pass.UpdateSettings(settings);
            _pass.Setup(_compositeMaterial);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_compositeMaterial);
            _pass?.Dispose();
        }

        class VoxelRaytracerPass : ScriptableRenderPass
        {
            private Settings _settings;
            private ComputeShader _shader;
            private Material _compositeMaterial;

            // Shader IDs
            private static readonly int _ResultParams = Shader.PropertyToID("_Result");
            private static readonly int _ResultDepthParams = Shader.PropertyToID("_ResultDepth");
            private static readonly int _CameraToWorldParams = Shader.PropertyToID("_CameraToWorld");
            private static readonly int _CameraInverseProjectionParams = Shader.PropertyToID("_CameraInverseProjection");
            private static readonly int _CameraDepthTextureParams = Shader.PropertyToID("_CameraDepthTexture");
            private static readonly int _VoxelDepthTextureParams = Shader.PropertyToID("_VoxelDepthTexture");
            private static readonly int _ZBufferParamsID = Shader.PropertyToID("_ZBufferParams");
            private static readonly int _RaytraceParams = Shader.PropertyToID("_RaytraceParams");
            private static readonly int _GlobalNodeBufferParams = Shader.PropertyToID("_GlobalNodeBuffer");
            private static readonly int _GlobalPayloadBufferParams = Shader.PropertyToID("_GlobalPayloadBuffer");
            private static readonly int _GlobalBrickDataBufferParams = Shader.PropertyToID("_GlobalBrickDataBuffer");
            private static readonly int _TLASGridBufferParams = Shader.PropertyToID("_TLASGridBuffer");
            private static readonly int _TLASChunkIndexBufferParams = Shader.PropertyToID("_TLASChunkIndexBuffer");
            private static readonly int _TLASBoundsMinParams = Shader.PropertyToID("_TLASBoundsMin");
            private static readonly int _TLASBoundsMaxParams = Shader.PropertyToID("_TLASBoundsMax");
            private static readonly int _TLASResolutionParams = Shader.PropertyToID("_TLASResolution");
            private static readonly int _ChunkBufferParams = Shader.PropertyToID("_ChunkBuffer");
            private static readonly int _ChunkCountParams = Shader.PropertyToID("_ChunkCount");
            private static readonly int _VoxelMaterialBufferParams = Shader.PropertyToID("_VoxelMaterialBuffer");
            private static readonly int _AlbedoTextureArrayParams = Shader.PropertyToID("_AlbedoTextureArray");
            private static readonly int _NormalTextureArrayParams = Shader.PropertyToID("_NormalTextureArray");
            private static readonly int _MaskTextureArrayParams = Shader.PropertyToID("_MaskTextureArray");
            private static readonly int _MainLightPositionParams = Shader.PropertyToID("_MainLightPosition");
            private static readonly int _MainLightColorParams = Shader.PropertyToID("_MainLightColor");
            private static readonly int _RaycastBufferParams = Shader.PropertyToID("_RaycastBuffer");
            private static readonly int _FrameCountParams = Shader.PropertyToID("_FrameCount");
            private static readonly int _BlueNoiseTextureParams = Shader.PropertyToID("_BlueNoiseTexture");
            private static readonly int _MousePositionParams = Shader.PropertyToID("_MousePosition");
            private static readonly int _MaxIterationsParams = Shader.PropertyToID("_MaxIterations");
            private static readonly int _MaxMarchStepsParams = Shader.PropertyToID("_MaxMarchSteps");

            private RTHandle _albedoHandle;
            private RTHandle _normalHandle;
            private RTHandle _maskHandle;
            private RTHandle _blueNoiseHandle;

            public VoxelRaytracerPass(Settings settings)
            {
                _settings = settings;
                _shader = settings.raytraceShader;
                renderPassEvent = settings.injectionPoint;
            }

            public void UpdateSettings(Settings newSettings) { _settings = newSettings; }
            public void Setup(Material mat) { _compositeMaterial = mat; }

            public void Dispose()
            {
                _albedoHandle?.Release(); 
                _normalHandle?.Release(); 
                _maskHandle?.Release();
                _blueNoiseHandle?.Release();
                if (VoxelRaytracerFeature.RaycastHitBuffer != null)
                {
                    VoxelRaytracerFeature.RaycastHitBuffer.Release();
                    VoxelRaytracerFeature.RaycastHitBuffer = null;
                }
            }            
            private void CheckTextureHandle(ref RTHandle handle, Texture texture)
            {
                if (texture == null) return;
                if (handle == null || handle.rt != texture) { handle?.Release(); handle = RTHandles.Alloc(texture); }
            }

            private class PassData
            {
                public ComputeShader computeShader;
                public int kernel;
                public TextureHandle targetColor;
                public TextureHandle targetDepth;
                public TextureHandle sourceDepth;
                public Matrix4x4 cameraToWorld;
                public Matrix4x4 cameraInverseProjection;
                public Vector4 zBufferParams;
                public int width; public int height;
                public Vector4 mainLightPosition;
                public Vector4 mainLightColor;
                public Vector4 raytraceParams; 
                public GraphicsBuffer nodeBuffer;
                public GraphicsBuffer payloadBuffer;
                public GraphicsBuffer brickDataBuffer;
                public GraphicsBuffer tlasGridBuffer;
                public GraphicsBuffer tlasChunkIndexBuffer;
                public Vector3 tlasBoundsMin;
                public Vector3 tlasBoundsMax;
                public int tlasResolution;
                public GraphicsBuffer chunkBuffer;
                public int chunkCount;
                public GraphicsBuffer materialBuffer;
                public GraphicsBuffer raycastBuffer;
                public TextureHandle albedoArray;
                public TextureHandle normalArray;
                public TextureHandle maskArray;
                public int frameCount;
                public TextureHandle blueNoise;
                public Vector2 mousePosition;
                public int maxIterations;
                public int maxMarchSteps;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (VoxelVolumePool.Instance == null) return;

                var cameraData = frameData.Get<UniversalCameraData>();
                
                Plane[] allPlanes = GeometryUtility.CalculateFrustumPlanes(cameraData.camera);
                Plane[] cullingPlanes;

                if (_settings.useCameraFarPlane)
                {
                    cullingPlanes = allPlanes;
                }
                else
                {
                    cullingPlanes = new Plane[5];
                    for (int i = 0; i < 5; i++)
                    {
                        cullingPlanes[i] = allPlanes[i];
                    }
                }

                VoxelVolumePool.Instance.UpdateVisibility(cullingPlanes);

                if (VoxelVolumePool.Instance.VisibleChunkCount == 0) return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var lightData = frameData.Get<UniversalLightData>();
                var cameraDesc = cameraData.cameraTargetDescriptor;

                float currentScale = 1.0f;
                int iterations = 128;
                int marchSteps = 64;

                switch (_settings.qualityLevel)
                {
                    case QualityLevel.High: 
                        currentScale = 1.0f; 
                        iterations = 128;
                        marchSteps = 64;
                        break;
                    case QualityLevel.Low: 
                        currentScale = 0.5f; 
                        iterations = 64; // Reduced from 128
                        marchSteps = 32; // Reduced from 64
                        break;
                    case QualityLevel.Custom: 
                        currentScale = _settings.renderScale; 
                        iterations = _settings.iterations;
                        marchSteps = _settings.marchSteps;
                        break;
                }

                int scaledWidth = Mathf.Max(1, Mathf.RoundToInt(cameraDesc.width * currentScale));
                int scaledHeight = Mathf.Max(1, Mathf.RoundToInt(cameraDesc.height * currentScale));

                TextureDesc colorDesc = new TextureDesc(scaledWidth, scaledHeight);
                colorDesc.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
                colorDesc.enableRandomWrite = true;
                colorDesc.name = "VoxelRaytraceResult";
                TextureHandle tempResult = renderGraph.CreateTexture(colorDesc);

                TextureDesc depthDesc = new TextureDesc(scaledWidth, scaledHeight);
                depthDesc.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat;
                depthDesc.enableRandomWrite = true;
                depthDesc.name = "VoxelRaytraceDepth";
                TextureHandle tempResultDepth = renderGraph.CreateTexture(depthDesc);

                CheckTextureHandle(ref _albedoHandle, VoxelDefinitionManager.Instance.albedoTextureArray);
                CheckTextureHandle(ref _normalHandle, VoxelDefinitionManager.Instance.normalTextureArray);
                CheckTextureHandle(ref _maskHandle, VoxelDefinitionManager.Instance.maskTextureArray);
                CheckTextureHandle(ref _blueNoiseHandle, _settings.blueNoiseTexture);

                SetupLights(lightData, out var mainPos, out var mainCol);

                float fov = cameraData.camera.fieldOfView;
                float height = cameraDesc.height; // Use original height for spread calculation to keep LOD consistent
                float rawPixelSpread = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 2.0f / height;
                float finalSpread = rawPixelSpread * _settings.lodBias;

                using (var builder = renderGraph.AddComputePass("Voxel Raytracer Single-Dispatch", out PassData data))
                {
                    data.computeShader = _shader;
                    data.kernel = _shader.FindKernel("CSMain");
                    
                    if (VoxelRaytracerFeature.RaycastHitBuffer == null || !VoxelRaytracerFeature.RaycastHitBuffer.IsValid())
                         VoxelRaytracerFeature.RaycastHitBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
                    data.raycastBuffer = VoxelRaytracerFeature.RaycastHitBuffer;

                    var pool = VoxelVolumePool.Instance;
                    data.nodeBuffer = pool.GlobalNodeBuffer;
                    data.payloadBuffer = pool.GlobalPayloadBuffer;
                    data.brickDataBuffer = pool.GlobalBrickDataBuffer;
                    data.chunkBuffer = pool.ChunkBuffer;
                    data.chunkCount = pool.VisibleChunkCount;
                    data.tlasGridBuffer = pool.TLASGridBuffer;
                    data.tlasChunkIndexBuffer = pool.TLASChunkIndexBuffer;
                    data.tlasBoundsMin = pool.TLASBoundsMin;
                    data.tlasBoundsMax = pool.TLASBoundsMax;
                    data.tlasResolution = pool.TLASResolution;
                    data.frameCount = Time.frameCount;

                    data.materialBuffer = VoxelDefinitionManager.Instance.VoxelMaterialBuffer;
                    if (_albedoHandle != null) data.albedoArray = renderGraph.ImportTexture(_albedoHandle);
                    if (_normalHandle != null) data.normalArray = renderGraph.ImportTexture(_normalHandle);
                    if (_maskHandle != null) data.maskArray = renderGraph.ImportTexture(_maskHandle);
                    if (_blueNoiseHandle != null) data.blueNoise = renderGraph.ImportTexture(_blueNoiseHandle);

                    data.width = scaledWidth; data.height = scaledHeight;
                    data.cameraToWorld = cameraData.camera.cameraToWorldMatrix;
                    data.cameraInverseProjection = cameraData.camera.projectionMatrix.inverse;
                    data.zBufferParams = Shader.GetGlobalVector(_ZBufferParamsID);
                    data.sourceDepth = resourceData.cameraDepthTexture;
                    data.targetColor = tempResult;
                    data.targetDepth = tempResultDepth;
                    data.mainLightPosition = mainPos;
                    data.mainLightColor = mainCol;
                    data.raytraceParams = new Vector4(finalSpread, 0, 0, 0); 
                    data.mousePosition = VoxelRaytracerFeature.MousePosition;
                    data.maxIterations = iterations;
                    data.maxMarchSteps = marchSteps;

                    builder.UseTexture(data.targetColor, AccessFlags.Write);
                    builder.UseTexture(data.targetDepth, AccessFlags.Write);
                    builder.UseTexture(data.sourceDepth, AccessFlags.Read);
                    if (data.albedoArray.IsValid()) builder.UseTexture(data.albedoArray, AccessFlags.Read);
                    if (data.normalArray.IsValid()) builder.UseTexture(data.normalArray, AccessFlags.Read);
                    if (data.maskArray.IsValid()) builder.UseTexture(data.maskArray, AccessFlags.Read);
                    if (data.blueNoise.IsValid()) builder.UseTexture(data.blueNoise, AccessFlags.Read);

                    builder.SetRenderFunc((PassData pd, ComputeGraphContext ctx) =>
                    {
                        var cs = pd.computeShader;
                        var ker = pd.kernel;
                        var cmd = ctx.cmd;

                        cmd.SetComputeBufferParam(cs, ker, _GlobalNodeBufferParams, pd.nodeBuffer);
                        cmd.SetComputeBufferParam(cs, ker, _GlobalPayloadBufferParams, pd.payloadBuffer);
                        cmd.SetComputeBufferParam(cs, ker, _GlobalBrickDataBufferParams, pd.brickDataBuffer);
                        cmd.SetComputeBufferParam(cs, ker, _ChunkBufferParams, pd.chunkBuffer);
                        cmd.SetComputeIntParam(cs, _ChunkCountParams, pd.chunkCount); 
                        
                        if (pd.tlasGridBuffer != null) cmd.SetComputeBufferParam(cs, ker, _TLASGridBufferParams, pd.tlasGridBuffer);
                        if (pd.tlasChunkIndexBuffer != null) cmd.SetComputeBufferParam(cs, ker, _TLASChunkIndexBufferParams, pd.tlasChunkIndexBuffer);
                        cmd.SetComputeVectorParam(cs, _TLASBoundsMinParams, pd.tlasBoundsMin);
                        cmd.SetComputeVectorParam(cs, _TLASBoundsMaxParams, pd.tlasBoundsMax);
                        cmd.SetComputeIntParam(cs, _TLASResolutionParams, pd.tlasResolution);
                        cmd.SetComputeIntParam(cs, _FrameCountParams, pd.frameCount);
                        cmd.SetComputeVectorParam(cs, _MousePositionParams, pd.mousePosition);
                        cmd.SetComputeIntParam(cs, _MaxIterationsParams, pd.maxIterations);
                        cmd.SetComputeIntParam(cs, _MaxMarchStepsParams, pd.maxMarchSteps);
                        
                        if (pd.blueNoise.IsValid()) cmd.SetComputeTextureParam(cs, ker, _BlueNoiseTextureParams, pd.blueNoise);

                        cmd.SetComputeBufferParam(cs, ker, _RaycastBufferParams, pd.raycastBuffer);
                        if (pd.materialBuffer != null) cmd.SetComputeBufferParam(cs, ker, _VoxelMaterialBufferParams, pd.materialBuffer);
                        if (pd.albedoArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, _AlbedoTextureArrayParams, pd.albedoArray);
                        if (pd.normalArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, _NormalTextureArrayParams, pd.normalArray);
                        if (pd.maskArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, _MaskTextureArrayParams, pd.maskArray);

                        cmd.SetComputeMatrixParam(cs, _CameraToWorldParams, pd.cameraToWorld);
                        cmd.SetComputeMatrixParam(cs, _CameraInverseProjectionParams, pd.cameraInverseProjection);
                        cmd.SetComputeVectorParam(cs, _ZBufferParamsID, pd.zBufferParams);
                        cmd.SetComputeTextureParam(cs, ker, _CameraDepthTextureParams, pd.sourceDepth);
                        cmd.SetComputeTextureParam(cs, ker, _ResultParams, pd.targetColor);
                        cmd.SetComputeTextureParam(cs, ker, _ResultDepthParams, pd.targetDepth);
                        cmd.SetComputeVectorParam(cs, _MainLightPositionParams, pd.mainLightPosition);
                        cmd.SetComputeVectorParam(cs, _MainLightColorParams, pd.mainLightColor);
                        cmd.SetComputeVectorParam(cs, _RaytraceParams, pd.raytraceParams);

                        int groupsX = Mathf.CeilToInt(pd.width / 8.0f);
                        int groupsY = Mathf.CeilToInt(pd.height / 8.0f);
                        cmd.DispatchCompute(cs, ker, groupsX, groupsY, 1);
                    });
                }
                
                using (var builder = renderGraph.AddRasterRenderPass<BlitPassData>("Composite Voxels", out var blitData))
                {
                    blitData.source = tempResult;
                    blitData.depthSource = tempResultDepth;
                    blitData.material = _compositeMaterial;
                    builder.UseTexture(blitData.source, AccessFlags.Read);
                    builder.UseTexture(blitData.depthSource, AccessFlags.Read);
                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);

                    builder.SetRenderFunc((BlitPassData bData, RasterGraphContext context) =>
                    {
                        bData.material.SetTexture(_VoxelDepthTextureParams, bData.depthSource);
                        Blitter.BlitTexture(context.cmd, bData.source, new Vector4(1, 1, 0, 0), bData.material, 0);
                    });
                }
            }

            private void SetupLights(UniversalLightData lightData, out Vector4 mainPos, out Vector4 mainCol)
            {
                mainPos = new Vector4(0, 1, 0, 0); 
                mainCol = Color.white;
                var lights = lightData.visibleLights;
                int mainLightIndex = lightData.mainLightIndex;

                if (mainLightIndex != -1 && mainLightIndex < lights.Length)
                {
                    VisibleLight mainLight = lights[mainLightIndex];
                    if (mainLight.lightType == LightType.Directional)
                    {
                        Vector4 dir = -mainLight.localToWorldMatrix.GetColumn(2);
                        dir.w = 0; mainPos = dir; 
                        mainCol = mainLight.finalColor;
                    }
                }
            }
            
            private class BlitPassData { public TextureHandle source; public TextureHandle depthSource; public Material material; }
        }
    }
}