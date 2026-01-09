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
        
        // Shared Buffer for debug picking
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

            // Shader Property IDs
            private static readonly int _ResultParams = Shader.PropertyToID("_Result");
            private static readonly int _ResultDepthParams = Shader.PropertyToID("_ResultDepth");
            
            // Camera
            private static readonly int _CameraToWorldParams = Shader.PropertyToID("_CameraToWorld");
            private static readonly int _CameraInverseProjectionParams = Shader.PropertyToID("_CameraInverseProjection");
            private static readonly int _CameraDepthTextureParams = Shader.PropertyToID("_CameraDepthTexture");
            private static readonly int _VoxelDepthTextureParams = Shader.PropertyToID("_VoxelDepthTexture");
            private static readonly int _ZBufferParamsID = Shader.PropertyToID("_ZBufferParams");

            // Global Monolithic Buffers
            private static readonly int _GlobalNodeBufferParams = Shader.PropertyToID("_GlobalNodeBuffer");
            private static readonly int _GlobalPayloadBufferParams = Shader.PropertyToID("_GlobalPayloadBuffer");
            private static readonly int _GlobalBrickBufferParams = Shader.PropertyToID("_GlobalBrickBuffer");
            private static readonly int _GlobalBrickMaterialBufferParams = Shader.PropertyToID("_GlobalBrickMaterialBuffer");
            
            // TLAS (Chunk Map)
            private static readonly int _ChunkBufferParams = Shader.PropertyToID("_ChunkBuffer");
            private static readonly int _ChunkCountParams = Shader.PropertyToID("_ChunkCount");

            // Materials & Lights
            private static readonly int _VoxelMaterialBufferParams = Shader.PropertyToID("_VoxelMaterialBuffer");
            private static readonly int _AlbedoTextureArrayParams = Shader.PropertyToID("_AlbedoTextureArray");
            private static readonly int _NormalTextureArrayParams = Shader.PropertyToID("_NormalTextureArray");
            private static readonly int _MaskTextureArrayParams = Shader.PropertyToID("_MaskTextureArray");
            private static readonly int _MainLightPositionParams = Shader.PropertyToID("_MainLightPosition");
            private static readonly int _MainLightColorParams = Shader.PropertyToID("_MainLightColor");
            private static readonly int _RaycastBufferParams = Shader.PropertyToID("_RaycastBuffer");

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
                _albedoHandle?.Release(); _normalHandle?.Release(); _maskHandle?.Release();
                VoxelRaytracerFeature.RaycastHitBuffer?.Release();
            }

            private void CheckTextureHandle(ref RTHandle handle, Texture texture)
            {
                if (texture == null) return;
                if (handle == null || handle.rt != texture) { handle?.Release(); handle = RTHandles.Alloc(texture); }
            }

            // Data passed to the RenderGraph execution
            private class PassData
            {
                public ComputeShader computeShader;
                public int kernel;
                
                // Targets
                public TextureHandle targetColor;
                public TextureHandle targetDepth;
                public TextureHandle sourceDepth;
                
                // Camera Data
                public Matrix4x4 cameraToWorld;
                public Matrix4x4 cameraInverseProjection;
                public Vector4 zBufferParams;
                public int width; 
                public int height;

                // Lighting
                public Vector4 mainLightPosition;
                public Vector4 mainLightColor;

                // Voxel Data
                public GraphicsBuffer nodeBuffer;
                public GraphicsBuffer payloadBuffer;
                public GraphicsBuffer brickBuffer;
                public GraphicsBuffer brickMaterialBuffer;
                public GraphicsBuffer chunkBuffer;
                public int chunkCount;
                public GraphicsBuffer materialBuffer;
                public GraphicsBuffer raycastBuffer;

                // Material Textures
                public TextureHandle albedoArray;
                public TextureHandle normalArray;
                public TextureHandle maskArray;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                // Ensure the system is ready
                if (VoxelVolumePool.Instance == null) return;
                if (VoxelVolumePool.Instance.ActiveChunkCount == 0) return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var lightData = frameData.Get<UniversalLightData>();
                var cameraDesc = cameraData.cameraTargetDescriptor;

                // 1. Create Temporary Render Targets for Voxel Result
                TextureDesc colorDesc = new TextureDesc(cameraDesc.width, cameraDesc.height);
                colorDesc.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
                colorDesc.enableRandomWrite = true;
                colorDesc.name = "VoxelRaytraceResult";
                TextureHandle tempResult = renderGraph.CreateTexture(colorDesc);

                TextureDesc depthDesc = new TextureDesc(cameraDesc.width, cameraDesc.height);
                depthDesc.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat;
                depthDesc.enableRandomWrite = true;
                depthDesc.name = "VoxelRaytraceDepth";
                TextureHandle tempResultDepth = renderGraph.CreateTexture(depthDesc);

                // 2. Prepare Resources
                CheckTextureHandle(ref _albedoHandle, VoxelDefinitionManager.Instance.albedoTextureArray);
                CheckTextureHandle(ref _normalHandle, VoxelDefinitionManager.Instance.normalTextureArray);
                CheckTextureHandle(ref _maskHandle, VoxelDefinitionManager.Instance.maskTextureArray);

                SetupLights(lightData, out var mainPos, out var mainCol);

                // 3. Add Compute Pass
                using (var builder = renderGraph.AddComputePass("Voxel Raytracer Single-Dispatch", out PassData data))
                {
                    data.computeShader = _shader;
                    data.kernel = _shader.FindKernel("CSMain");

                    if (VoxelRaytracerFeature.RaycastHitBuffer == null || !VoxelRaytracerFeature.RaycastHitBuffer.IsValid())
                         VoxelRaytracerFeature.RaycastHitBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
                    data.raycastBuffer = VoxelRaytracerFeature.RaycastHitBuffer;

                    // Fetch Pool Data
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

                    // Camera Setup
                    data.width = cameraDesc.width;
                    data.height = cameraDesc.height;
                    data.cameraToWorld = cameraData.camera.cameraToWorldMatrix;
                    data.cameraInverseProjection = cameraData.camera.projectionMatrix.inverse;
                    data.zBufferParams = Shader.GetGlobalVector(_ZBufferParamsID);
                    data.sourceDepth = resourceData.cameraDepthTexture;

                    // Outputs
                    data.targetColor = tempResult;
                    data.targetDepth = tempResultDepth;

                    // Light Setup
                    data.mainLightPosition = mainPos;
                    data.mainLightColor = mainCol;

                    // Declare dependencies
                    builder.UseTexture(data.targetColor, AccessFlags.Write);
                    builder.UseTexture(data.targetDepth, AccessFlags.Write);
                    builder.UseTexture(data.sourceDepth, AccessFlags.Read);
                    if (data.albedoArray.IsValid()) builder.UseTexture(data.albedoArray, AccessFlags.Read);
                    if (data.normalArray.IsValid()) builder.UseTexture(data.normalArray, AccessFlags.Read);
                    if (data.maskArray.IsValid()) builder.UseTexture(data.maskArray, AccessFlags.Read);

                    builder.SetRenderFunc((PassData pd, ComputeGraphContext ctx) =>
                    {
                        var cs = pd.computeShader;
                        var ker = pd.kernel;
                        var cmd = ctx.cmd;

                        // Bind Global Monolithic Buffers
                        cmd.SetComputeBufferParam(cs, ker, _GlobalNodeBufferParams, pd.nodeBuffer);
                        cmd.SetComputeBufferParam(cs, ker, _GlobalPayloadBufferParams, pd.payloadBuffer);
                        cmd.SetComputeBufferParam(cs, ker, _GlobalBrickBufferParams, pd.brickBuffer);
                        cmd.SetComputeBufferParam(cs, ker, _GlobalBrickMaterialBufferParams, pd.brickMaterialBuffer);
                        
                        // Bind TLAS (Chunk Map)
                        cmd.SetComputeBufferParam(cs, ker, _ChunkBufferParams, pd.chunkBuffer);
                        cmd.SetComputeIntParam(cs, _ChunkCountParams, pd.chunkCount);

                        // Bind Other Globals
                        cmd.SetComputeBufferParam(cs, ker, _RaycastBufferParams, pd.raycastBuffer);
                        if (pd.materialBuffer != null) cmd.SetComputeBufferParam(cs, ker, _VoxelMaterialBufferParams, pd.materialBuffer);
                        if (pd.albedoArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, _AlbedoTextureArrayParams, pd.albedoArray);
                        if (pd.normalArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, _NormalTextureArrayParams, pd.normalArray);
                        if (pd.maskArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, _MaskTextureArrayParams, pd.maskArray);

                        // Bind Camera & Targets
                        cmd.SetComputeMatrixParam(cs, _CameraToWorldParams, pd.cameraToWorld);
                        cmd.SetComputeMatrixParam(cs, _CameraInverseProjectionParams, pd.cameraInverseProjection);
                        cmd.SetComputeVectorParam(cs, _ZBufferParamsID, pd.zBufferParams);
                        cmd.SetComputeTextureParam(cs, ker, _CameraDepthTextureParams, pd.sourceDepth);
                        cmd.SetComputeTextureParam(cs, ker, _ResultParams, pd.targetColor);
                        cmd.SetComputeTextureParam(cs, ker, _ResultDepthParams, pd.targetDepth);
                        
                        // Bind Lights
                        cmd.SetComputeVectorParam(cs, _MainLightPositionParams, pd.mainLightPosition);
                        cmd.SetComputeVectorParam(cs, _MainLightColorParams, pd.mainLightColor);

                        // DISPATCH: Full Screen (Single Dispatch)
                        int groupsX = Mathf.CeilToInt(pd.width / 8.0f);
                        int groupsY = Mathf.CeilToInt(pd.height / 8.0f);
                        cmd.DispatchCompute(cs, ker, groupsX, groupsY, 1);
                    });
                }

                // 4. Composite Pass (Blit over Scene)
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