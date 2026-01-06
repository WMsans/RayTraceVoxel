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
        
        // Ensure both SVOManager and VoxelDefinitionManager are ready
        if (SVOManager.Instance == null || !SVOManager.Instance.IsReady) return;
        if (VoxelDefinitionManager.Instance == null || VoxelDefinitionManager.Instance.VoxelMaterialBuffer == null) return;

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
        
        // --- Shader Property IDs ---
        // Result & Camera
        private static readonly int _ResultParams = Shader.PropertyToID("_Result");
        private static readonly int _ResultDepthParams = Shader.PropertyToID("_ResultDepth");
        private static readonly int _CameraToWorldParams = Shader.PropertyToID("_CameraToWorld");
        private static readonly int _CameraInverseProjectionParams = Shader.PropertyToID("_CameraInverseProjection");
        private static readonly int _CameraViewProjectionParams = Shader.PropertyToID("_CameraViewProjection");
        private static readonly int _CameraDepthTextureParams = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int _VoxelDepthTextureParams = Shader.PropertyToID("_VoxelDepthTexture");
        private static readonly int _ZBufferParamsID = Shader.PropertyToID("_ZBufferParams");
        private static readonly int _GridSizeParams = Shader.PropertyToID("_GridSize"); 
        
        // SVO Buffers
        private static readonly int _NodeBufferParams = Shader.PropertyToID("_NodeBuffer");
        private static readonly int _PayloadBufferParams = Shader.PropertyToID("_PayloadBuffer");
        private static readonly int _BrickBufferParams = Shader.PropertyToID("_BrickBuffer");
        private static readonly int _RaycastBufferParams = Shader.PropertyToID("_RaycastBuffer");

        // Palette / Materials (GPU Data Structures)
        private static readonly int _VoxelMaterialBufferParams = Shader.PropertyToID("_VoxelMaterialBuffer");
        private static readonly int _AlbedoTextureArrayParams = Shader.PropertyToID("_AlbedoTextureArray");
        private static readonly int _NormalTextureArrayParams = Shader.PropertyToID("_NormalTextureArray");
        private static readonly int _MaskTextureArrayParams = Shader.PropertyToID("_MaskTextureArray");

        // Lighting
        private static readonly int _MainLightPositionParams = Shader.PropertyToID("_MainLightPosition");
        private static readonly int _MainLightColorParams = Shader.PropertyToID("_MainLightColor");
        private static readonly int _MainLightShadowmapTextureParams = Shader.PropertyToID("_MainLightShadowmapTexture");
        private static readonly int _AdditionalLightsParams = Shader.PropertyToID("_AdditionalLights");
        private static readonly int _AdditionalLightCountParams = Shader.PropertyToID("_AdditionalLightCount");

        private GraphicsBuffer _lightBuffer;
        private VoxelLight[] _lightDataArray = new VoxelLight[64];
        
        // RTHandle Wrappers for External Textures
        private RTHandle _albedoHandle;
        private RTHandle _normalHandle;
        private RTHandle _maskHandle;

        public VoxelRaytracerPass(Settings settings)
        {
            _settings = settings;
            _shader = settings.raytraceShader;
            renderPassEvent = settings.injectionPoint;
        }
        
        public void Dispose()
        {
            _lightBuffer?.Dispose();
            _albedoHandle?.Release();
            _normalHandle?.Release();
            _maskHandle?.Release();
            VoxelRaytracerFeature.RaycastHitBuffer?.Release();
            VoxelRaytracerFeature.RaycastHitBuffer = null;
        }

        public void Setup(Material mat)
        {
            _compositeMaterial = mat;
        }
        
        private void CheckTextureHandle(ref RTHandle handle, Texture texture)
        {
            if (texture == null) return;
            if (handle == null || handle.rt != texture)
            {
                handle?.Release();
                handle = RTHandles.Alloc(texture);
            }
        }

        struct VoxelLight
        {
            public Vector4 position;
            public Vector4 color;
            public Vector4 attenuation;
        }

        private class PassData
        {
            public ComputeShader computeShader;
            public int kernel;
            
            // Textures & Buffers
            public TextureHandle sourceDepth;
            public TextureHandle targetColor; 
            public TextureHandle targetDepth;
            
            public GraphicsBuffer nodeBuffer;
            public GraphicsBuffer payloadBuffer;
            public GraphicsBuffer brickBuffer;
            public GraphicsBuffer raycastBuffer;

            // Palette Data
            public GraphicsBuffer materialBuffer;
            public TextureHandle albedoArray;
            public TextureHandle normalArray;
            public TextureHandle maskArray;

            // Camera & Grid
            public Matrix4x4 cameraToWorld;
            public Matrix4x4 cameraInverseProjection;
            public Matrix4x4 cameraViewProjection;
            public Vector4 zBufferParams;
            public int width;
            public int height;
            public float gridSize;
            
            // Lighting
            public Vector4 mainLightPosition;
            public Vector4 mainLightColor;
            public TextureHandle shadowMap;
            public GraphicsBuffer additionalLightsBuffer;
            public int additionalLightsCount;
        }

        // --- Render Graph Recording ---
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            var lightData = frameData.Get<UniversalLightData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            var cameraDesc = cameraData.cameraTargetDescriptor;

            // Create Temporary Textures
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

            // Setup Lights (Simplified for brevity, same as previous)
            SetupLights(renderingData, lightData, out var mainPos, out var mainCol, out int addCount);

            // Ensure Handles are valid
            CheckTextureHandle(ref _albedoHandle, VoxelDefinitionManager.Instance.albedoTextureArray);
            CheckTextureHandle(ref _normalHandle, VoxelDefinitionManager.Instance.normalTextureArray);
            CheckTextureHandle(ref _maskHandle, VoxelDefinitionManager.Instance.maskTextureArray);

            using (var builder = renderGraph.AddComputePass("Voxel Raytracer Pass", out PassData data))
            {
                data.computeShader = _shader;
                data.kernel = _shader.FindKernel("CSMain");
                
                // SVO Data
                data.nodeBuffer = SVOManager.Instance.NodeBuffer;
                data.payloadBuffer = SVOManager.Instance.PayloadBuffer;
                data.brickBuffer = SVOManager.Instance.BrickBuffer;
                
                if (VoxelRaytracerFeature.RaycastHitBuffer == null || !VoxelRaytracerFeature.RaycastHitBuffer.IsValid())
                {
                     VoxelRaytracerFeature.RaycastHitBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16); // float4
                }
                data.raycastBuffer = VoxelRaytracerFeature.RaycastHitBuffer;
                
                // Palette Data
                data.materialBuffer = VoxelDefinitionManager.Instance.VoxelMaterialBuffer;
                if (_albedoHandle != null) data.albedoArray = renderGraph.ImportTexture(_albedoHandle);
                if (_normalHandle != null) data.normalArray = renderGraph.ImportTexture(_normalHandle);
                if (_maskHandle != null) data.maskArray = renderGraph.ImportTexture(_maskHandle);

                // Camera Data
                data.width = desc.width;
                data.height = desc.height;
                data.cameraToWorld = cameraData.camera.cameraToWorldMatrix;
                data.cameraInverseProjection = cameraData.camera.projectionMatrix.inverse;
                var proj = GL.GetGPUProjectionMatrix(cameraData.camera.projectionMatrix, false);
                var view = cameraData.camera.worldToCameraMatrix;
                data.cameraViewProjection = proj * view;
                data.zBufferParams = Shader.GetGlobalVector(_ZBufferParamsID);
                data.gridSize = (float)SVOManager.Instance.resolution;

                data.sourceDepth = resourceData.cameraDepthTexture;
                data.targetColor = tempResult;
                data.targetDepth = tempResultDepth;

                // Light Data
                data.mainLightPosition = mainPos;
                data.mainLightColor = mainCol;
                data.shadowMap = resourceData.mainShadowsTexture;
                data.additionalLightsBuffer = _lightBuffer;
                data.additionalLightsCount = addCount;

                // Resource Declarations
                builder.UseTexture(data.sourceDepth, AccessFlags.Read);
                builder.UseTexture(data.targetColor, AccessFlags.Write);
                builder.UseTexture(data.targetDepth, AccessFlags.Write);
                if (data.shadowMap.IsValid()) builder.UseTexture(data.shadowMap, AccessFlags.Read);
                
                if (data.albedoArray.IsValid()) builder.UseTexture(data.albedoArray, AccessFlags.Read);
                if (data.normalArray.IsValid()) builder.UseTexture(data.normalArray, AccessFlags.Read);
                if (data.maskArray.IsValid()) builder.UseTexture(data.maskArray, AccessFlags.Read);

                builder.SetRenderFunc((PassData passData, ComputeGraphContext ctx) =>
                {
                    var cs = passData.computeShader;
                    var kernel = passData.kernel;
                    var cmd = ctx.cmd;

                    // Bind SVO
                    cmd.SetComputeBufferParam(cs, kernel, _NodeBufferParams, passData.nodeBuffer);
                    cmd.SetComputeBufferParam(cs, kernel, _PayloadBufferParams, passData.payloadBuffer);
                    cmd.SetComputeBufferParam(cs, kernel, _BrickBufferParams, passData.brickBuffer);
                    cmd.SetComputeBufferParam(cs, kernel, _RaycastBufferParams, passData.raycastBuffer);

                    // Bind Palette (NEW)
                    if (passData.materialBuffer != null)
                        cmd.SetComputeBufferParam(cs, kernel, _VoxelMaterialBufferParams, passData.materialBuffer);
                    if (passData.albedoArray.IsValid())
                        cmd.SetComputeTextureParam(cs, kernel, _AlbedoTextureArrayParams, passData.albedoArray);
                    if (passData.normalArray.IsValid())
                        cmd.SetComputeTextureParam(cs, kernel, _NormalTextureArrayParams, passData.normalArray);
                    if (passData.maskArray.IsValid())
                        cmd.SetComputeTextureParam(cs, kernel, _MaskTextureArrayParams, passData.maskArray);

                    // Bind Camera & Others
                    cmd.SetComputeMatrixParam(cs, _CameraToWorldParams, passData.cameraToWorld);
                    cmd.SetComputeMatrixParam(cs, _CameraInverseProjectionParams, passData.cameraInverseProjection);
                    cmd.SetComputeMatrixParam(cs, _CameraViewProjectionParams, passData.cameraViewProjection);
                    cmd.SetComputeVectorParam(cs, _ZBufferParamsID, passData.zBufferParams);
                    cmd.SetComputeFloatParam(cs, _GridSizeParams, passData.gridSize);
                    
                    cmd.SetComputeTextureParam(cs, kernel, _CameraDepthTextureParams, passData.sourceDepth);
                    cmd.SetComputeTextureParam(cs, kernel, _ResultParams, passData.targetColor);
                    cmd.SetComputeTextureParam(cs, kernel, _ResultDepthParams, passData.targetDepth);

                    // Bind Lights
                    cmd.SetComputeVectorParam(cs, _MainLightPositionParams, passData.mainLightPosition);
                    cmd.SetComputeVectorParam(cs, _MainLightColorParams, passData.mainLightColor);
                    if (passData.shadowMap.IsValid()) cmd.SetComputeTextureParam(cs, kernel, _MainLightShadowmapTextureParams, passData.shadowMap);
                    cmd.SetComputeBufferParam(cs, kernel, _AdditionalLightsParams, passData.additionalLightsBuffer);
                    cmd.SetComputeIntParam(cs, _AdditionalLightCountParams, passData.additionalLightsCount);

                    int groupsX = Mathf.CeilToInt(passData.width / 8.0f);
                    int groupsY = Mathf.CeilToInt(passData.height / 8.0f);
                    cmd.DispatchCompute(cs, kernel, groupsX, groupsY, 1);
                });
            }

            // Composite Pass
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
                    bData.material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    bData.material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    bData.material.SetInt("_ZWrite", 1);
                    var compareFunc = SystemInfo.usesReversedZBuffer ? CompareFunction.GreaterEqual : CompareFunction.LessEqual;
                    bData.material.SetInt("_ZTest", (int)compareFunc);
                    bData.material.SetTexture(_VoxelDepthTextureParams, bData.depthSource);
                    Blitter.BlitTexture(context.cmd, bData.source, new Vector4(1, 1, 0, 0), bData.material, 0);
                });
            }
        }

        // Helper for light extraction
        private void SetupLights(UniversalRenderingData renderingData, UniversalLightData lightData, out Vector4 mainPos, out Vector4 mainCol, out int addCount)
        {
            mainPos = new Vector4(0, 1, 0, 0);
            mainCol = Color.white; // Default to white, not black
            addCount = 0;

            var lights = lightData.visibleLights;
            int mainLightIndex = lightData.mainLightIndex;

            if (mainLightIndex != -1 && mainLightIndex < lights.Length)
            {
                VisibleLight mainLight = lights[mainLightIndex];
                if (mainLight.lightType == LightType.Directional)
                {
                    // Directional Light: Position is Direction (w=0)
                    // VisibleLight.localToWorldMatrix.GetColumn(2) is Forward (Z). 
                    // Light direction is usually -Forward.
                    Vector4 dir = -mainLight.localToWorldMatrix.GetColumn(2);
                    dir.w = 0;
                    mainPos = dir;
                    mainCol = mainLight.finalColor;
                }
            }

            // Collect Additional Lights
            int count = 0;
            for (int i = 0; i < lights.Length; i++)
            {
                if (i == mainLightIndex) continue;
                if (count >= _lightDataArray.Length) break;

                VisibleLight vl = lights[i];
                VoxelLight voxelLight = new VoxelLight();
                
                // Color
                voxelLight.color = vl.finalColor;
                
                // Position / Direction
                if (vl.lightType == LightType.Directional)
                {
                    Vector4 dir = -vl.localToWorldMatrix.GetColumn(2);
                    dir.w = 0;
                    voxelLight.position = dir;
                    voxelLight.attenuation = new Vector4(1, 0, 0, 0); // No attenuation for directional
                }
                else
                {
                    // Point/Spot
                    Vector4 pos = vl.localToWorldMatrix.GetColumn(3);
                    pos.w = 1;
                    voxelLight.position = pos;
                    
                    // Attenuation
                    // Range in x, Falloff in y
                    float range = vl.range;
                    voxelLight.attenuation = new Vector4(range, 1.0f / (range * range), 0, 0);
                }
                
                _lightDataArray[count] = voxelLight;
                count++;
            }
            addCount = count;
            
            if (_lightBuffer == null || _lightBuffer.count < _lightDataArray.Length)
            {
                _lightBuffer?.Dispose();
                _lightBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _lightDataArray.Length, System.Runtime.InteropServices.Marshal.SizeOf<VoxelLight>());
            }
            _lightBuffer.SetData(_lightDataArray, 0, 0, 64);
        }
        
        private class BlitPassData { public TextureHandle source; public TextureHandle depthSource; public Material material; }
    }
}