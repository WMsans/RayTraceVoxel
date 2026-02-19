using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

using VoxelEngine.Core.Data;
using VoxelEngine.Core.Streaming;

namespace VoxelEngine.Core.Rendering
{
    internal sealed class VoxelRaytracePass : ScriptableRenderPass
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

        private readonly CameraHistoryManager _cameraHistoryManager = new CameraHistoryManager();
        private readonly VoxelVegetationPass _vegetationPass = new VoxelVegetationPass();
        private readonly GodRaysPass _godRaysPass = new GodRaysPass();
        private readonly TAAPass _taaPass = new TAAPass();
        private readonly CompositePass _compositePass = new CompositePass();
        private readonly FXAAPass _fxaaPass = new FXAAPass();

        public VoxelRaytracePass(VoxelRaytracerSettings settings)
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

            _cameraHistoryManager.Release();

            if (VoxelRaytracerFeature.RaycastHitBuffer != null)
            {
                VoxelRaytracerFeature.RaycastHitBuffer.Release();
                VoxelRaytracerFeature.RaycastHitBuffer = null;
            }
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
                case VoxelRaytracerSettings.QualityLevel.High:
                    currentScale = 1.0f;
                    break;
                case VoxelRaytracerSettings.QualityLevel.Low:
                    currentScale = 0.5f;
                    iterations = 64;
                    marchSteps = 32;
                    break;
                case VoxelRaytracerSettings.QualityLevel.Custom:
                    currentScale = _settings.renderScale;
                    iterations = _settings.iterations;
                    marchSteps = _settings.marchSteps;
                    break;
            }
            int scaledWidth = Mathf.Max(1, Mathf.RoundToInt(cameraDesc.width * currentScale));
            int scaledHeight = Mathf.Max(1, Mathf.RoundToInt(cameraDesc.height * currentScale));

            TextureDesc colorDesc = new TextureDesc(scaledWidth, scaledHeight)
            {
                colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat,
                enableRandomWrite = true,
                name = "VoxelRaytraceResult_LowRes"
            };
            TextureHandle lowResResult = renderGraph.CreateTexture(colorDesc);
            TextureDesc depthDesc = new TextureDesc(scaledWidth, scaledHeight)
            {
                colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat,
                enableRandomWrite = true,
                name = "VoxelRaytraceDepth_LowRes"
            };
            TextureHandle lowResDepth = renderGraph.CreateTexture(depthDesc);
            TextureDesc normDesc = new TextureDesc(scaledWidth, scaledHeight)
            {
                colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat,
                enableRandomWrite = true,
                name = "VoxelRaytraceNormals"
            };
            TextureHandle lowResNormals = renderGraph.CreateTexture(normDesc);
            TextureDesc mvDesc = new TextureDesc(scaledWidth, scaledHeight)
            {
                colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16_SFloat,
                enableRandomWrite = true,
                name = "VoxelMotionVectors"
            };
            TextureHandle motionVectorTex = renderGraph.CreateTexture(mvDesc);

            int frameIndex = Time.frameCount % 16;
            float jitterX = (Halton(frameIndex + 1, 2) - 0.5f);
            float jitterY = (Halton(frameIndex + 1, 3) - 0.5f);
            bool useTAA = _settings.enableTAA && _taaMaterial != null;
            if (!useTAA)
            {
                jitterX = 0;
                jitterY = 0;
            }

            var cam = cameraData.camera;
            Matrix4x4 view = cam.worldToCameraMatrix;
            Matrix4x4 proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 viewProj = proj * view;
            _cameraHistoryManager.TryGetPrevViewProj(cam, viewProj, out Matrix4x4 prevViewProj);

            TextureHandle historyRead = TextureHandle.nullHandle;
            TextureHandle historyWrite = TextureHandle.nullHandle;
            if (useTAA)
            {
                _cameraHistoryManager.GetHistoryTextures(cam, renderGraph, scaledWidth, scaledHeight, out historyRead, out historyWrite);
            }

            TextureHandle compositeOutput;
            bool useFXAA = _settings.enableFXAA && _fxaaMaterial != null;
            if (useFXAA)
            {
                TextureDesc fullScreenDesc = new TextureDesc(cameraDesc.width, cameraDesc.height)
                {
                    colorFormat = cameraDesc.graphicsFormat,
                    name = "VoxelComposite_PreFXAA"
                };
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
                data.computeShader = _shader;
                data.kernel = _shader.FindKernel("CSMain");
                if (VoxelRaytracerFeature.RaycastHitBuffer == null || !VoxelRaytracerFeature.RaycastHitBuffer.IsValid())
                    VoxelRaytracerFeature.RaycastHitBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, 16);
                data.raycastBuffer = VoxelRaytracerFeature.RaycastHitBuffer;
                var pool = VoxelVolumePool.Instance;
                data.nodeBuffer = pool.GlobalNodeBuffer;
                data.payloadBuffer = pool.GlobalPayloadBuffer;
                data.brickDataBuffer = pool.GlobalBrickDataBuffer;
                data.pageTableBuffer = pool.GlobalPageTableBuffer;
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
                data.width = scaledWidth;
                data.height = scaledHeight;
                data.cameraToWorld = cameraData.camera.cameraToWorldMatrix;
                data.cameraInverseProjection = cameraData.camera.projectionMatrix.inverse;
                data.viewProj = viewProj;
                data.prevViewProj = prevViewProj;
                data.zBufferParams = Shader.GetGlobalVector(ShaderParamIDs._ZBufferParamsID);
                data.sourceDepth = resourceData.cameraDepthTexture;
                data.sourceColor = resourceData.activeColorTexture;
                data.targetColor = lowResResult;
                data.targetDepth = lowResDepth;
                data.targetNormals = lowResNormals;
                data.targetMotionVector = motionVectorTex;
                data.mainLightPosition = mainPos;
                data.mainLightColor = mainCol;
                data.raytraceParams = new Vector4(finalSpread, jitterX, jitterY, _settings.textureScale);
                data.mousePosition = VoxelRaytracerFeature.MousePosition * currentScale;
                data.maxIterations = iterations;
                data.maxMarchSteps = marchSteps;
                data.debugNormals = (_settings.debugMode == VoxelRaytracerSettings.DebugMode.Normals) ? 1.0f : 0.0f;
                data.debugBricks = (_settings.debugMode == VoxelRaytracerSettings.DebugMode.Bricks) ? 1.0f : 0.0f;
                data.celShadeParams = new Vector4((float)_settings.celSteps, _settings.shadowBrightness, 0, 0);
                data.atmosphereParams = new Vector4(_settings.enableAtmosphere ? _settings.atmosphereDensity : 0.0f, 0, 0, 0);
                data.atmosphereColor = _settings.atmosphereColor;

                builder.UseTexture(data.targetColor, AccessFlags.Write);
                builder.UseTexture(data.targetDepth, AccessFlags.Write);
                builder.UseTexture(data.targetNormals, AccessFlags.Write);
                builder.UseTexture(data.targetMotionVector, AccessFlags.Write);
                builder.UseTexture(data.sourceDepth, AccessFlags.Read);
                builder.UseTexture(data.sourceColor, AccessFlags.Read);
                if (data.albedoArray.IsValid()) builder.UseTexture(data.albedoArray, AccessFlags.Read);
                if (data.normalArray.IsValid()) builder.UseTexture(data.normalArray, AccessFlags.Read);
                if (data.maskArray.IsValid()) builder.UseTexture(data.maskArray, AccessFlags.Read);
                if (data.blueNoise.IsValid()) builder.UseTexture(data.blueNoise, AccessFlags.Read);

                builder.SetRenderFunc((PassDataClasses.PassData pd, ComputeGraphContext ctx) =>
                {
                    var cs = pd.computeShader;
                    var ker = pd.kernel;
                    var cmd = ctx.cmd;
                    cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._GlobalNodeBufferParams, pd.nodeBuffer);
                    cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._GlobalPayloadBufferParams, pd.payloadBuffer);
                    cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._GlobalBrickDataBufferParams, pd.brickDataBuffer);
                    cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._PageTableBufferParams, pd.pageTableBuffer);
                    cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._ChunkBufferParams, pd.chunkBuffer);
                    cmd.SetComputeIntParam(cs, ShaderParamIDs._ChunkCountParams, pd.chunkCount);
                    if (pd.tlasGridBuffer != null) cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._TLASGridBufferParams, pd.tlasGridBuffer);
                    if (pd.tlasChunkIndexBuffer != null) cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._TLASChunkIndexBufferParams, pd.tlasChunkIndexBuffer);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._TLASBoundsMinParams, pd.tlasBoundsMin);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._TLASBoundsMaxParams, pd.tlasBoundsMax);
                    cmd.SetComputeIntParam(cs, ShaderParamIDs._TLASResolutionParams, pd.tlasResolution);
                    cmd.SetComputeIntParam(cs, ShaderParamIDs._FrameCountParams, pd.frameCount);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._MousePositionParams, pd.mousePosition);
                    cmd.SetComputeIntParam(cs, ShaderParamIDs._MaxIterationsParams, pd.maxIterations);
                    cmd.SetComputeIntParam(cs, ShaderParamIDs._MaxMarchStepsParams, pd.maxMarchSteps);
                    if (pd.blueNoise.IsValid()) cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._BlueNoiseTextureParams, pd.blueNoise);
                    if (pd.materialBuffer != null) cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._VoxelMaterialBufferParams, pd.materialBuffer);
                    if (pd.albedoArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._AlbedoTextureArrayParams, pd.albedoArray);
                    if (pd.normalArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._NormalTextureArrayParams, pd.normalArray);
                    if (pd.maskArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._MaskTextureArrayParams, pd.maskArray);
                    cmd.SetComputeMatrixParam(cs, ShaderParamIDs._CameraToWorldParams, pd.cameraToWorld);
                    cmd.SetComputeMatrixParam(cs, ShaderParamIDs._CameraInverseProjectionParams, pd.cameraInverseProjection);
                    cmd.SetComputeMatrixParam(cs, ShaderParamIDs._CameraViewProjectionParams, pd.viewProj);
                    cmd.SetComputeMatrixParam(cs, ShaderParamIDs._PrevViewProjMatrixParams, pd.prevViewProj);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._ZBufferParamsID, pd.zBufferParams);
                    cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._CameraDepthTextureParams, pd.sourceDepth);
                    cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._SourceTexParams, pd.sourceColor);
                    cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._ResultParams, pd.targetColor);
                    cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._ResultDepthParams, pd.targetDepth);
                    cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._ResultNormalsParams, pd.targetNormals);
                    cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._MotionVectorTextureParams, pd.targetMotionVector);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._MainLightPositionParams, pd.mainLightPosition);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._MainLightColorParams, pd.mainLightColor);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._RaytraceParams, pd.raytraceParams);
                    cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._RaycastBufferParams, pd.raycastBuffer);
                    cmd.SetComputeFloatParam(cs, ShaderParamIDs._DebugViewNormalsParams, pd.debugNormals);
                    cmd.SetComputeFloatParam(cs, ShaderParamIDs._DebugViewBricksParams, pd.debugBricks);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._CelShadeParams, pd.celShadeParams);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._AtmosphereParams, pd.atmosphereParams);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._AtmosphereColor, pd.atmosphereColor);

                    int groupsX = Mathf.CeilToInt(pd.width / 8.0f);
                    int groupsY = Mathf.CeilToInt(pd.height / 8.0f);
                    cmd.DispatchCompute(cs, ker, groupsX, groupsY, 1);
                });
            }

            TextureHandle compositeSource = lowResResult;

            _vegetationPass.Record(renderGraph, scaledWidth, scaledHeight, lowResResult, lowResDepth, lowResNormals, _copyMaterial);
            _godRaysPass.Record(renderGraph, cameraData, _settings, _godRayMaterial, lowResDepth, lowResResult, mainPos, scaledWidth, scaledHeight);
            compositeSource = _taaPass.Record(renderGraph, useTAA, _taaMaterial, compositeSource, historyRead, historyWrite, motionVectorTex, _settings.taaBlend);
            _compositePass.Record(renderGraph, _settings, _compositeMaterial, compositeSource, lowResDepth, lowResNormals, compositeOutput, resourceData.activeDepthTexture, useFXAA, mainPos, mainCol);
            _fxaaPass.Record(renderGraph, useFXAA, _fxaaMaterial, compositeOutput, resourceData.activeColorTexture);
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
                    Vector4 dir = -mainLight.localToWorldMatrix.GetColumn(2);
                    dir.w = 0;
                    mainPos = dir;
                    mainCol = mainLight.finalColor;
                }
            }
        }
    }
}
