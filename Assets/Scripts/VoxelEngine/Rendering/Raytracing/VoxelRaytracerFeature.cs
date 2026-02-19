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
        public VoxelRaytracerSettings settings = new VoxelRaytracerSettings();
        private VoxelRaytracerPass _pass;
        private Material _compositeMaterial;
        private Material _fxaaMaterial;
        private Material _taaMaterial;
        private Material _godRayMaterial;

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

            if (settings.godRayShader != null)
                _godRayMaterial = new Material(settings.godRayShader);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (settings.raytraceShader == null) return;
            if (VoxelVolumePool.Instance == null) return;

            _pass.UpdateSettings(settings);
            _pass.Setup(_compositeMaterial, _fxaaMaterial, _taaMaterial, _godRayMaterial);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_compositeMaterial);
            CoreUtils.Destroy(_fxaaMaterial);
            CoreUtils.Destroy(_taaMaterial);
            CoreUtils.Destroy(_godRayMaterial);
            _pass?.Dispose();
        }

        class VoxelRaytracerPass : ScriptableRenderPass
        {
            private VoxelRaytracerSettings _settings;
            private ComputeShader _shader;
            private Material _compositeMaterial;
            private Material _fxaaMaterial;
            private Material _taaMaterial;
            private Material _godRayMaterial;
            private Material _copyMaterial;

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

            public VoxelRaytracerPass(VoxelRaytracerSettings settings)
            {
                _settings = settings;
                _shader = settings.raytraceShader;
                renderPassEvent = settings.injectionPoint;
                _copyMaterial = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/Universal Render Pipeline/Blit"));
            }

            public void UpdateSettings(VoxelRaytracerSettings newSettings) { _settings = newSettings; }
            public void Setup(Material composite, Material fxaa, Material taa, Material godrays)
            {
                _compositeMaterial = composite;
                _fxaaMaterial = fxaa;
                _taaMaterial = taa;
                _godRayMaterial = godrays;
            }

            public void Dispose()
            {
                _albedoHandle?.Release();
                _normalHandle?.Release();
                _maskHandle?.Release();
                _blueNoiseHandle?.Release();
                CoreUtils.Destroy(_compositeMaterial);

                if (VoxelRaytracerFeature.RaycastHitBuffer != null)
                {
                    VoxelRaytracerFeature.RaycastHitBuffer.Release();
                    VoxelRaytracerFeature.RaycastHitBuffer = null;
                }
                foreach (var kvp in _cameraHistory)
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

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (VoxelVolumePool.Instance == null) return;
                var cameraData = frameData.Get<UniversalCameraData>();

                if (_settings.cullFrustum)
                {
                    Plane[] allPlanes = GeometryUtility.CalculateFrustumPlanes(cameraData.camera);
                    Plane[] cullingPlanes = _settings.useCameraFarPlane ? allPlanes : new Plane[] { allPlanes[0], allPlanes[1], allPlanes[2], allPlanes[3], allPlanes[4] };
                    VoxelVolumePool.Instance.UpdateVisibility(cullingPlanes, cameraData.camera.transform.position, _settings.shadowDistance);
                }
                else
                {
                    VoxelVolumePool.Instance.UpdateVisibility(null);
                }
                if (VoxelVolumePool.Instance.VisibleChunkCount == 0) return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var lightData = frameData.Get<UniversalLightData>();
                var cameraDesc = cameraData.cameraTargetDescriptor;

                float currentScale = 1.0f;
                int iterations = 128;
                int marchSteps = 64;
                switch (_settings.qualityLevel)
                {
                    case VoxelRaytracerSettings.QualityLevel.High: currentScale = 1.0f; break;
                    case VoxelRaytracerSettings.QualityLevel.Low: currentScale = 0.5f; iterations = 64; marchSteps = 32; break;
                    case VoxelRaytracerSettings.QualityLevel.Custom: currentScale = _settings.renderScale; iterations = _settings.iterations; marchSteps = _settings.marchSteps; break;
                }
                int scaledWidth = Mathf.Max(1, Mathf.RoundToInt(cameraDesc.width * currentScale));
                int scaledHeight = Mathf.Max(1, Mathf.RoundToInt(cameraDesc.height * currentScale));

                TextureDesc colorDesc = new TextureDesc(scaledWidth, scaledHeight) { colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite = true, name = "VoxelRaytraceResult_LowRes" };
                TextureHandle lowResResult = renderGraph.CreateTexture(colorDesc);
                TextureDesc depthDesc = new TextureDesc(scaledWidth, scaledHeight) { colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat, enableRandomWrite = true, name = "VoxelRaytraceDepth_LowRes" };
                TextureHandle lowResDepth = renderGraph.CreateTexture(depthDesc);
                TextureDesc normDesc = new TextureDesc(scaledWidth, scaledHeight) { colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite = true, name = "VoxelRaytraceNormals" };
                TextureHandle lowResNormals = renderGraph.CreateTexture(normDesc);
                TextureDesc mvDesc = new TextureDesc(scaledWidth, scaledHeight) { colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16_SFloat, enableRandomWrite = true, name = "VoxelMotionVectors" };
                TextureHandle motionVectorTex = renderGraph.CreateTexture(mvDesc);

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

                TextureHandle historyRead = TextureHandle.nullHandle;
                TextureHandle historyWrite = TextureHandle.nullHandle;
                if (useTAA)
                {
                    if (!_cameraHistory.TryGetValue(cam, out CameraHistory hist)) { hist = new CameraHistory(); _cameraHistory[cam] = hist; }
                    for (int i = 0; i < 2; i++)
                    {
                        if (hist.historyTextures[i] == null || hist.historyTextures[i].rt.width != scaledWidth || hist.historyTextures[i].rt.height != scaledHeight)
                        {
                            hist.historyTextures[i]?.Release();
                            hist.historyTextures[i] = RTHandles.Alloc(scaledWidth, scaledHeight, colorFormat: UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, name: $"VoxelHistory_{i}");
                        }
                    }
                    historyRead = renderGraph.ImportTexture(hist.historyTextures[hist.currentIndex]);
                    historyWrite = renderGraph.ImportTexture(hist.historyTextures[(hist.currentIndex + 1) % 2]);
                    hist.currentIndex = (hist.currentIndex + 1) % 2;
                }

                TextureHandle compositeOutput;
                bool useFXAA = _settings.enableFXAA && _fxaaMaterial != null;
                if (useFXAA)
                {
                    TextureDesc fullScreenDesc = new TextureDesc(cameraDesc.width, cameraDesc.height) { colorFormat = cameraDesc.graphicsFormat, name = "VoxelComposite_PreFXAA" };
                    compositeOutput = renderGraph.CreateTexture(fullScreenDesc);
                }
                else
                {
                    compositeOutput = resourceData.activeColorTexture;
                }

                CheckTextureHandle(ref _albedoHandle, VoxelDefinitionManager.Instance.albedoTextureArray);
                CheckTextureHandle(ref _normalHandle, VoxelDefinitionManager.Instance.normalTextureArray);
                CheckTextureHandle(ref _maskHandle, VoxelDefinitionManager.Instance.maskTextureArray);
                CheckTextureHandle(ref _blueNoiseHandle, _settings.blueNoiseTexture);
                SetupLights(lightData, out var mainPos, out var mainCol);

                float fov = cameraData.camera.fieldOfView;
                float rawPixelSpread = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 2.0f / cameraDesc.height;
                float finalSpread = rawPixelSpread * _settings.lodBias;

                using (var builder = renderGraph.AddComputePass("Voxel Raytracer", out PassDataClasses.PassData data))
                {
                    data.computeShader = _shader; data.kernel = _shader.FindKernel("CSMain");
                    if (VoxelRaytracerFeature.RaycastHitBuffer == null || !VoxelRaytracerFeature.RaycastHitBuffer.IsValid()) VoxelRaytracerFeature.RaycastHitBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, 16);
                    data.raycastBuffer = VoxelRaytracerFeature.RaycastHitBuffer;
                    var pool = VoxelVolumePool.Instance; data.nodeBuffer = pool.GlobalNodeBuffer; data.payloadBuffer = pool.GlobalPayloadBuffer; data.brickDataBuffer = pool.GlobalBrickDataBuffer; data.pageTableBuffer = pool.GlobalPageTableBuffer; data.chunkBuffer = pool.ChunkBuffer; data.chunkCount = pool.VisibleChunkCount; data.tlasGridBuffer = pool.TLASGridBuffer; data.tlasChunkIndexBuffer = pool.TLASChunkIndexBuffer; data.tlasBoundsMin = pool.TLASBoundsMin; data.tlasBoundsMax = pool.TLASBoundsMax; data.tlasResolution = pool.TLASResolution; data.frameCount = Time.frameCount; data.materialBuffer = VoxelDefinitionManager.Instance.VoxelMaterialBuffer;
                    if (_albedoHandle != null) data.albedoArray = renderGraph.ImportTexture(_albedoHandle);
                    if (_normalHandle != null) data.normalArray = renderGraph.ImportTexture(_normalHandle);
                    if (_maskHandle != null) data.maskArray = renderGraph.ImportTexture(_maskHandle);
                    if (_blueNoiseHandle != null) data.blueNoise = renderGraph.ImportTexture(_blueNoiseHandle);
                    data.width = scaledWidth; data.height = scaledHeight; data.cameraToWorld = cameraData.camera.cameraToWorldMatrix; data.cameraInverseProjection = cameraData.camera.projectionMatrix.inverse; data.viewProj = viewProj; data.prevViewProj = prevViewProj; data.zBufferParams = Shader.GetGlobalVector(ShaderParamIDs._ZBufferParamsID); data.sourceDepth = resourceData.cameraDepthTexture; data.sourceColor = resourceData.activeColorTexture; data.targetColor = lowResResult; data.targetDepth = lowResDepth; data.targetNormals = lowResNormals; data.targetMotionVector = motionVectorTex; data.mainLightPosition = mainPos; data.mainLightColor = mainCol;
                    data.raytraceParams = new Vector4(finalSpread, jitterX, jitterY, _settings.textureScale);
                    data.mousePosition = VoxelRaytracerFeature.MousePosition * currentScale; data.maxIterations = iterations; data.maxMarchSteps = marchSteps;
                    data.debugNormals = (_settings.debugMode == VoxelRaytracerSettings.DebugMode.Normals) ? 1.0f : 0.0f;
                    data.debugBricks = (_settings.debugMode == VoxelRaytracerSettings.DebugMode.Bricks) ? 1.0f : 0.0f;
                    data.celShadeParams = new Vector4((float)_settings.celSteps, _settings.shadowBrightness, 0, 0);
                    data.atmosphereParams = new Vector4(_settings.enableAtmosphere ? _settings.atmosphereDensity : 0.0f, 0, 0, 0);
                    data.atmosphereColor = _settings.atmosphereColor;

                    builder.UseTexture(data.targetColor, AccessFlags.Write); builder.UseTexture(data.targetDepth, AccessFlags.Write);
                    builder.UseTexture(data.targetNormals, AccessFlags.Write); builder.UseTexture(data.targetMotionVector, AccessFlags.Write);
                    builder.UseTexture(data.sourceDepth, AccessFlags.Read); builder.UseTexture(data.sourceColor, AccessFlags.Read);
                    if (data.albedoArray.IsValid()) builder.UseTexture(data.albedoArray, AccessFlags.Read);
                    if (data.normalArray.IsValid()) builder.UseTexture(data.normalArray, AccessFlags.Read);
                    if (data.maskArray.IsValid()) builder.UseTexture(data.maskArray, AccessFlags.Read);
                    if (data.blueNoise.IsValid()) builder.UseTexture(data.blueNoise, AccessFlags.Read);

                    builder.SetRenderFunc((PassDataClasses.PassData pd, ComputeGraphContext ctx) =>
                    {
                        var cs = pd.computeShader; var ker = pd.kernel; var cmd = ctx.cmd;
                        cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._GlobalNodeBufferParams, pd.nodeBuffer);
                        cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._GlobalPayloadBufferParams, pd.payloadBuffer);
                        cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._GlobalBrickDataBufferParams, pd.brickDataBuffer);
                        cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._PageTableBufferParams, pd.pageTableBuffer);
                        cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._ChunkBufferParams, pd.chunkBuffer);
                        cmd.SetComputeIntParam(cs, ShaderParamIDs._ChunkCountParams, pd.chunkCount);
                        if (pd.tlasGridBuffer != null) cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._TLASGridBufferParams, pd.tlasGridBuffer);
                        if (pd.tlasChunkIndexBuffer != null) cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._TLASChunkIndexBufferParams, pd.tlasChunkIndexBuffer);
                        cmd.SetComputeVectorParam(cs, ShaderParamIDs._TLASBoundsMinParams, pd.tlasBoundsMin); cmd.SetComputeVectorParam(cs, ShaderParamIDs._TLASBoundsMaxParams, pd.tlasBoundsMax); cmd.SetComputeIntParam(cs, ShaderParamIDs._TLASResolutionParams, pd.tlasResolution); cmd.SetComputeIntParam(cs, ShaderParamIDs._FrameCountParams, pd.frameCount); cmd.SetComputeVectorParam(cs, ShaderParamIDs._MousePositionParams, pd.mousePosition); cmd.SetComputeIntParam(cs, ShaderParamIDs._MaxIterationsParams, pd.maxIterations); cmd.SetComputeIntParam(cs, ShaderParamIDs._MaxMarchStepsParams, pd.maxMarchSteps);
                        if (pd.blueNoise.IsValid()) cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._BlueNoiseTextureParams, pd.blueNoise);
                        if (pd.materialBuffer != null) cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._VoxelMaterialBufferParams, pd.materialBuffer);
                        if (pd.albedoArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._AlbedoTextureArrayParams, pd.albedoArray);
                        if (pd.normalArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._NormalTextureArrayParams, pd.normalArray);
                        if (pd.maskArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._MaskTextureArrayParams, pd.maskArray);
                        cmd.SetComputeMatrixParam(cs, ShaderParamIDs._CameraToWorldParams, pd.cameraToWorld); cmd.SetComputeMatrixParam(cs, ShaderParamIDs._CameraInverseProjectionParams, pd.cameraInverseProjection); cmd.SetComputeMatrixParam(cs, ShaderParamIDs._CameraViewProjectionParams, pd.viewProj); cmd.SetComputeMatrixParam(cs, ShaderParamIDs._PrevViewProjMatrixParams, pd.prevViewProj);
                        cmd.SetComputeVectorParam(cs, ShaderParamIDs._ZBufferParamsID, pd.zBufferParams); cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._CameraDepthTextureParams, pd.sourceDepth); cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._SourceTexParams, pd.sourceColor); cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._ResultParams, pd.targetColor); cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._ResultDepthParams, pd.targetDepth); cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._ResultNormalsParams, pd.targetNormals); cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._MotionVectorTextureParams, pd.targetMotionVector);
                        cmd.SetComputeVectorParam(cs, ShaderParamIDs._MainLightPositionParams, pd.mainLightPosition); cmd.SetComputeVectorParam(cs, ShaderParamIDs._MainLightColorParams, pd.mainLightColor); cmd.SetComputeVectorParam(cs, ShaderParamIDs._RaytraceParams, pd.raytraceParams); cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._RaycastBufferParams, pd.raycastBuffer);
                        cmd.SetComputeFloatParam(cs, ShaderParamIDs._DebugViewNormalsParams, pd.debugNormals);
                        cmd.SetComputeFloatParam(cs, ShaderParamIDs._DebugViewBricksParams, pd.debugBricks);
                        cmd.SetComputeVectorParam(cs, ShaderParamIDs._CelShadeParams, pd.celShadeParams);
                        cmd.SetComputeVectorParam(cs, ShaderParamIDs._AtmosphereParams, pd.atmosphereParams);
                        cmd.SetComputeVectorParam(cs, ShaderParamIDs._AtmosphereColor, pd.atmosphereColor);

                        int groupsX = Mathf.CeilToInt(pd.width / 8.0f); int groupsY = Mathf.CeilToInt(pd.height / 8.0f);
                        cmd.DispatchCompute(cs, ker, groupsX, groupsY, 1);
                    });
                }

                TextureHandle compositeSource = lowResResult;

                bool hasGrass = VoxelGrassRenderer.ActiveRenderers.Count > 0;
                bool hasLeaves = VoxelLeafRenderer.ActiveLeafRenderers.Count > 0;

                if (hasGrass || hasLeaves)
                {
                    TextureDesc copyDesc = new TextureDesc(scaledWidth, scaledHeight) { colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat, name = "VoxelDepthCopy" };
                    TextureHandle voxelDepthCopy = renderGraph.CreateTexture(copyDesc);

                    using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.CopyPassData>("Copy Voxel Depth", out var copyData))
                    {
                        copyData.source = lowResDepth;
                        copyData.dest = voxelDepthCopy;
                        copyData.material = _copyMaterial;
                        builder.UseTexture(copyData.source, AccessFlags.Read);
                        builder.SetRenderAttachment(copyData.dest, 0, AccessFlags.Write);
                        builder.SetRenderFunc((PassDataClasses.CopyPassData cData, RasterGraphContext context) =>
                        {
                            Blitter.BlitTexture(context.cmd, cData.source, new Vector4(1, 1, 0, 0), cData.material, 0);
                        });
                    }

                    using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.VegetationPassData>("Voxel Vegetation", out var vegData))
                    {
                        builder.AllowGlobalStateModification(true);

                        vegData.colorTarget = lowResResult;
                        vegData.depthTarget = lowResDepth;
                        vegData.normalTarget = lowResNormals;
                        vegData.depthCopy = voxelDepthCopy;

                        TextureDesc tempDepthDesc = new TextureDesc(scaledWidth, scaledHeight) { depthBufferBits = DepthBits.Depth32, name = "VegetationTempZ" };
                        vegData.tempDepthBuffer = renderGraph.CreateTexture(tempDepthDesc);

                        builder.SetRenderAttachment(vegData.colorTarget, 0, AccessFlags.Write);
                        builder.SetRenderAttachment(vegData.depthTarget, 1, AccessFlags.Write);
                        builder.SetRenderAttachment(vegData.normalTarget, 2, AccessFlags.Write);
                        builder.SetRenderAttachmentDepth(vegData.tempDepthBuffer, AccessFlags.Write);
                        builder.UseTexture(vegData.depthCopy, AccessFlags.Read);

                        builder.SetRenderFunc((PassDataClasses.VegetationPassData vData, RasterGraphContext context) =>
                        {
                            context.cmd.ClearRenderTarget(true, false, Color.black);
                            context.cmd.SetGlobalTexture(ShaderParamIDs._VoxelDepthCopyParams, vData.depthCopy);
                            if (hasGrass) { foreach (var renderer in VoxelGrassRenderer.ActiveRenderers) renderer.Draw(context.cmd); }
                            if (hasLeaves) { foreach (var renderer in VoxelLeafRenderer.ActiveLeafRenderers) renderer.Draw(context.cmd); }
                        });
                    }
                }

                if (_settings.enableGodRays && _godRayMaterial != null)
                {
                    Vector3 vectorToSun = new Vector3(mainPos.x, mainPos.y, mainPos.z).normalized;

                    if (vectorToSun == Vector3.zero) vectorToSun = Vector3.up;

                    float sunHeight = Mathf.Clamp01(Vector3.Dot(vectorToSun, Vector3.up));

                    float dynamicSunThreshold = Mathf.SmoothStep(_settings.dawnSunThreshold, _settings.noonSunThreshold, sunHeight);

                    Vector3 cameraPos = cameraData.camera.transform.position;
                    Vector3 sunWorldPos = cameraPos + vectorToSun * 10000.0f;
                    Vector3 viewportPos = cameraData.camera.WorldToViewportPoint(sunWorldPos);

                    float isVisible = (viewportPos.z > 0) ? 1.0f : 0.0f;

                    TextureDesc occDesc = new TextureDesc(scaledWidth, scaledHeight) { colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, name = "GodRays_Occluders" };
                    TextureHandle occluderTex = renderGraph.CreateTexture(occDesc);

                    TextureDesc blurDesc = new TextureDesc(scaledWidth, scaledHeight) { colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, name = "GodRays_Blur" };
                    TextureHandle blurTex = renderGraph.CreateTexture(blurDesc);

                    using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.GodRayPassData>("God Rays Occluders", out var grData))
                    {
                        builder.AllowGlobalStateModification(true);

                        grData.sourceDepth = lowResDepth;
                        grData.occluderTex = occluderTex;
                        grData.material = _godRayMaterial;
                        grData.lightPosScreen = new Vector3(viewportPos.x, viewportPos.y, isVisible);
                        grData.lightColor = _settings.lightSourceColor;
                        grData.sunThreshold = dynamicSunThreshold;

                        builder.UseTexture(grData.sourceDepth, AccessFlags.Read);
                        builder.SetRenderAttachment(grData.occluderTex, 0, AccessFlags.Write);

                        builder.SetRenderFunc((PassDataClasses.GodRayPassData d, RasterGraphContext ctx) =>
                        {
                            ctx.cmd.SetGlobalTexture(ShaderParamIDs._VoxelDepthTextureParams, d.sourceDepth);
                            d.material.SetVector(ShaderParamIDs._LightPositionParams, d.lightPosScreen);
                            d.material.SetColor(ShaderParamIDs._LightColorGodRayParams, d.lightColor);
                            d.material.SetFloat(ShaderParamIDs._SunThresholdParams, d.sunThreshold);
                            Blitter.BlitTexture(ctx.cmd, d.sourceDepth, new Vector4(1, 1, 0, 0), d.material, 0);
                        });
                    }

                    using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.GodRayPassData>("God Rays Blur", out var grData))
                    {
                        grData.occluderTex = occluderTex;
                        grData.blurTex = blurTex;
                        grData.material = _godRayMaterial;
                        grData.lightPosScreen = new Vector3(viewportPos.x, viewportPos.y, isVisible);
                        grData.density = _settings.rayDensity;
                        grData.decay = _settings.rayDecay;
                        grData.weight = _settings.rayWeight;
                        grData.exposure = _settings.rayExposure;
                        grData.samples = _settings.raySamples;

                        builder.UseTexture(grData.occluderTex, AccessFlags.Read);
                        builder.SetRenderAttachment(grData.blurTex, 0, AccessFlags.Write);

                        builder.SetRenderFunc((PassDataClasses.GodRayPassData d, RasterGraphContext ctx) =>
                        {
                            d.material.SetVector(ShaderParamIDs._LightPositionParams, d.lightPosScreen);
                            d.material.SetFloat(ShaderParamIDs._DensityParams, d.density);
                            d.material.SetFloat(ShaderParamIDs._DecayParams, d.decay);
                            d.material.SetFloat(ShaderParamIDs._WeightParams, d.weight);
                            d.material.SetFloat(ShaderParamIDs._ExposureParams, d.exposure);
                            d.material.SetInt(ShaderParamIDs._SamplesParams, d.samples);
                            Blitter.BlitTexture(ctx.cmd, d.occluderTex, new Vector4(1, 1, 0, 0), d.material, 1);
                        });
                    }

                    using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.GodRayPassData>("God Rays Blend", out var grData))
                    {
                        grData.blurTex = blurTex;
                        grData.destTex = lowResResult;
                        grData.material = _godRayMaterial;

                        builder.UseTexture(grData.blurTex, AccessFlags.Read);
                        builder.SetRenderAttachment(grData.destTex, 0, AccessFlags.ReadWrite);

                        builder.SetRenderFunc((PassDataClasses.GodRayPassData d, RasterGraphContext ctx) =>
                        {
                            Blitter.BlitTexture(ctx.cmd, d.blurTex, new Vector4(1, 1, 0, 0), d.material, 2);
                        });
                    }
                }

                if (useTAA)
                {
                    using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.TAAPassData>("Voxel TAA", out var taaData))
                    {
                        taaData.source = lowResResult; taaData.history = historyRead; taaData.motion = motionVectorTex; taaData.destination = historyWrite; taaData.material = _taaMaterial; taaData.blend = _settings.taaBlend;
                        builder.UseTexture(taaData.source, AccessFlags.Read); builder.UseTexture(taaData.history, AccessFlags.Read); builder.UseTexture(taaData.motion, AccessFlags.Read); builder.SetRenderAttachment(taaData.destination, 0, AccessFlags.Write);
                        builder.SetRenderFunc((PassDataClasses.TAAPassData tData, RasterGraphContext context) => { tData.material.SetTexture(ShaderParamIDs._HistoryTexParams, tData.history); tData.material.SetTexture(ShaderParamIDs._MotionVectorTextureParams, tData.motion); tData.material.SetFloat(ShaderParamIDs._BlendParams, tData.blend); Blitter.BlitTexture(context.cmd, tData.source, new Vector4(1, 1, 0, 0), tData.material, 0); });
                    }
                    compositeSource = historyWrite;
                }

                using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.CompositePassData>("Composite & Upscale", out var compData))
                {
                    compData.source = compositeSource;
                    compData.depthSource = lowResDepth;
                    compData.normalSource = lowResNormals;
                    compData.material = _compositeMaterial;
                    compData.useFSR = (_settings.upscalingMode == VoxelRaytracerSettings.UpscalingMode.SpatialFSR);
                    compData.sharpness = _settings.sharpness;

                    compData.enableOutline = _settings.enableOutline;
                    compData.outlineColor = _settings.outlineColor;
                    compData.outlineThickness = _settings.outlineThickness;
                    compData.outlineStrength = _settings.outlineStrength;
                    compData.outlineShadowStrength = _settings.outlineShadowStrength;
                    compData.outlineColor = _settings.outlineColor;
                    compData.mainLightColor = mainCol;
                    compData.mainLightDirection = mainPos;

                    compData.normalColor = _settings.normalHighlightColor;
                    compData.normalStrength = _settings.normalHighlightStrength;
                    compData.normalThreshold = _settings.normalThreshold;
                    compData.normalFadeDistance = _settings.normalFadeDistance;

                    builder.UseTexture(compData.source, AccessFlags.Read);
                    builder.UseTexture(compData.depthSource, AccessFlags.Read);
                    builder.UseTexture(compData.normalSource, AccessFlags.Read);

                    builder.SetRenderAttachment(compositeOutput, 0, AccessFlags.Write);
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);

                    builder.SetRenderFunc((PassDataClasses.CompositePassData cData, RasterGraphContext context) =>
                    {
                        if (useFXAA) { context.cmd.ClearRenderTarget(false, true, Color.clear); }

                        cData.material.SetTexture(ShaderParamIDs._VoxelDepthTextureParams, cData.depthSource);
                        cData.material.SetTexture(ShaderParamIDs._VoxelNormalTextureParams, cData.normalSource);
                        cData.material.SetFloat(ShaderParamIDs._SharpnessParams, cData.sharpness);

                        if (cData.useFSR) cData.material.EnableKeyword("_UPSCALING_FSR");
                        else cData.material.DisableKeyword("_UPSCALING_FSR");

                        if (cData.enableOutline)
                        {
                            cData.material.EnableKeyword("_OUTLINE_ON");

                            cData.material.SetColor(ShaderParamIDs._MainLightColorParams, cData.mainLightColor);
                            cData.material.SetVector(ShaderParamIDs._MainLightDirectionID, cData.mainLightDirection);

                            cData.material.SetColor(ShaderParamIDs._OutlineColorParams, cData.outlineColor);
                            cData.material.SetVector(ShaderParamIDs._OutlineParamsID, new Vector4(cData.outlineThickness, cData.outlineStrength, 0, 0));
                            cData.material.SetFloat(ShaderParamIDs._OutlineShadowStrengthID, cData.outlineShadowStrength);

                            cData.material.SetColor(ShaderParamIDs._NormalOutlineColorParams, cData.normalColor);
                            cData.material.SetVector(ShaderParamIDs._NormalOutlineParamsID, new Vector4(cData.normalThreshold, cData.normalStrength, cData.normalFadeDistance, 0));
                        }
                        else
                        {
                            cData.material.DisableKeyword("_OUTLINE_ON");
                        }

                        Blitter.BlitTexture(context.cmd, cData.source, new Vector4(1, 1, 0, 0), cData.material, 0);
                    });
                }

                if (useFXAA)
                {
                    using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.FXAAPassData>("FXAA Pass", out var fxaaData))
                    {
                        fxaaData.source = compositeOutput;
                        fxaaData.material = _fxaaMaterial;
                        builder.UseTexture(fxaaData.source, AccessFlags.Read);
                        builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                        builder.SetRenderFunc((PassDataClasses.FXAAPassData fData, RasterGraphContext context) => { Blitter.BlitTexture(context.cmd, fData.source, new Vector4(1, 1, 0, 0), fData.material, 0); });
                    }
                }
            }

            private void SetupLights(UniversalLightData lightData, out Vector4 mainPos, out Vector4 mainCol)
            {
                mainPos = new Vector4(0, 1, 0, 0);
                mainCol = Color.white;
                int mainLightIndex = lightData.mainLightIndex;
                if (mainLightIndex != -1 && mainLightIndex < lightData.visibleLights.Length)
                {
                    VisibleLight mainLight = lightData.visibleLights[mainLightIndex];
                    if (mainLight.lightType == LightType.Directional)
                    {
                        Vector4 dir = -mainLight.localToWorldMatrix.GetColumn(2); dir.w = 0; mainPos = dir; mainCol = mainLight.finalColor;
                    }
                }
            }
        }
    }
}
