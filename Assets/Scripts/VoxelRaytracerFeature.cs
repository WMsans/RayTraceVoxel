using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class VoxelRaytracerFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public ComputeShader raytraceShader;
        public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingOpaques;
    }

    public Settings settings = new Settings();
    private VoxelRaytracerPass _pass;

    public override void Create()
    {
        _pass = new VoxelRaytracerPass(settings);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.raytraceShader == null) return;
        // Check SVO readiness
        if (SVOManager.Instance == null || !SVOManager.Instance.IsReady) return;

        renderer.EnqueuePass(_pass);
    }

    // --- The Render Pass ---
    class VoxelRaytracerPass : ScriptableRenderPass
    {
        private Settings _settings;
        private ComputeShader _shader;
        
        // Shader Property IDs
        private static readonly int _ResultParams = Shader.PropertyToID("_Result");
        private static readonly int _CameraToWorldParams = Shader.PropertyToID("_CameraToWorld");
        private static readonly int _CameraInverseProjectionParams = Shader.PropertyToID("_CameraInverseProjection");
        private static readonly int _CameraDepthTextureParams = Shader.PropertyToID("_CameraDepthTexture");
        
        // SVO IDs
        private static readonly int _NodeBufferParams = Shader.PropertyToID("_NodeBuffer");
        private static readonly int _PayloadBufferParams = Shader.PropertyToID("_PayloadBuffer");

        private class PassData
        {
            public ComputeShader computeShader;
            public int kernel;
            public TextureHandle sourceDepth;
            public TextureHandle targetColor; // Temporary writable texture
            public GraphicsBuffer nodeBuffer;
            public GraphicsBuffer payloadBuffer;
            public Matrix4x4 cameraToWorld;
            public Matrix4x4 cameraInverseProjection;
            public int width;
            public int height;
        }

        private class BlitPassData
        {
            public TextureHandle source;
        }

        public VoxelRaytracerPass(Settings settings)
        {
            _settings = settings;
            _shader = settings.raytraceShader;
            renderPassEvent = settings.injectionPoint;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // 1. Get Resources
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            // 2. Define Texture Descriptors manually (Fixes CS0029 & CS0266)
            var cameraDesc = cameraData.cameraTargetDescriptor;
            TextureDesc desc = new TextureDesc(cameraDesc.width, cameraDesc.height);
            desc.colorFormat = cameraDesc.graphicsFormat;
            desc.depthBufferBits = DepthBits.None; // We don't need depth in the result texture
            desc.enableRandomWrite = true;         // Required for Compute Shader UAV
            desc.msaaSamples = MSAASamples.None;   // Compute shaders don't support MSAA targets directly
            desc.name = "VoxelRaytraceResult";

            // 3. Setup the Compute Pass
            using (var builder = renderGraph.AddComputePass("Voxel Raytracer Pass", out PassData data))
            {
                // Setup Pass Data
                data.computeShader = _shader;
                data.kernel = _shader.FindKernel("CSMain");
                data.nodeBuffer = SVOManager.Instance.NodeBuffer;
                data.payloadBuffer = SVOManager.Instance.PayloadBuffer;
                data.width = desc.width;
                data.height = desc.height;
                
                // Camera Matrices
                data.cameraToWorld = cameraData.camera.cameraToWorldMatrix;
                data.cameraInverseProjection = cameraData.camera.projectionMatrix.inverse;

                // Dependencies
                data.sourceDepth = resourceData.cameraDepthTexture;
                builder.UseTexture(data.sourceDepth, AccessFlags.Read); 

                // Output
                TextureHandle tempResult = renderGraph.CreateTexture(desc);
                data.targetColor = tempResult;
                builder.UseTexture(data.targetColor, AccessFlags.Write);

                // Execution Logic
                builder.SetRenderFunc((PassData passData, ComputeGraphContext ctx) =>
                {
                    var cs = passData.computeShader;
                    var kernel = passData.kernel;
                    var cmd = ctx.cmd;

                    // Bind SVO Buffers
                    cmd.SetComputeBufferParam(cs, kernel, _NodeBufferParams, passData.nodeBuffer);
                    cmd.SetComputeBufferParam(cs, kernel, _PayloadBufferParams, passData.payloadBuffer);

                    // Bind Camera Data
                    cmd.SetComputeMatrixParam(cs, _CameraToWorldParams, passData.cameraToWorld);
                    cmd.SetComputeMatrixParam(cs, _CameraInverseProjectionParams, passData.cameraInverseProjection);

                    // Bind Textures
                    cmd.SetComputeTextureParam(cs, kernel, _CameraDepthTextureParams, passData.sourceDepth);
                    cmd.SetComputeTextureParam(cs, kernel, _ResultParams, passData.targetColor);

                    // Dispatch
                    int threadGroupsX = Mathf.CeilToInt(passData.width / 8.0f);
                    int threadGroupsY = Mathf.CeilToInt(passData.height / 8.0f);
                    cmd.DispatchCompute(cs, kernel, threadGroupsX, threadGroupsY, 1);
                });

                // 4. Blit Pass (Fixes CS0122 & CS1061)
                // Manually add a Raster Pass to blit the compute result back to the camera
                using (var blitBuilder = renderGraph.AddRasterRenderPass<BlitPassData>("Blit Voxel to Camera", out var blitData))
                {
                    blitData.source = tempResult;
                    
                    // Read from the compute result
                    blitBuilder.UseTexture(blitData.source, AccessFlags.Read);
                    
                    // Write to the active camera color
                    blitBuilder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);

                    blitBuilder.SetRenderFunc((BlitPassData bData, RasterGraphContext context) =>
                    {
                        // Blitter.BlitTexture draws a full-screen quad using the source texture
                        // onto the currently bound RenderTarget (set by SetRenderAttachment)
                        Blitter.BlitTexture(context.cmd, bData.source, new Vector4(1, 1, 0, 0), 0, false);
                    });
                }
            }
        }
    }
}