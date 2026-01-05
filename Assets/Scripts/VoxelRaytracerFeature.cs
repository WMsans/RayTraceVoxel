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
        public Shader compositeShader; 
        public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingOpaques;
    }

    public Settings settings = new Settings();
    private VoxelRaytracerPass _pass;
    private Material _compositeMaterial;

    public override void Create()
    {
        _pass = new VoxelRaytracerPass(settings);
        
        // Fallback or setup composite material
        if (settings.compositeShader != null)
            _compositeMaterial = new Material(settings.compositeShader);
        else
            // Basic Blit shader that supports blending
            _compositeMaterial = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/Universal Render Pipeline/Blit"));
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.raytraceShader == null) return;
        
        if (SVOManager.Instance == null)
        {
            // Debug.LogWarning("VoxelRaytracer: SVOManager instance is missing.");
            return;
        }

        if (!SVOManager.Instance.IsReady)
        {
            // Debug.LogWarning("VoxelRaytracer: SVOManager is not ready.");
            return;
        }

        // Ensure we pass the material to the pass
        _pass.Setup(_compositeMaterial);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_compositeMaterial);
    }

    // --- The Render Pass ---
    class VoxelRaytracerPass : ScriptableRenderPass
    {
        private Settings _settings;
        private ComputeShader _shader;
        private Material _compositeMaterial;
        
        // Shader Property IDs
        private static readonly int _ResultParams = Shader.PropertyToID("_Result");
        private static readonly int _CameraToWorldParams = Shader.PropertyToID("_CameraToWorld");
        private static readonly int _CameraInverseProjectionParams = Shader.PropertyToID("_CameraInverseProjection");
        private static readonly int _CameraDepthTextureParams = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int _GridSizeParams = Shader.PropertyToID("_GridSize"); // New
        
        private static readonly int _NodeBufferParams = Shader.PropertyToID("_NodeBuffer");
        private static readonly int _PayloadBufferParams = Shader.PropertyToID("_PayloadBuffer");
        private static readonly int _BrickBufferParams = Shader.PropertyToID("_BrickBuffer"); 

        public VoxelRaytracerPass(Settings settings)
        {
            _settings = settings;
            _shader = settings.raytraceShader;
            renderPassEvent = settings.injectionPoint;
        }

        public void Setup(Material mat)
        {
            _compositeMaterial = mat;
        }

        private class PassData
        {
            public ComputeShader computeShader;
            public int kernel;
            public TextureHandle sourceDepth;
            public TextureHandle targetColor; 
            public GraphicsBuffer nodeBuffer;
            public GraphicsBuffer payloadBuffer;
            public GraphicsBuffer brickBuffer;
            public Matrix4x4 cameraToWorld;
            public Matrix4x4 cameraInverseProjection;
            public int width;
            public int height;
            public float gridSize; // New
        }

        private class BlitPassData
        {
            public TextureHandle source;
            public Material material;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            var cameraDesc = cameraData.cameraTargetDescriptor;

            // 1. Setup Compute Target (Temporary RGBA)
            TextureDesc desc = new TextureDesc(cameraDesc.width, cameraDesc.height);
            desc.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
            desc.depthBufferBits = DepthBits.None;
            desc.enableRandomWrite = true;
            desc.name = "VoxelRaytraceResult";

            TextureHandle tempResult = renderGraph.CreateTexture(desc);

            // 2. Compute Pass
            using (var builder = renderGraph.AddComputePass("Voxel Raytracer Pass", out PassData data))
            {
                data.computeShader = _shader;
                data.kernel = _shader.FindKernel("CSMain");
                data.nodeBuffer = SVOManager.Instance.NodeBuffer;
                data.payloadBuffer = SVOManager.Instance.PayloadBuffer;
                data.brickBuffer = SVOManager.Instance.BrickBuffer;
                data.width = desc.width;
                data.height = desc.height;
                data.cameraToWorld = cameraData.camera.cameraToWorldMatrix;
                data.cameraInverseProjection = cameraData.camera.projectionMatrix.inverse;
                data.sourceDepth = resourceData.cameraDepthTexture;
                data.targetColor = tempResult;
                data.gridSize = (float)SVOManager.Instance.resolution; // New

                builder.UseTexture(data.sourceDepth, AccessFlags.Read);
                builder.UseTexture(data.targetColor, AccessFlags.Write);

                builder.SetRenderFunc((PassData passData, ComputeGraphContext ctx) =>
                {
                    var cs = passData.computeShader;
                    var kernel = passData.kernel;
                    var cmd = ctx.cmd;

                    cmd.SetComputeBufferParam(cs, kernel, _NodeBufferParams, passData.nodeBuffer);
                    cmd.SetComputeBufferParam(cs, kernel, _PayloadBufferParams, passData.payloadBuffer);
                    cmd.SetComputeBufferParam(cs, kernel, _BrickBufferParams, passData.brickBuffer);

                    cmd.SetComputeMatrixParam(cs, _CameraToWorldParams, passData.cameraToWorld);
                    cmd.SetComputeMatrixParam(cs, _CameraInverseProjectionParams, passData.cameraInverseProjection);
                    cmd.SetComputeFloatParam(cs, _GridSizeParams, passData.gridSize); // New

                    cmd.SetComputeTextureParam(cs, kernel, _CameraDepthTextureParams, passData.sourceDepth);
                    cmd.SetComputeTextureParam(cs, kernel, _ResultParams, passData.targetColor);

                    int groupsX = Mathf.CeilToInt(passData.width / 8.0f);
                    int groupsY = Mathf.CeilToInt(passData.height / 8.0f);
                    cmd.DispatchCompute(cs, kernel, groupsX, groupsY, 1);
                });
            }

            // 3. Composite Pass (Blend Over Opaque)
            using (var builder = renderGraph.AddRasterRenderPass<BlitPassData>("Composite Voxels", out var blitData))
            {
                blitData.source = tempResult;
                blitData.material = _compositeMaterial;

                // Read the Voxel result
                builder.UseTexture(blitData.source, AccessFlags.Read);
                
                // Write to Camera Color (Standard Forward Pipeline Integration)
                // We use LoadAction.Load to keep existing opaque geometry
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);

                builder.SetRenderFunc((BlitPassData bData, RasterGraphContext context) =>
                {
                    // Important: Set Blend Mode for Compositing
                    // SrcAlpha (Voxel Alpha) + OneMinusSrcAlpha (Background)
                    bData.material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    bData.material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    bData.material.SetInt("_ZWrite", 0);

                    // Draw full screen quad blending 'source' over the current attachment
                    Blitter.BlitTexture(context.cmd, bData.source, new Vector4(1, 1, 0, 0), bData.material, 0);
                });
            }
        }
    }
}