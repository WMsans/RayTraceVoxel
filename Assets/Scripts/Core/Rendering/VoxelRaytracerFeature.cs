using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using VoxelEngine.Core;
using VoxelEngine.Core.Data;
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
            if (VoxelVolumeRegistry.Volumes.Count == 0) return;
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
            private static readonly int _ResultParams = Shader.PropertyToID("_Result");
            private static readonly int _ResultDepthParams = Shader.PropertyToID("_ResultDepth");
            private static readonly int _CameraToWorldParams = Shader.PropertyToID("_CameraToWorld");
            private static readonly int _CameraInverseProjectionParams = Shader.PropertyToID("_CameraInverseProjection");
            private static readonly int _CameraViewProjectionParams = Shader.PropertyToID("_CameraViewProjection");
            private static readonly int _CameraDepthTextureParams = Shader.PropertyToID("_CameraDepthTexture");
            private static readonly int _VoxelDepthTextureParams = Shader.PropertyToID("_VoxelDepthTexture");
            private static readonly int _ZBufferParamsID = Shader.PropertyToID("_ZBufferParams");
            private static readonly int _GridSizeParams = Shader.PropertyToID("_GridSize"); 
            
            private static readonly int _NodeBufferParams = Shader.PropertyToID("_NodeBuffer");
            private static readonly int _PayloadBufferParams = Shader.PropertyToID("_PayloadBuffer");
            private static readonly int _BrickBufferParams = Shader.PropertyToID("_BrickBuffer");
            private static readonly int _BrickMaterialBufferParams = Shader.PropertyToID("_BrickMaterialBuffer");
            private static readonly int _RaycastBufferParams = Shader.PropertyToID("_RaycastBuffer");

            private static readonly int _VoxelMaterialBufferParams = Shader.PropertyToID("_VoxelMaterialBuffer");
            private static readonly int _AlbedoTextureArrayParams = Shader.PropertyToID("_AlbedoTextureArray");
            private static readonly int _NormalTextureArrayParams = Shader.PropertyToID("_NormalTextureArray");
            private static readonly int _MaskTextureArrayParams = Shader.PropertyToID("_MaskTextureArray");

            private static readonly int _MainLightPositionParams = Shader.PropertyToID("_MainLightPosition");
            private static readonly int _MainLightColorParams = Shader.PropertyToID("_MainLightColor");
            private static readonly int _MainLightShadowmapTextureParams = Shader.PropertyToID("_MainLightShadowmapTexture");
            private static readonly int _AdditionalLightsParams = Shader.PropertyToID("_AdditionalLights");
            private static readonly int _AdditionalLightCountParams = Shader.PropertyToID("_AdditionalLightCount");
            private static readonly int _ShadowCascadeCountParams = Shader.PropertyToID("_ShadowCascadeCount");

            // NEW: Chunk Transforms
            private static readonly int _ChunkWorldOriginParams = Shader.PropertyToID("_ChunkWorldOrigin");
            private static readonly int _ChunkWorldSizeParams = Shader.PropertyToID("_ChunkWorldSize");

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

            private class PassData
            {
                public ComputeShader computeShader;
                public int kernel;
                
                public TextureHandle sourceDepth;
                public TextureHandle targetColor; 
                public TextureHandle targetDepth;
                
                // We don't bind single buffers here anymore, we iterate
                public List<VoxelVolume> volumes; 
                public GraphicsBuffer raycastBuffer;

                public GraphicsBuffer materialBuffer;
                public TextureHandle albedoArray;
                public TextureHandle normalArray;
                public TextureHandle maskArray;

                public Matrix4x4 cameraToWorld;
                public Matrix4x4 cameraInverseProjection;
                public Matrix4x4 cameraViewProjection;
                public Vector4 zBufferParams;
                public int width;
                public int height;
                
                public Vector4 mainLightPosition;
                public Vector4 mainLightColor;
                public TextureHandle shadowMap;
                public GraphicsBuffer additionalLightsBuffer;
                public int additionalLightsCount;
                public int shadowCascadeCount;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (VoxelVolumeRegistry.Volumes.Count == 0) return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var lightData = frameData.Get<UniversalLightData>();
                var renderingData = frameData.Get<UniversalRenderingData>();
                var shadowData = frameData.Get<UniversalShadowData>(); 
                var cameraDesc = cameraData.cameraTargetDescriptor;

                // --- 1. Create Resources ---
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

                SetupLights(renderingData, lightData, out var mainPos, out var mainCol, out int addCount);

                CheckTextureHandle(ref _albedoHandle, VoxelDefinitionManager.Instance.albedoTextureArray);
                CheckTextureHandle(ref _normalHandle, VoxelDefinitionManager.Instance.normalTextureArray);
                CheckTextureHandle(ref _maskHandle, VoxelDefinitionManager.Instance.maskTextureArray);

                // --- 2. CLEAR PASSES ---
                
                // Pass 2a: Clear Color (Clears the main result texture)
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Clear Voxel Color", out var data))
                {
                    data.targetColor = tempResult;
                    builder.SetRenderAttachment(data.targetColor, 0, AccessFlags.Write);
                    builder.SetRenderFunc((PassData passData, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.ClearRenderTarget(RTClearFlags.Color, Color.clear, 1, 0);
                    });
                }

                // Pass 2b: Clear Depth (Clears the R32_SFloat custom depth texture)
                // We bind this as a Color Attachment because it is an R32 Float texture, not a hardware Depth Buffer.
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Clear Voxel Depth", out var data))
                {
                    data.targetDepth = tempResultDepth;
                    builder.SetRenderAttachment(data.targetDepth, 0, AccessFlags.Write);
                    
                    builder.SetRenderFunc((PassData passData, RasterGraphContext ctx) =>
                    {
                        // Handle Reversed-Z (Far is 0.0, Near is 1.0)
                        bool reversedZ = SystemInfo.usesReversedZBuffer;
                        float clearDepth = reversedZ ? 0.0f : 1.0f;
                        // Clear the R channel (floats) to the Far Plane value
                        ctx.cmd.ClearRenderTarget(RTClearFlags.Color, new Color(clearDepth, 0, 0, 0), 1, 0);
                    });
                }

                // --- 3. COMPUTE PASS ---
                using (var builder = renderGraph.AddComputePass("Voxel Raytracer Pass", out PassData data))
                {
                    data.computeShader = _shader;
                    data.kernel = _shader.FindKernel("CSMain");
                    
                    data.volumes = new List<VoxelVolume>(VoxelVolumeRegistry.Volumes);

                    if (VoxelRaytracerFeature.RaycastHitBuffer == null || !VoxelRaytracerFeature.RaycastHitBuffer.IsValid())
                    {
                        VoxelRaytracerFeature.RaycastHitBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16); 
                    }
                    data.raycastBuffer = VoxelRaytracerFeature.RaycastHitBuffer;
                    
                    data.materialBuffer = VoxelDefinitionManager.Instance.VoxelMaterialBuffer;
                    if (_albedoHandle != null) data.albedoArray = renderGraph.ImportTexture(_albedoHandle);
                    if (_normalHandle != null) data.normalArray = renderGraph.ImportTexture(_normalHandle);
                    if (_maskHandle != null) data.maskArray = renderGraph.ImportTexture(_maskHandle);

                    data.width = desc.width;
                    data.height = desc.height;
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
                    data.shadowCascadeCount = shadowData.mainLightShadowCascadesCount;

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

                        // 2. Global Uniforms
                        cmd.SetComputeBufferParam(cs, kernel, _RaycastBufferParams, passData.raycastBuffer);

                        if (passData.materialBuffer != null)
                            cmd.SetComputeBufferParam(cs, kernel, _VoxelMaterialBufferParams, passData.materialBuffer);
                        if (passData.albedoArray.IsValid())
                            cmd.SetComputeTextureParam(cs, kernel, _AlbedoTextureArrayParams, passData.albedoArray);
                        if (passData.normalArray.IsValid())
                            cmd.SetComputeTextureParam(cs, kernel, _NormalTextureArrayParams, passData.normalArray);
                        if (passData.maskArray.IsValid())
                            cmd.SetComputeTextureParam(cs, kernel, _MaskTextureArrayParams, passData.maskArray);

                        cmd.SetComputeMatrixParam(cs, _CameraToWorldParams, passData.cameraToWorld);
                        cmd.SetComputeMatrixParam(cs, _CameraInverseProjectionParams, passData.cameraInverseProjection);
                        cmd.SetComputeMatrixParam(cs, _CameraViewProjectionParams, passData.cameraViewProjection);
                        cmd.SetComputeVectorParam(cs, _ZBufferParamsID, passData.zBufferParams);
                        
                        cmd.SetComputeTextureParam(cs, kernel, _CameraDepthTextureParams, passData.sourceDepth);
                        cmd.SetComputeTextureParam(cs, kernel, _ResultParams, passData.targetColor);
                        cmd.SetComputeTextureParam(cs, kernel, _ResultDepthParams, passData.targetDepth);

                        cmd.SetComputeVectorParam(cs, _MainLightPositionParams, passData.mainLightPosition);
                        cmd.SetComputeVectorParam(cs, _MainLightColorParams, passData.mainLightColor);
                        if (passData.shadowMap.IsValid()) cmd.SetComputeTextureParam(cs, kernel, _MainLightShadowmapTextureParams, passData.shadowMap);
                        
                        cmd.SetComputeBufferParam(cs, kernel, _AdditionalLightsParams, passData.additionalLightsBuffer);
                        cmd.SetComputeIntParam(cs, _AdditionalLightCountParams, passData.additionalLightsCount);
                        cmd.SetComputeIntParam(cs, _ShadowCascadeCountParams, passData.shadowCascadeCount);

                        // 3. Loop and Render Each Volume
                        int groupsX = Mathf.CeilToInt(passData.width / 8.0f);
                        int groupsY = Mathf.CeilToInt(passData.height / 8.0f);

                        foreach (var vol in passData.volumes)
                        {
                            if (!vol.IsReady || !vol.gameObject.activeInHierarchy) continue;

                            cmd.SetComputeBufferParam(cs, kernel, _NodeBufferParams, vol.NodeBuffer);
                            cmd.SetComputeBufferParam(cs, kernel, _PayloadBufferParams, vol.PayloadBuffer);
                            cmd.SetComputeBufferParam(cs, kernel, _BrickBufferParams, vol.BrickBuffer);
                            cmd.SetComputeBufferParam(cs, kernel, _BrickMaterialBufferParams, vol.BrickMaterialBuffer);

                            cmd.SetComputeFloatParam(cs, _GridSizeParams, (float)vol.Resolution);
                            
                            Vector3 origin = vol.transform.position;
                            float size = vol.Resolution * vol.transform.localScale.x;

                            cmd.SetComputeVectorParam(cs, _ChunkWorldOriginParams, origin);
                            cmd.SetComputeFloatParam(cs, _ChunkWorldSizeParams, size);
                            
                            cmd.DispatchCompute(cs, kernel, groupsX, groupsY, 1);
                        }
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

            private void SetupLights(UniversalRenderingData renderingData, UniversalLightData lightData, out Vector4 mainPos, out Vector4 mainCol, out int addCount)
            {
                mainPos = new Vector4(0, 1, 0, 0);
                mainCol = Color.white; 
                addCount = 0;

                var lights = lightData.visibleLights;
                int mainLightIndex = lightData.mainLightIndex;

                if (mainLightIndex != -1 && mainLightIndex < lights.Length)
                {
                    VisibleLight mainLight = lights[mainLightIndex];
                    if (mainLight.lightType == LightType.Directional)
                    {
                        Vector4 dir = -mainLight.localToWorldMatrix.GetColumn(2);
                        dir.w = 0;
                        mainPos = dir;
                        mainCol = mainLight.finalColor;
                    }
                }

                int count = 0;
                for (int i = 0; i < lights.Length; i++)
                {
                    if (i == mainLightIndex) continue;
                    if (count >= _lightDataArray.Length) break;

                    VisibleLight vl = lights[i];
                    VoxelLight voxelLight = new VoxelLight();
                    voxelLight.color = vl.finalColor;
                    
                    if (vl.lightType == LightType.Directional)
                    {
                        Vector4 dir = -vl.localToWorldMatrix.GetColumn(2);
                        dir.w = 0;
                        voxelLight.position = dir;
                        voxelLight.attenuation = new Vector4(1, 0, 0, 0); 
                    }
                    else
                    {
                        Vector4 pos = vl.localToWorldMatrix.GetColumn(3);
                        pos.w = 1;
                        voxelLight.position = pos;
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
}