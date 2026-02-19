using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

using VoxelEngine.Core.Streaming;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelRaytracerFeature : ScriptableRendererFeature
    {
        public VoxelRaytracerSettings settings = new VoxelRaytracerSettings();
        private VoxelRaytracerOrchestratorPass _pass;
        private Material _compositeMaterial;
        private Material _fxaaMaterial;
        private Material _taaMaterial;
        private Material _godRayMaterial;
        private Material _copyMaterial;
        private Material _edgeBlendMaterial;

        public static Vector2 MousePosition;
        public static GraphicsBuffer RaycastHitBuffer;

        public override void Create()
        {
            _pass = new VoxelRaytracerOrchestratorPass(settings);

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

            _copyMaterial = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/Universal Render Pipeline/Blit"));

            if (settings.edgeBlendShader != null)
                _edgeBlendMaterial = new Material(settings.edgeBlendShader);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (settings.raytraceShader == null) return;
            if (VoxelVolumePool.Instance == null) return;

            _pass.UpdateSettings(settings);
            _pass.Setup(_compositeMaterial, _fxaaMaterial, _taaMaterial, _godRayMaterial, _copyMaterial, _edgeBlendMaterial);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_compositeMaterial);
            CoreUtils.Destroy(_fxaaMaterial);
            CoreUtils.Destroy(_taaMaterial);
            CoreUtils.Destroy(_godRayMaterial);
            CoreUtils.Destroy(_copyMaterial);
            CoreUtils.Destroy(_edgeBlendMaterial);
            _pass?.Dispose();
        }

        private sealed class VoxelRaytracerOrchestratorPass : ScriptableRenderPass
        {
            private VoxelRaytracerSettings _settings;
            private Material _compositeMaterial;
            private Material _fxaaMaterial;
            private Material _taaMaterial;
            private Material _godRayMaterial;
            private Material _copyMaterial;
            private Material _edgeBlendMaterial;

            private readonly CameraHistoryManager _cameraHistoryManager = new CameraHistoryManager();
            private readonly VoxelRaytracePass _raytracePass;
            private readonly VoxelVegetationPass _vegetationPass = new VoxelVegetationPass();
            private readonly GodRaysPass _godRaysPass = new GodRaysPass();
            private readonly TAAPass _taaPass = new TAAPass();
            private readonly CompositePass _compositePass = new CompositePass();
            private readonly FXAAPass _fxaaPass = new FXAAPass();
            private readonly EdgeBlendPass _edgeBlendPass = new EdgeBlendPass();

            public VoxelRaytracerOrchestratorPass(VoxelRaytracerSettings settings)
            {
                _settings = settings;
                renderPassEvent = settings.injectionPoint;
                _raytracePass = new VoxelRaytracePass(settings);
            }

            public void UpdateSettings(VoxelRaytracerSettings newSettings)
            {
                _settings = newSettings;
                renderPassEvent = newSettings.injectionPoint;
                _raytracePass.UpdateSettings(newSettings);
            }

            public void Setup(Material composite, Material fxaa, Material taa, Material godrays, Material copyMaterial, Material edgeBlendMaterial)
            {
                _compositeMaterial = composite;
                _fxaaMaterial = fxaa;
                _taaMaterial = taa;
                _godRayMaterial = godrays;
                _copyMaterial = copyMaterial;
                _edgeBlendMaterial = edgeBlendMaterial;
            }

            public void Dispose()
            {
                _raytracePass.Dispose();
                _cameraHistoryManager.Release();
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

                SetupLights(lightData, out var mainPos, out var mainCol);

                float fov = cameraData.camera.fieldOfView;
                float rawPixelSpread = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 2.0f / cameraDesc.height;
                float finalSpread = rawPixelSpread * _settings.lodBias;

                var raytraceResult = _raytracePass.Record(renderGraph, cameraData, resourceData, mainPos, mainCol, viewProj, prevViewProj, scaledWidth, scaledHeight, currentScale, finalSpread, new Vector2(jitterX, jitterY), iterations, marchSteps);
                TextureHandle compositeSource = raytraceResult.LowResResult;

                VoxelRaytracePass.RaytraceOutput edgeRaytraceResult = default;
                bool useEdgeBlur = _settings.enableEdgeBlur && _edgeBlendMaterial != null;
                float edgeScale = 1.0f;
                if (useEdgeBlur)
                {
                    edgeScale = Mathf.Clamp(_settings.edgeRenderScale, 0.1f, 1.0f);
                    if (edgeScale < 1.0f)
                    {
                        int edgeScaledWidth = Mathf.Max(1, Mathf.RoundToInt(cameraDesc.width * edgeScale));
                        int edgeScaledHeight = Mathf.Max(1, Mathf.RoundToInt(cameraDesc.height * edgeScale));
                        edgeRaytraceResult = _raytracePass.Record(renderGraph, cameraData, resourceData, mainPos, mainCol, viewProj, prevViewProj, edgeScaledWidth, edgeScaledHeight, edgeScale, finalSpread, new Vector2(jitterX, jitterY), iterations, marchSteps);
                    }
                }

                _vegetationPass.Record(renderGraph, scaledWidth, scaledHeight, raytraceResult.LowResResult, raytraceResult.LowResDepth, raytraceResult.LowResNormals, _copyMaterial);
                _godRaysPass.Record(renderGraph, cameraData, _settings, _godRayMaterial, raytraceResult.LowResDepth, raytraceResult.LowResResult, mainPos, scaledWidth, scaledHeight);

                if (useEdgeBlur && edgeScale < 1.0f)
                {
                    TextureDesc edgeBlendDesc = new TextureDesc(scaledWidth, scaledHeight)
                    {
                        colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat,
                        name = "VoxelEdgeBlend"
                    };
                    TextureHandle edgeBlendTarget = renderGraph.CreateTexture(edgeBlendDesc);
                    compositeSource = _edgeBlendPass.Record(renderGraph, compositeSource, edgeRaytraceResult.LowResResult, edgeBlendTarget, _edgeBlendMaterial, _settings.edgeWidthPercent);
                }

                compositeSource = _taaPass.Record(renderGraph, useTAA, _taaMaterial, compositeSource, historyRead, historyWrite, raytraceResult.MotionVectors, _settings.taaBlend);
                _compositePass.Record(renderGraph, _settings, _compositeMaterial, compositeSource, raytraceResult.LowResDepth, raytraceResult.LowResNormals, compositeOutput, resourceData.activeDepthTexture, useFXAA, mainPos, mainCol);
                _fxaaPass.Record(renderGraph, useFXAA, _fxaaMaterial, compositeOutput, resourceData.activeColorTexture);
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
}
