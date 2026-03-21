using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

using VoxelEngine.Core.Streaming;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelRaytraceFeature : ScriptableRendererFeature
    {
        public VoxelRaytraceSettings settings = new VoxelRaytraceSettings();

        public static Vector2 MousePosition;
        public static GraphicsBuffer RaycastHitBuffer;

        private VoxelRaytraceRenderPass _pass;
        private Material _edgeBlendMaterial;

        public override void Create()
        {
            _pass = new VoxelRaytraceRenderPass(settings);

            if (settings.edgeBlendShader != null)
                _edgeBlendMaterial = new Material(settings.edgeBlendShader);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (settings.raytraceShader == null) return;
            if (VoxelVolumePool.Instance == null) return;

            _pass.UpdateSettings(settings);
            _pass.SetEdgeBlendMaterial(_edgeBlendMaterial);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_edgeBlendMaterial);
            _pass?.Dispose();
        }

        private sealed class VoxelRaytraceRenderPass : ScriptableRenderPass
        {
            private VoxelRaytraceSettings _settings;
            private Material _edgeBlendMaterial;
            private readonly Dictionary<Camera, Matrix4x4> _prevViewProj = new Dictionary<Camera, Matrix4x4>();

            private readonly VoxelRaytracePass _raytracePass;
            private readonly EdgeBlendPass _edgeBlendPass = new EdgeBlendPass();

            public VoxelRaytraceRenderPass(VoxelRaytraceSettings settings)
            {
                _settings = settings;
                renderPassEvent = settings.injectionPoint;
                _raytracePass = new VoxelRaytracePass(settings);
            }

            public void UpdateSettings(VoxelRaytraceSettings newSettings)
            {
                _settings = newSettings;
                renderPassEvent = newSettings.injectionPoint;
                _raytracePass.UpdateSettings(newSettings);
            }

            public void SetEdgeBlendMaterial(Material mat) => _edgeBlendMaterial = mat;

            public void Dispose()
            {
                _raytracePass.Dispose();
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (VoxelVolumePool.Instance == null) return;
                var cameraData = frameData.Get<UniversalCameraData>();

                if (_settings.cullFrustum)
                {
                    Plane[] allPlanes = GeometryUtility.CalculateFrustumPlanes(cameraData.camera);
                    Plane[] cullingPlanes = _settings.useCameraFarPlane
                        ? allPlanes
                        : new Plane[] { allPlanes[0], allPlanes[1], allPlanes[2], allPlanes[3], allPlanes[4] };
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
                    case VoxelRaytraceSettings.QualityLevel.High:
                        currentScale = 1.0f;
                        break;
                    case VoxelRaytraceSettings.QualityLevel.Low:
                        currentScale = 0.5f;
                        iterations = 64;
                        marchSteps = 32;
                        break;
                    case VoxelRaytraceSettings.QualityLevel.Custom:
                        currentScale = _settings.renderScale;
                        iterations = _settings.iterations;
                        marchSteps = _settings.marchSteps;
                        break;
                }
                int scaledWidth = Mathf.Max(1, Mathf.RoundToInt(cameraDesc.width * currentScale));
                int scaledHeight = Mathf.Max(1, Mathf.RoundToInt(cameraDesc.height * currentScale));

                float jitterX = 0f;
                float jitterY = 0f;
                if (_settings.enableJitter)
                {
                    int frameIndex = Time.frameCount % 16;
                    jitterX = Halton(frameIndex + 1, 2) - 0.5f;
                    jitterY = Halton(frameIndex + 1, 3) - 0.5f;
                }

                SetupLights(lightData, out var mainPos, out var mainCol);

                var cam = cameraData.camera;
                Matrix4x4 view = cam.worldToCameraMatrix;
                Matrix4x4 proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
                Matrix4x4 viewProj = proj * view;

                if (!_prevViewProj.TryGetValue(cam, out Matrix4x4 prevViewProj))
                    prevViewProj = viewProj;
                _prevViewProj[cam] = viewProj;

                float fov = cam.fieldOfView;
                float rawPixelSpread = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 2.0f / cameraDesc.height;
                float finalSpread = rawPixelSpread * _settings.lodBias;

                var raytraceResult = _raytracePass.Record(renderGraph, cameraData, resourceData, mainPos, mainCol, viewProj, prevViewProj, scaledWidth, scaledHeight, currentScale, finalSpread, new Vector2(jitterX, jitterY), iterations, marchSteps);
                TextureHandle compositeSource = raytraceResult.LowResResult;

                bool useEdgeBlur = _settings.enableEdgeBlur && _edgeBlendMaterial != null;
                float edgeScale = 1.0f;
                if (useEdgeBlur)
                {
                    edgeScale = Mathf.Clamp(_settings.edgeRenderScale, 0.1f, 1.0f);
                    edgeScale = Mathf.Min(edgeScale, currentScale);
                    if (edgeScale < 1.0f)
                    {
                        int edgeScaledWidth = Mathf.Max(1, Mathf.RoundToInt(cameraDesc.width * edgeScale));
                        int edgeScaledHeight = Mathf.Max(1, Mathf.RoundToInt(cameraDesc.height * edgeScale));
                        var edgeResult = _raytracePass.Record(renderGraph, cameraData, resourceData, mainPos, mainCol, viewProj, prevViewProj, edgeScaledWidth, edgeScaledHeight, edgeScale, finalSpread, new Vector2(jitterX, jitterY), iterations, marchSteps);

                        TextureDesc edgeBlendDesc = new TextureDesc(scaledWidth, scaledHeight)
                        {
                            colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat,
                            name = "VoxelEdgeBlend"
                        };
                        TextureHandle edgeBlendTarget = renderGraph.CreateTexture(edgeBlendDesc);
                        compositeSource = _edgeBlendPass.Record(renderGraph, compositeSource, edgeResult.LowResResult, edgeBlendTarget, _edgeBlendMaterial, _settings.edgeWidthPercent);
                    }
                }

                var voxelData = frameData.GetOrCreate<VoxelFrameData>();
                voxelData.Color = compositeSource;
                voxelData.Depth = raytraceResult.LowResDepth;
                voxelData.Normals = raytraceResult.LowResNormals;
                voxelData.MotionVectors = raytraceResult.MotionVectors;
                voxelData.ScaledWidth = scaledWidth;
                voxelData.ScaledHeight = scaledHeight;
                voxelData.RenderScale = currentScale;
                voxelData.Jitter = new Vector2(jitterX, jitterY);
                voxelData.ViewProj = viewProj;
                voxelData.PrevViewProj = prevViewProj;
                voxelData.MainLightPosition = mainPos;
                voxelData.MainLightColor = mainCol;
                voxelData.PixelSpread = finalSpread;
            }

            private static float Halton(int index, int radix)
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

            private static void SetupLights(UniversalLightData lightData, out Vector4 mainPos, out Vector4 mainCol)
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
