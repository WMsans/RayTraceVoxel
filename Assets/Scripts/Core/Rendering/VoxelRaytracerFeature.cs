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
        [System.Serializable]
        public class Settings
        {
            public ComputeShader raytraceShader;
            public Shader compositeShader;
            public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingSkybox;
        }
        public Settings settings = new Settings();
        private VoxelRaytracerPass _pass;
        private Material _compositeMaterial;
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
            
            // IDs
            private static readonly int _ResultParams = Shader.PropertyToID("_Result");
            private static readonly int _ResultDepthParams = Shader.PropertyToID("_ResultDepth");
            private static readonly int _CameraToWorldParams = Shader.PropertyToID("_CameraToWorld");
            private static readonly int _CameraInverseProjectionParams = Shader.PropertyToID("_CameraInverseProjection");
            private static readonly int _CameraViewProjectionParams = Shader.PropertyToID("_CameraViewProjection");
            private static readonly int _CameraDepthTextureParams = Shader.PropertyToID("_CameraDepthTexture");
            private static readonly int _VoxelDepthTextureParams = Shader.PropertyToID("_VoxelDepthTexture");
            private static readonly int _ZBufferParamsID = Shader.PropertyToID("_ZBufferParams");
            
            // Global Buffer IDs
            private static readonly int _GlobalNodeBufferParams = Shader.PropertyToID("_GlobalNodeBuffer");
            private static readonly int _GlobalPayloadBufferParams = Shader.PropertyToID("_GlobalPayloadBuffer");
            private static readonly int _GlobalBrickBufferParams = Shader.PropertyToID("_GlobalBrickBuffer");
            private static readonly int _GlobalBrickMaterialBufferParams = Shader.PropertyToID("_GlobalBrickMaterialBuffer");
            private static readonly int _ChunkBufferParams = Shader.PropertyToID("_ChunkBuffer");
            private static readonly int _ChunkCountParams = Shader.PropertyToID("_ChunkCount");

            // Textures/Lights
            private static readonly int _VoxelMaterialBufferParams = Shader.PropertyToID("_VoxelMaterialBuffer");
            private static readonly int _AlbedoTextureArrayParams = Shader.PropertyToID("_AlbedoTextureArray");
            private static readonly int _NormalTextureArrayParams = Shader.PropertyToID("_NormalTextureArray");
            private static readonly int _MaskTextureArrayParams = Shader.PropertyToID("_MaskTextureArray");
            private static readonly int _MainLightPositionParams = Shader.PropertyToID("_MainLightPosition");
            private static readonly int _MainLightColorParams = Shader.PropertyToID("_MainLightColor");
            private static readonly int _MainLightShadowmapTextureParams = Shader.PropertyToID("_MainLightShadowmapTexture");
            private static readonly int _AdditionalLightsParams = Shader.PropertyToID("_AdditionalLights");
            private static readonly int _AdditionalLightCountParams = Shader.PropertyToID("_AdditionalLightCount");
            private static readonly int _RaycastBufferParams = Shader.PropertyToID("_RaycastBuffer");

            private GraphicsBuffer _lightBuffer;
            private VoxelLight[] _lightDataArray = new VoxelLight[64];
            private RTHandle _albedoHandle;
            private RTHandle _normalHandle;
            private RTHandle _maskHandle;

            public VoxelRaytracerPass(Settings settings)
            {
                _settings = settings;
                _shader = settings.raytraceShader;
                renderPassEvent = settings.injectionPoint;
            }

            public void Setup(Material mat) { _compositeMaterial = mat; }
            
            public void Dispose()
            {
                _lightBuffer?.Dispose();
                _albedoHandle?.Release(); _normalHandle?.Release(); _maskHandle?.Release();
                VoxelRaytracerFeature.RaycastHitBuffer?.Release();
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
                public TextureHandle sourceDepth;
                public TextureHandle targetColor; 
                public TextureHandle targetDepth;
                public GraphicsBuffer raycastBuffer;
                public Matrix4x4 cameraToWorld;
                public Matrix4x4 cameraInverseProjection;
                public Matrix4x4 cameraViewProjection;
                public Vector4 zBufferParams;
                public int width; public int height;
                
                // Lighting
                public Vector4 mainLightPosition;
                public Vector4 mainLightColor;
                public TextureHandle shadowMap;
                public GraphicsBuffer additionalLightsBuffer;
                public int additionalLightsCount;
                
                // Voxel Resources
                public GraphicsBuffer nodeBuffer;
                public GraphicsBuffer payloadBuffer;
                public GraphicsBuffer brickBuffer;
                public GraphicsBuffer brickMaterialBuffer;
                public GraphicsBuffer chunkBuffer;
                public int chunkCount;
                public GraphicsBuffer materialBuffer;
                public TextureHandle albedoArray;
                public TextureHandle normalArray;
                public TextureHandle maskArray;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (VoxelVolumePool.Instance == null) return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var lightData = frameData.Get<UniversalLightData>();
                var cameraDesc = cameraData.cameraTargetDescriptor;

                // Create Textures
                TextureDesc desc = new TextureDesc(cameraDesc.width, cameraDesc.height);
                desc.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
                desc.depthBufferBits = DepthBits.None;
                desc.enableRandomWrite = true;
                desc.name = "VoxelRaytraceResult";
                TextureHandle tempResult = renderGraph.CreateTexture(desc);
                
                TextureDesc depthDesc = new TextureDesc(cameraDesc.width, cameraDesc.height);
                depthDesc.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat;
                depthDesc.depthBufferBits = DepthBits.None;
                depthDesc.enableRandomWrite = true;
                depthDesc.name = "VoxelRaytraceDepth";
                TextureHandle tempResultDepth = renderGraph.CreateTexture(depthDesc);

                SetupLights(frameData.Get<UniversalRenderingData>(), lightData, out var mainPos, out var mainCol, out int addCount);

                CheckTextureHandle(ref _albedoHandle, VoxelDefinitionManager.Instance.albedoTextureArray);
                CheckTextureHandle(ref _normalHandle, VoxelDefinitionManager.Instance.normalTextureArray);
                CheckTextureHandle(ref _maskHandle, VoxelDefinitionManager.Instance.maskTextureArray);

                // --- 1. CLEAR PASSES ---
                
                // Pass 1a: Clear Color (Transparent)
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Clear Voxel Color", out var data))
                {
                    data.targetColor = tempResult;
                    builder.SetRenderAttachment(data.targetColor, 0, AccessFlags.Write);
                    builder.SetRenderFunc((PassData pd, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.ClearRenderTarget(RTClearFlags.Color, Color.clear, 1, 0);
                    });
                }

                // Pass 1b: Clear Depth (Far Plane)
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Clear Voxel Depth", out var data))
                {
                    data.targetDepth = tempResultDepth;
                    builder.SetRenderAttachment(data.targetDepth, 0, AccessFlags.Write);
                    builder.SetRenderFunc((PassData pd, RasterGraphContext ctx) =>
                    {
                        bool reversedZ = SystemInfo.usesReversedZBuffer;
                        ctx.cmd.ClearRenderTarget(RTClearFlags.Color, new Color(reversedZ ? 0f : 1f, 0,0,0), 1, 0);
                    });
                }

                // --- 2. COMPUTE PASS ---
                using (var builder = renderGraph.AddComputePass("Voxel Raytracer Global", out PassData data))
                {
                    data.computeShader = _shader;
                    data.kernel = _shader.FindKernel("CSMain");
                    
                    if (VoxelRaytracerFeature.RaycastHitBuffer == null || !VoxelRaytracerFeature.RaycastHitBuffer.IsValid())
                         VoxelRaytracerFeature.RaycastHitBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
                    data.raycastBuffer = VoxelRaytracerFeature.RaycastHitBuffer;

                    // Fetch Global Buffers
                    var pool = VoxelVolumePool.Instance;
                    data.nodeBuffer = pool.GlobalNodeBuffer;
                    data.payloadBuffer = pool.GlobalPayloadBuffer;
                    data.brickBuffer = pool.GlobalBrickBuffer;
                    data.brickMaterialBuffer = pool.GlobalBrickMaterialBuffer;
                    data.chunkBuffer = pool.ChunkBuffer;
                    data.chunkCount = pool.ActiveChunkCount;

                    data.materialBuffer = VoxelDefinitionManager.Instance.VoxelMaterialBuffer;
                    if (_albedoHandle != null) data.albedoArray = renderGraph.ImportTexture(_albedoHandle);
                    if (_normalHandle != null) data.normalArray = renderGraph.ImportTexture(_normalHandle);
                    if (_maskHandle != null) data.maskArray = renderGraph.ImportTexture(_maskHandle);

                    data.width = desc.width; data.height = desc.height;
                    data.cameraToWorld = cameraData.camera.cameraToWorldMatrix;
                    data.cameraInverseProjection = cameraData.camera.projectionMatrix.inverse;
                    var proj = GL.GetGPUProjectionMatrix(cameraData.camera.projectionMatrix, false);
                    var view = cameraData.camera.worldToCameraMatrix;
                    data.cameraViewProjection = proj * view;
                    data.zBufferParams = Shader.GetGlobalVector(_ZBufferParamsID);
                    
                    data.sourceDepth = resourceData.cameraDepthTexture;
                    data.targetColor = tempResult;
                    data.targetDepth = tempResultDepth;
                    data.mainLightPosition = mainPos;
                    data.mainLightColor = mainCol;
                    data.shadowMap = resourceData.mainShadowsTexture;
                    data.additionalLightsBuffer = _lightBuffer;
                    data.additionalLightsCount = addCount;

                    // Bind Resources
                    builder.UseTexture(data.targetColor, AccessFlags.Write);
                    builder.UseTexture(data.targetDepth, AccessFlags.Write);
                    builder.UseTexture(data.sourceDepth, AccessFlags.Read);
                    if (data.shadowMap.IsValid()) builder.UseTexture(data.shadowMap, AccessFlags.Read);
                    if (data.albedoArray.IsValid()) builder.UseTexture(data.albedoArray, AccessFlags.Read);
                    if (data.normalArray.IsValid()) builder.UseTexture(data.normalArray, AccessFlags.Read);
                    if (data.maskArray.IsValid()) builder.UseTexture(data.maskArray, AccessFlags.Read);

                    builder.SetRenderFunc((PassData pd, ComputeGraphContext ctx) =>
                    {
                        var cs = pd.computeShader;
                        var ker = pd.kernel;
                        var cmd = ctx.cmd;

                        // Bind Globals
                        cmd.SetComputeBufferParam(cs, ker, _GlobalNodeBufferParams, pd.nodeBuffer);
                        cmd.SetComputeBufferParam(cs, ker, _GlobalPayloadBufferParams, pd.payloadBuffer);
                        cmd.SetComputeBufferParam(cs, ker, _GlobalBrickBufferParams, pd.brickBuffer);
                        cmd.SetComputeBufferParam(cs, ker, _GlobalBrickMaterialBufferParams, pd.brickMaterialBuffer);
                        cmd.SetComputeBufferParam(cs, ker, _ChunkBufferParams, pd.chunkBuffer);
                        cmd.SetComputeIntParam(cs, _ChunkCountParams, pd.chunkCount);
                        
                        cmd.SetComputeBufferParam(cs, ker, _RaycastBufferParams, pd.raycastBuffer);
                        if (pd.materialBuffer != null) cmd.SetComputeBufferParam(cs, ker, _VoxelMaterialBufferParams, pd.materialBuffer);

                        if (pd.albedoArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, _AlbedoTextureArrayParams, pd.albedoArray);
                        if (pd.normalArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, _NormalTextureArrayParams, pd.normalArray);
                        if (pd.maskArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, _MaskTextureArrayParams, pd.maskArray);

                        cmd.SetComputeMatrixParam(cs, _CameraToWorldParams, pd.cameraToWorld);
                        cmd.SetComputeMatrixParam(cs, _CameraInverseProjectionParams, pd.cameraInverseProjection);
                        cmd.SetComputeMatrixParam(cs, _CameraViewProjectionParams, pd.cameraViewProjection);
                        cmd.SetComputeVectorParam(cs, _ZBufferParamsID, pd.zBufferParams);
                        
                        cmd.SetComputeTextureParam(cs, ker, _CameraDepthTextureParams, pd.sourceDepth);
                        cmd.SetComputeTextureParam(cs, ker, _ResultParams, pd.targetColor);
                        cmd.SetComputeTextureParam(cs, ker, _ResultDepthParams, pd.targetDepth);
                        
                        cmd.SetComputeVectorParam(cs, _MainLightPositionParams, pd.mainLightPosition);
                        cmd.SetComputeVectorParam(cs, _MainLightColorParams, pd.mainLightColor);
                        if (pd.shadowMap.IsValid()) cmd.SetComputeTextureParam(cs, ker, _MainLightShadowmapTextureParams, pd.shadowMap);
                        cmd.SetComputeBufferParam(cs, ker, _AdditionalLightsParams, pd.additionalLightsBuffer);
                        cmd.SetComputeIntParam(cs, _AdditionalLightCountParams, pd.additionalLightsCount);

                        // Dispatch Full Screen
                        int groupsX = Mathf.CeilToInt(pd.width / 8.0f);
                        int groupsY = Mathf.CeilToInt(pd.height / 8.0f);
                        cmd.DispatchCompute(cs, ker, groupsX, groupsY, 1);
                    });
                }

                // --- 3. COMPOSITE PASS ---
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
            
            private void SetupLights(UniversalRenderingData renderingData, UniversalLightData lightData, out Vector4 mainPos, out Vector4 mainCol, out int addCount)
            {
                 mainPos = new Vector4(0, 1, 0, 0); mainCol = Color.white; addCount = 0;
                 // (Simplified light setup for brevity - same as previous)
                 var lights = lightData.visibleLights;
                 int mainLightIndex = lightData.mainLightIndex;

                 if (mainLightIndex != -1 && mainLightIndex < lights.Length)
                 {
                     VisibleLight mainLight = lights[mainLightIndex];
                     if (mainLight.lightType == LightType.Directional)
                     {
                         Vector4 dir = -mainLight.localToWorldMatrix.GetColumn(2);
                         dir.w = 0; mainPos = dir; mainCol = mainLight.finalColor;
                     }
                 }
                 
                 // Ensure buffer exists
                 if (_lightBuffer == null || _lightBuffer.count < _lightDataArray.Length)
                 {
                    _lightBuffer?.Dispose();
                    _lightBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _lightDataArray.Length, System.Runtime.InteropServices.Marshal.SizeOf<VoxelLight>());
                 }
                 _lightBuffer.SetData(_lightDataArray, 0, 0, 1); 
            }
            private class BlitPassData { public TextureHandle source; public TextureHandle depthSource; public Material material; }
        }
    }
}