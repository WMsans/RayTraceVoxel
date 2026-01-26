using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using VoxelEngine.Core;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Streaming;
using System.Collections.Generic;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelRaytracerFeature : ScriptableRendererFeature
    {
        public enum QualityLevel { High, Low, Custom }
        public enum UpscalingMode { Bilinear, SpatialFSR }

        [System.Serializable]
        public class Settings
        {
            public ComputeShader raytraceShader;
            public Shader taaShader; 
            public Shader compositeShader;
            public Shader fxaaShader;
            public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingSkybox;

            [Header("Quality")]
            public QualityLevel qualityLevel = QualityLevel.High;
            [Range(0.1f, 1.0f)]
            public float renderScale = 1.0f;
            public int iterations = 128;
            public int marchSteps = 64;
            
            [Header("Upscaling & Anti-Aliasing")]
            public UpscalingMode upscalingMode = UpscalingMode.SpatialFSR;
            [Range(0.0f, 1.0f)] public float sharpness = 0.5f;
            public bool enableFXAA = true;
            public bool enableTAA = true; 
            [Range(0.0f, 1.0f)] public float taaBlend = 0.93f; 
            
            [Header("LOD Settings")]
            [Range(1.0f, 200.0f)] 
            public float lodBias = 1.0f;

            [Header("Culling")]
            public bool useCameraFarPlane = false; 

            [Header("Dithering")]
            public Texture2D blueNoiseTexture;
        }

        public Settings settings = new Settings();
        private VoxelRaytracerPass _pass;
        private Material _compositeMaterial;
        private Material _fxaaMaterial;
        private Material _taaMaterial;

        public static Vector2 MousePosition;
        public static GraphicsBuffer RaycastHitBuffer;

        public override void Create()
        {
            _pass = new VoxelRaytracerPass(settings);
            
            if (settings.compositeShader != null)
                _compositeMaterial = new Material(settings.compositeShader);
            else
                _compositeMaterial = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/Universal Render Pipeline/Blit"));

            if (settings.fxaaShader != null)
                _fxaaMaterial = new Material(settings.fxaaShader);

            if (settings.taaShader != null)
                _taaMaterial = new Material(settings.taaShader);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (settings.raytraceShader == null) return;
            if (VoxelVolumePool.Instance == null) return;

            _pass.UpdateSettings(settings);
            _pass.Setup(_compositeMaterial, _fxaaMaterial, _taaMaterial);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_compositeMaterial);
            CoreUtils.Destroy(_fxaaMaterial);
            CoreUtils.Destroy(_taaMaterial);
            _pass?.Dispose();
        }

        class VoxelRaytracerPass : ScriptableRenderPass
        {
            private Settings _settings;
            private ComputeShader _shader;
            private Material _compositeMaterial;
            private Material _fxaaMaterial;
            private Material _taaMaterial;

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
            private static readonly int _PageTableBufferParams = Shader.PropertyToID("_PageTableBuffer");
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
            private static readonly int _CameraViewProjectionParams = Shader.PropertyToID("_CameraViewProjection");
            private static readonly int _PrevViewProjMatrixParams = Shader.PropertyToID("_PrevViewProjMatrix");
            private static readonly int _MotionVectorTextureParams = Shader.PropertyToID("_MotionVectorTexture");
            private static readonly int _SourceTexParams = Shader.PropertyToID("_SourceTex");
            private static readonly int _SharpnessParams = Shader.PropertyToID("_Sharpness");
            private static readonly int _HistoryTexParams = Shader.PropertyToID("_HistoryTex");
            private static readonly int _BlendParams = Shader.PropertyToID("_Blend");

            private RTHandle _albedoHandle;
            private RTHandle _normalHandle;
            private RTHandle _maskHandle;
            private RTHandle _blueNoiseHandle;

            class CameraHistory
            {
                public RTHandle[] historyTextures = new RTHandle[2];
                public int currentIndex = 0;
            }
            private Dictionary<Camera, CameraHistory> _cameraHistory = new Dictionary<Camera, CameraHistory>();
            private Dictionary<Camera, Matrix4x4> _prevMatrices = new Dictionary<Camera, Matrix4x4>();

            public VoxelRaytracerPass(Settings settings)
            {
                _settings = settings;
                _shader = settings.raytraceShader;
                renderPassEvent = settings.injectionPoint;
            }

            public void UpdateSettings(Settings newSettings) { _settings = newSettings; }
            public void Setup(Material composite, Material fxaa, Material taa) 
            { 
                _compositeMaterial = composite; 
                _fxaaMaterial = fxaa;
                _taaMaterial = taa;
            }

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
                foreach(var kvp in _cameraHistory)
                {
                    kvp.Value.historyTextures[0]?.Release();
                    kvp.Value.historyTextures[1]?.Release();
                }
                _cameraHistory.Clear();
            }            
            
            private void CheckTextureHandle(ref RTHandle handle, Texture texture)
            {
                if (texture == null) return;
                if (handle == null || handle.rt != texture) { handle?.Release(); handle = RTHandles.Alloc(texture); }
            }

            private float Halton(int index, int radix)
            {
                float result = 0f;
                float fraction = 1f / radix;
                while (index > 0)
                {
                    result += (index % radix) * fraction;
                    index /= radix;
                    fraction /= radix;
                }
                return result;
            }

            // --- Pass Data Classes ---
            private class PassData {
                public ComputeShader computeShader; public int kernel; public TextureHandle targetColor; public TextureHandle targetDepth; public TextureHandle targetMotionVector; public TextureHandle sourceDepth; public TextureHandle sourceColor; public Matrix4x4 cameraToWorld; public Matrix4x4 cameraInverseProjection; public Matrix4x4 viewProj; public Matrix4x4 prevViewProj; public Vector4 zBufferParams; public int width; public int height; public Vector4 mainLightPosition; public Vector4 mainLightColor; public Vector4 raytraceParams; public GraphicsBuffer nodeBuffer; public GraphicsBuffer payloadBuffer; public GraphicsBuffer brickDataBuffer; public GraphicsBuffer pageTableBuffer; public GraphicsBuffer tlasGridBuffer; public GraphicsBuffer tlasChunkIndexBuffer; public Vector3 tlasBoundsMin; public Vector3 tlasBoundsMax; public int tlasResolution; public GraphicsBuffer chunkBuffer; public int chunkCount; public GraphicsBuffer materialBuffer; public GraphicsBuffer raycastBuffer; public TextureHandle albedoArray; public TextureHandle normalArray; public TextureHandle maskArray; public int frameCount; public TextureHandle blueNoise; public Vector2 mousePosition; public int maxIterations; public int maxMarchSteps;
            }
            private class CompositePassData { public TextureHandle source; public TextureHandle depthSource; public Material material; public bool useFSR; public float sharpness; }
            private class FXAAPassData { public TextureHandle source; public Material material; }
            private class TAAPassData { public TextureHandle source; public TextureHandle history; public TextureHandle motion; public TextureHandle destination; public Material material; public float blend; }
            private class GrassPassData { public TextureHandle colorTarget; public TextureHandle depthTarget; }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (VoxelVolumePool.Instance == null) return;

                var cameraData = frameData.Get<UniversalCameraData>();
                
                // Culling update
                Plane[] allPlanes = GeometryUtility.CalculateFrustumPlanes(cameraData.camera);
                Plane[] cullingPlanes = _settings.useCameraFarPlane ? allPlanes : new Plane[] { allPlanes[0], allPlanes[1], allPlanes[2], allPlanes[3], allPlanes[4] };
                VoxelVolumePool.Instance.UpdateVisibility(cullingPlanes);

                if (VoxelVolumePool.Instance.VisibleChunkCount == 0) return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var lightData = frameData.Get<UniversalLightData>();
                var cameraDesc = cameraData.cameraTargetDescriptor;

                // Resolution Logic
                float currentScale = 1.0f;
                int iterations = 128;
                int marchSteps = 64;
                switch (_settings.qualityLevel) {
                    case QualityLevel.High: currentScale = 1.0f; break;
                    case QualityLevel.Low: currentScale = 0.5f; iterations = 64; marchSteps = 32; break;
                    case QualityLevel.Custom: currentScale = _settings.renderScale; iterations = _settings.iterations; marchSteps = _settings.marchSteps; break;
                }
                int scaledWidth = Mathf.Max(1, Mathf.RoundToInt(cameraDesc.width * currentScale));
                int scaledHeight = Mathf.Max(1, Mathf.RoundToInt(cameraDesc.height * currentScale));

                // Create Textures
                TextureDesc colorDesc = new TextureDesc(scaledWidth, scaledHeight) { colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite = true, name = "VoxelRaytraceResult_LowRes" };
                TextureHandle lowResResult = renderGraph.CreateTexture(colorDesc);

                TextureDesc depthDesc = new TextureDesc(scaledWidth, scaledHeight) { colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat, enableRandomWrite = true, name = "VoxelRaytraceDepth_LowRes" };
                TextureHandle lowResDepth = renderGraph.CreateTexture(depthDesc);
                
                TextureDesc mvDesc = new TextureDesc(scaledWidth, scaledHeight) { colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16_SFloat, enableRandomWrite = true, name = "VoxelMotionVectors" };
                TextureHandle motionVectorTex = renderGraph.CreateTexture(mvDesc);

                // Jitter & Matrices Setup
                int frameIndex = Time.frameCount % 16;
                float jitterX = (Halton(frameIndex + 1, 2) - 0.5f);
                float jitterY = (Halton(frameIndex + 1, 3) - 0.5f);
                bool useTAA = _settings.enableTAA && _taaMaterial != null;
                if (!useTAA) { jitterX = 0; jitterY = 0; }

                var cam = cameraData.camera;
                Matrix4x4 view = cam.worldToCameraMatrix;
                Matrix4x4 proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
                Matrix4x4 viewProj = proj * view;
                if (!_prevMatrices.TryGetValue(cam, out Matrix4x4 prevViewProj)) prevViewProj = viewProj;
                _prevMatrices[cam] = viewProj;

                // TAA History Setup
                TextureHandle historyRead = TextureHandle.nullHandle;
                TextureHandle historyWrite = TextureHandle.nullHandle;
                if (useTAA) {
                    if (!_cameraHistory.TryGetValue(cam, out CameraHistory hist)) { hist = new CameraHistory(); _cameraHistory[cam] = hist; }
                    for(int i=0; i<2; i++) {
                        if (hist.historyTextures[i] == null || hist.historyTextures[i].rt.width != scaledWidth || hist.historyTextures[i].rt.height != scaledHeight) {
                            hist.historyTextures[i]?.Release();
                            hist.historyTextures[i] = RTHandles.Alloc(scaledWidth, scaledHeight, colorFormat: UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, name: $"VoxelHistory_{i}");
                        }
                    }
                    historyRead = renderGraph.ImportTexture(hist.historyTextures[hist.currentIndex]);
                    historyWrite = renderGraph.ImportTexture(hist.historyTextures[(hist.currentIndex + 1) % 2]);
                    hist.currentIndex = (hist.currentIndex + 1) % 2;
                }

                // Final Output Setup
                TextureHandle compositeOutput;
                bool useFXAA = _settings.enableFXAA && _fxaaMaterial != null;
                if (useFXAA) {
                    TextureDesc fullScreenDesc = new TextureDesc(cameraDesc.width, cameraDesc.height) { colorFormat = cameraDesc.graphicsFormat, name = "VoxelComposite_PreFXAA" };
                    compositeOutput = renderGraph.CreateTexture(fullScreenDesc);
                } else {
                    compositeOutput = resourceData.activeColorTexture;
                }

                // --- 1. Compute Pass (Raytrace) ---
                CheckTextureHandle(ref _albedoHandle, VoxelDefinitionManager.Instance.albedoTextureArray);
                CheckTextureHandle(ref _normalHandle, VoxelDefinitionManager.Instance.normalTextureArray);
                CheckTextureHandle(ref _maskHandle, VoxelDefinitionManager.Instance.maskTextureArray);
                CheckTextureHandle(ref _blueNoiseHandle, _settings.blueNoiseTexture);
                SetupLights(lightData, out var mainPos, out var mainCol);

                float fov = cameraData.camera.fieldOfView;
                float rawPixelSpread = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 2.0f / cameraDesc.height;
                float finalSpread = rawPixelSpread * _settings.lodBias;

                using (var builder = renderGraph.AddComputePass("Voxel Raytracer", out PassData data))
                {
                    data.computeShader = _shader; data.kernel = _shader.FindKernel("CSMain");
                    if (VoxelRaytracerFeature.RaycastHitBuffer == null || !VoxelRaytracerFeature.RaycastHitBuffer.IsValid()) VoxelRaytracerFeature.RaycastHitBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
                    data.raycastBuffer = VoxelRaytracerFeature.RaycastHitBuffer;
                    var pool = VoxelVolumePool.Instance; data.nodeBuffer = pool.GlobalNodeBuffer; data.payloadBuffer = pool.GlobalPayloadBuffer; data.brickDataBuffer = pool.GlobalBrickDataBuffer; data.pageTableBuffer = pool.GlobalPageTableBuffer; data.chunkBuffer = pool.ChunkBuffer; data.chunkCount = pool.VisibleChunkCount; data.tlasGridBuffer = pool.TLASGridBuffer; data.tlasChunkIndexBuffer = pool.TLASChunkIndexBuffer; data.tlasBoundsMin = pool.TLASBoundsMin; data.tlasBoundsMax = pool.TLASBoundsMax; data.tlasResolution = pool.TLASResolution; data.frameCount = Time.frameCount; data.materialBuffer = VoxelDefinitionManager.Instance.VoxelMaterialBuffer;
                    if (_albedoHandle != null) data.albedoArray = renderGraph.ImportTexture(_albedoHandle);
                    if (_normalHandle != null) data.normalArray = renderGraph.ImportTexture(_normalHandle);
                    if (_maskHandle != null) data.maskArray = renderGraph.ImportTexture(_maskHandle);
                    if (_blueNoiseHandle != null) data.blueNoise = renderGraph.ImportTexture(_blueNoiseHandle);
                    data.width = scaledWidth; data.height = scaledHeight; data.cameraToWorld = cameraData.camera.cameraToWorldMatrix; data.cameraInverseProjection = cameraData.camera.projectionMatrix.inverse; data.viewProj = viewProj; data.prevViewProj = prevViewProj; data.zBufferParams = Shader.GetGlobalVector(_ZBufferParamsID); data.sourceDepth = resourceData.cameraDepthTexture; data.sourceColor = resourceData.activeColorTexture; data.targetColor = lowResResult; data.targetDepth = lowResDepth; data.targetMotionVector = motionVectorTex; data.mainLightPosition = mainPos; data.mainLightColor = mainCol; data.raytraceParams = new Vector4(finalSpread, jitterX, jitterY, 0); data.mousePosition = VoxelRaytracerFeature.MousePosition * currentScale; data.maxIterations = iterations; data.maxMarchSteps = marchSteps;

                    builder.UseTexture(data.targetColor, AccessFlags.Write);
                    builder.UseTexture(data.targetDepth, AccessFlags.Write);
                    builder.UseTexture(data.targetMotionVector, AccessFlags.Write);
                    builder.UseTexture(data.sourceDepth, AccessFlags.Read);
                    builder.UseTexture(data.sourceColor, AccessFlags.Read);
                    if (data.albedoArray.IsValid()) builder.UseTexture(data.albedoArray, AccessFlags.Read);
                    if (data.normalArray.IsValid()) builder.UseTexture(data.normalArray, AccessFlags.Read);
                    if (data.maskArray.IsValid()) builder.UseTexture(data.maskArray, AccessFlags.Read);
                    if (data.blueNoise.IsValid()) builder.UseTexture(data.blueNoise, AccessFlags.Read);

                    builder.SetRenderFunc((PassData pd, ComputeGraphContext ctx) =>
                    {
                        var cs = pd.computeShader; var ker = pd.kernel; var cmd = ctx.cmd;
                        cmd.SetComputeBufferParam(cs, ker, _GlobalNodeBufferParams, pd.nodeBuffer);
                        cmd.SetComputeBufferParam(cs, ker, _GlobalPayloadBufferParams, pd.payloadBuffer);
                        cmd.SetComputeBufferParam(cs, ker, _GlobalBrickDataBufferParams, pd.brickDataBuffer);
                        cmd.SetComputeBufferParam(cs, ker, _PageTableBufferParams, pd.pageTableBuffer);
                        cmd.SetComputeBufferParam(cs, ker, _ChunkBufferParams, pd.chunkBuffer);
                        cmd.SetComputeIntParam(cs, _ChunkCountParams, pd.chunkCount); 
                        if (pd.tlasGridBuffer != null) cmd.SetComputeBufferParam(cs, ker, _TLASGridBufferParams, pd.tlasGridBuffer);
                        if (pd.tlasChunkIndexBuffer != null) cmd.SetComputeBufferParam(cs, ker, _TLASChunkIndexBufferParams, pd.tlasChunkIndexBuffer);
                        cmd.SetComputeVectorParam(cs, _TLASBoundsMinParams, pd.tlasBoundsMin); cmd.SetComputeVectorParam(cs, _TLASBoundsMaxParams, pd.tlasBoundsMax); cmd.SetComputeIntParam(cs, _TLASResolutionParams, pd.tlasResolution); cmd.SetComputeIntParam(cs, _FrameCountParams, pd.frameCount); cmd.SetComputeVectorParam(cs, _MousePositionParams, pd.mousePosition); cmd.SetComputeIntParam(cs, _MaxIterationsParams, pd.maxIterations); cmd.SetComputeIntParam(cs, _MaxMarchStepsParams, pd.maxMarchSteps);
                        if (pd.blueNoise.IsValid()) cmd.SetComputeTextureParam(cs, ker, _BlueNoiseTextureParams, pd.blueNoise);
                        if (pd.materialBuffer != null) cmd.SetComputeBufferParam(cs, ker, _VoxelMaterialBufferParams, pd.materialBuffer);
                        if (pd.albedoArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, _AlbedoTextureArrayParams, pd.albedoArray);
                        if (pd.normalArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, _NormalTextureArrayParams, pd.normalArray);
                        if (pd.maskArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, _MaskTextureArrayParams, pd.maskArray);
                        cmd.SetComputeMatrixParam(cs, _CameraToWorldParams, pd.cameraToWorld); cmd.SetComputeMatrixParam(cs, _CameraInverseProjectionParams, pd.cameraInverseProjection); cmd.SetComputeMatrixParam(cs, _CameraViewProjectionParams, pd.viewProj); cmd.SetComputeMatrixParam(cs, _PrevViewProjMatrixParams, pd.prevViewProj);
                        cmd.SetComputeVectorParam(cs, _ZBufferParamsID, pd.zBufferParams); cmd.SetComputeTextureParam(cs, ker, _CameraDepthTextureParams, pd.sourceDepth); cmd.SetComputeTextureParam(cs, ker, _SourceTexParams, pd.sourceColor); cmd.SetComputeTextureParam(cs, ker, _ResultParams, pd.targetColor); cmd.SetComputeTextureParam(cs, ker, _ResultDepthParams, pd.targetDepth); cmd.SetComputeTextureParam(cs, ker, _MotionVectorTextureParams, pd.targetMotionVector);
                        cmd.SetComputeVectorParam(cs, _MainLightPositionParams, pd.mainLightPosition); cmd.SetComputeVectorParam(cs, _MainLightColorParams, pd.mainLightColor); cmd.SetComputeVectorParam(cs, _RaytraceParams, pd.raytraceParams); cmd.SetComputeBufferParam(cs, ker, _RaycastBufferParams, pd.raycastBuffer);
                        int groupsX = Mathf.CeilToInt(pd.width / 8.0f); int groupsY = Mathf.CeilToInt(pd.height / 8.0f);
                        cmd.DispatchCompute(cs, ker, groupsX, groupsY, 1);
                    });
                }
                
                TextureHandle compositeSource = lowResResult; 

                // --- 2. TAA Pass ---
                if (useTAA)
                {
                    using (var builder = renderGraph.AddRasterRenderPass<TAAPassData>("Voxel TAA", out var taaData))
                    {
                        taaData.source = lowResResult; taaData.history = historyRead; taaData.motion = motionVectorTex; taaData.destination = historyWrite; taaData.material = _taaMaterial; taaData.blend = _settings.taaBlend;
                        builder.UseTexture(taaData.source, AccessFlags.Read); builder.UseTexture(taaData.history, AccessFlags.Read); builder.UseTexture(taaData.motion, AccessFlags.Read); builder.SetRenderAttachment(taaData.destination, 0, AccessFlags.Write);
                        builder.SetRenderFunc((TAAPassData tData, RasterGraphContext context) => { tData.material.SetTexture(_HistoryTexParams, tData.history); tData.material.SetTexture(_MotionVectorTextureParams, tData.motion); tData.material.SetFloat(_BlendParams, tData.blend); Blitter.BlitTexture(context.cmd, tData.source, new Vector4(1, 1, 0, 0), tData.material, 0); });
                    }
                    compositeSource = historyWrite;
                }

                // --- 3. Composite (Upscale) Pass & Depth Write ---
                using (var builder = renderGraph.AddRasterRenderPass<CompositePassData>("Composite & Upscale", out var compData))
                {
                    compData.source = compositeSource; 
                    compData.depthSource = lowResDepth;
                    compData.material = _compositeMaterial;
                    compData.useFSR = (_settings.upscalingMode == UpscalingMode.SpatialFSR);
                    compData.sharpness = _settings.sharpness;

                    builder.UseTexture(compData.source, AccessFlags.Read);
                    builder.UseTexture(compData.depthSource, AccessFlags.Read);
                    
                    // [IMPORTANT] FORCE DEPTH WRITE for Grass Occlusion
                    // Even if using FXAA intermediate, we MUST write the Voxel Depth to the MAIN Camera Depth Buffer
                    // so subsequent passes (Grass) can test against it.
                    builder.SetRenderAttachment(compositeOutput, 0, AccessFlags.Write);
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);

                    builder.SetRenderFunc((CompositePassData cData, RasterGraphContext context) =>
                    {
                        if (useFXAA) { context.cmd.ClearRenderTarget(false, true, Color.clear); } // Clear intermediate color only
                        
                        cData.material.SetTexture(_VoxelDepthTextureParams, cData.depthSource);
                        cData.material.SetFloat(_SharpnessParams, cData.sharpness);
                        if (cData.useFSR) cData.material.EnableKeyword("_UPSCALING_FSR"); else cData.material.DisableKeyword("_UPSCALING_FSR");
                        
                        // This Blit writes Color to compositeOutput AND Depth to activeDepthTexture (via Shader SV_Depth)
                        Blitter.BlitTexture(context.cmd, cData.source, new Vector4(1, 1, 0, 0), cData.material, 0);
                    });
                }

                // --- 4. Grass Pass (Rasterization) ---
                // Grass is drawn AFTER Composite has populated the Depth Buffer.
                // Grass is drawn BEFORE FXAA so it gets anti-aliased.
                if (VoxelGrassRenderer.ActiveRenderers.Count > 0 || VoxelLeafRenderer.ActiveLeafRenderers.Count > 0)
                {
                    using (var builder = renderGraph.AddRasterRenderPass<GrassPassData>("Voxel Vegetation", out var grassData))
                    {
                        grassData.colorTarget = compositeOutput;
                        grassData.depthTarget = resourceData.activeDepthTexture;

                        // Bind targets
                        builder.SetRenderAttachment(grassData.colorTarget, 0, AccessFlags.Write);
                        builder.SetRenderAttachmentDepth(grassData.depthTarget, AccessFlags.ReadWrite);

                        builder.SetRenderFunc((GrassPassData gData, RasterGraphContext context) =>
                        {
                            // Draw Grass
                            foreach (var renderer in VoxelGrassRenderer.ActiveRenderers)
                            {
                                renderer.Draw(context.cmd);
                            }
                            // Draw Leaves
                            foreach (var renderer in VoxelLeafRenderer.ActiveLeafRenderers)
                            {
                                renderer.Draw(context.cmd);
                            }
                        });
                    }
                }

                // --- 5. FXAA Pass ---
                if (useFXAA)
                {
                    using (var builder = renderGraph.AddRasterRenderPass<FXAAPassData>("FXAA Pass", out var fxaaData))
                    {
                        fxaaData.source = compositeOutput;
                        fxaaData.material = _fxaaMaterial;
                        builder.UseTexture(fxaaData.source, AccessFlags.Read);
                        builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                        // No depth write needed for FXAA
                        builder.SetRenderFunc((FXAAPassData fData, RasterGraphContext context) => { Blitter.BlitTexture(context.cmd, fData.source, new Vector4(1, 1, 0, 0), fData.material, 0); });
                    }
                }
            }

            private void SetupLights(UniversalLightData lightData, out Vector4 mainPos, out Vector4 mainCol)
            {
                mainPos = new Vector4(0, 1, 0, 0); 
                mainCol = Color.white;
                int mainLightIndex = lightData.mainLightIndex;
                if (mainLightIndex != -1 && mainLightIndex < lightData.visibleLights.Length) {
                    VisibleLight mainLight = lightData.visibleLights[mainLightIndex];
                    if (mainLight.lightType == LightType.Directional) {
                        Vector4 dir = -mainLight.localToWorldMatrix.GetColumn(2); dir.w = 0; mainPos = dir; mainCol = mainLight.finalColor;
                    }
                }
            }
        }
    }
}