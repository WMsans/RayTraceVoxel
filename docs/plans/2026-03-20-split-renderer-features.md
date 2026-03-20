# Split VoxelRaytracerFeature Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the monolithic `VoxelRaytracerFeature` with 5 independent `ScriptableRendererFeature`s that share data via `ContextContainer`.

**Architecture:** Each feature owns its own settings class, creates its own materials, and enqueues its own `ScriptableRenderPass`. The raytrace feature writes a `VoxelFrameData` context item that downstream features read. Existing pass classes are modified to accept their specific settings types instead of the monolithic `VoxelRaytracerSettings`.

**Tech Stack:** Unity 6, URP (RenderGraph API), C#

**Design doc:** `docs/plans/2026-03-20-split-renderer-features-design.md`

---

### Task 1: Create VoxelFrameData context item

**Files:**
- Create: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/VoxelFrameData.cs`

**Step 1: Create the VoxelFrameData class**

```csharp
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelFrameData : ContextItem
    {
        public TextureHandle Color;
        public TextureHandle Depth;
        public TextureHandle Normals;
        public TextureHandle MotionVectors;
        public int ScaledWidth;
        public int ScaledHeight;
        public float RenderScale;
        public Vector2 Jitter;
        public Matrix4x4 ViewProj;
        public Matrix4x4 PrevViewProj;
        public Vector4 MainLightPosition;
        public Vector4 MainLightColor;
        public float PixelSpread;

        public override void Reset()
        {
            Color = Depth = Normals = MotionVectors = TextureHandle.nullHandle;
            ScaledWidth = ScaledHeight = 0;
            RenderScale = 1f;
            Jitter = Vector2.zero;
            ViewProj = PrevViewProj = Matrix4x4.identity;
            MainLightPosition = new Vector4(0, 1, 0, 0);
            MainLightColor = Vector4.one;
            PixelSpread = 0f;
        }
    }
}
```

**Step 2: Commit**

```bash
git add Assets/Scripts/VoxelEngine/Rendering/Raytracing/VoxelFrameData.cs
git commit -m "feat: add VoxelFrameData ContextItem for cross-feature data sharing"
```

---

### Task 2: Create per-feature settings classes

**Files:**
- Create: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/Settings/VoxelRaytraceSettings.cs`
- Create: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/Settings/VoxelGodRaysSettings.cs`
- Create: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/Settings/VoxelTAASettings.cs`
- Create: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/Settings/VoxelCompositeSettings.cs`

**Step 1: Create VoxelRaytraceSettings**

Extract raytrace-specific fields from the old `VoxelRaytracerSettings`. Keep the same enum types (`QualityLevel`, `DebugMode`) here since they're raytrace-specific.

```csharp
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    [System.Serializable]
    public class VoxelRaytraceSettings
    {
        public enum QualityLevel { High, Low, Custom }
        public enum DebugMode { None, Normals, Bricks }

        public ComputeShader raytraceShader;
        public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingSkybox;

        [Header("Quality")]
        public QualityLevel qualityLevel = QualityLevel.High;
        [Range(0, 8)] public int bounceCount = 3;
        [Range(0.1f, 1.0f)] public float renderScale = 1.0f;
        [Range(0.01f, 10.0f)] public float textureScale = 1.0f;
        public int iterations = 128;
        public int marchSteps = 64;

        [Header("Atmosphere")]
        public bool enableAtmosphere = true;
        public Color atmosphereColor = new Color(0.55f, 0.7f, 0.9f);
        [Range(0.0f, 0.1f)] public float atmosphereDensity = 0.005f;

        [Header("Cel Shading")]
        [Range(1, 10)] public int celSteps = 3;
        [Range(0.0f, 1.0f)] public float shadowBrightness = 0.2f;

        [Header("Edge Blur")]
        public bool enableEdgeBlur = true;
        [Range(0.01f, 0.5f)] public float edgeWidthPercent = 0.1f;
        [Range(0.1f, 1.0f)] public float edgeRenderScale = 0.5f;
        public Shader edgeBlendShader;

        [Header("LOD Settings")]
        [Range(1.0f, 200.0f)] public float lodBias = 1.0f;

        [Header("Culling")]
        public bool useCameraFarPlane = false;
        public bool cullFrustum = true;
        public float shadowDistance = 1500.0f;

        [Header("Dithering")]
        public Texture2D blueNoiseTexture;

        [Header("Debug")]
        public DebugMode debugMode = DebugMode.None;
    }
}
```

**Step 2: Create VoxelGodRaysSettings**

```csharp
using UnityEngine;

namespace VoxelEngine.Core.Rendering
{
    [System.Serializable]
    public class VoxelGodRaysSettings
    {
        public Shader godRayShader;

        [Tooltip("Threshold when the sun is directly overhead (Noon). Controls the size of the sun disk source.")]
        [Range(0.0f, 1.0f)] public float noonSunThreshold = 0.95f;

        [Tooltip("Threshold when the sun is at the horizon (Dawn/Dusk).")]
        [Range(0.0f, 1.0f)] public float dawnSunThreshold = 0.99f;

        [Range(0.0f, 5.0f)] public float rayDensity = 1.0f;
        [Range(0.0f, 1.0f)] public float rayDecay = 0.95f;
        [Range(0.0f, 1.0f)] public float rayWeight = 0.1f;
        [Range(0.0f, 5.0f)] public float rayExposure = 1.0f;
        [Range(16, 128)] public int raySamples = 32;
        public Color lightSourceColor = new Color(1.0f, 0.95f, 0.8f);
    }
}
```

**Step 3: Create VoxelTAASettings**

```csharp
using UnityEngine;

namespace VoxelEngine.Core.Rendering
{
    [System.Serializable]
    public class VoxelTAASettings
    {
        public Shader taaShader;
        [Range(0.0f, 1.0f)] public float taaBlend = 0.93f;
    }
}
```

**Step 4: Create VoxelCompositeSettings**

```csharp
using UnityEngine;

namespace VoxelEngine.Core.Rendering
{
    [System.Serializable]
    public class VoxelCompositeSettings
    {
        public enum UpscalingMode { Bilinear, SpatialFSR }

        public Shader compositeShader;
        public Shader fxaaShader;

        [Header("Upscaling")]
        public UpscalingMode upscalingMode = UpscalingMode.SpatialFSR;
        [Range(0.0f, 1.0f)] public float sharpness = 0.5f;

        [Header("Anti-Aliasing")]
        public bool enableFXAA = true;

        [Header("Outline")]
        public bool enableOutline = false;
        [Range(0.0f, 5.0f)] public float outlineThickness = 1.0f;
        [Range(0.0f, 1.0f)] public float outlineShadowStrength = 0.5f;
        [Range(0.0f, 1.0f)] public float outlineStrength = 0.5f;
        public Color outlineColor = Color.black;

        [Header("Normal Highlight")]
        [Range(0.0f, 1.0f)] public float normalHighlightStrength = 0.5f;
        [Range(0.0f, 2.0f)] public float normalThreshold = 0.6f;
        [Range(0.0f, 500.0f)] public float normalFadeDistance = 50.0f;
        public Color normalHighlightColor = Color.white;
    }
}
```

**Step 5: Commit**

```bash
git add Assets/Scripts/VoxelEngine/Rendering/Raytracing/Settings/
git commit -m "feat: add per-feature settings classes (raytrace, god rays, TAA, composite)"
```

---

### Task 3: Create the 5 feature classes and their directories

**Files:**
- Create: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/Features/VoxelRaytraceFeature.cs`
- Create: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/Features/VoxelVegetationFeature.cs`
- Create: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/Features/VoxelGodRaysFeature.cs`
- Create: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/Features/VoxelTAAFeature.cs`
- Create: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/Features/VoxelCompositeFeature.cs`

**Step 1: Create VoxelRaytraceFeature**

This is the largest feature. It absorbs the orchestrator's frustum culling, quality scaling, jitter, light setup, and pixel spread computation. It owns `VoxelRaytracePass` and `EdgeBlendPass`, creates the edge blend material, and writes `VoxelFrameData` to the context container.

The static `MousePosition` and `RaycastHitBuffer` fields stay here (referenced by `TerrainEditorTool`, `DynamicTerrainEditorTool`, and `TestGlobalDispatch`).

```csharp
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

                int frameIndex = Time.frameCount % 16;
                float jitterX = Halton(frameIndex + 1, 2) - 0.5f;
                float jitterY = Halton(frameIndex + 1, 3) - 0.5f;

                SetupLights(lightData, out var mainPos, out var mainCol);

                var cam = cameraData.camera;
                Matrix4x4 view = cam.worldToCameraMatrix;
                Matrix4x4 proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
                Matrix4x4 viewProj = proj * view;

                float fov = cam.fieldOfView;
                float rawPixelSpread = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 2.0f / cameraDesc.height;
                float finalSpread = rawPixelSpread * _settings.lodBias;

                var raytraceResult = _raytracePass.Record(renderGraph, cameraData, resourceData, mainPos, mainCol, viewProj, Matrix4x4.identity, scaledWidth, scaledHeight, currentScale, finalSpread, new Vector2(jitterX, jitterY), iterations, marchSteps);
                TextureHandle compositeSource = raytraceResult.LowResResult;

                // Edge blend (optional secondary raytrace at lower resolution)
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
                        var edgeResult = _raytracePass.Record(renderGraph, cameraData, resourceData, mainPos, mainCol, viewProj, Matrix4x4.identity, edgeScaledWidth, edgeScaledHeight, edgeScale, finalSpread, new Vector2(jitterX, jitterY), iterations, marchSteps);

                        TextureDesc edgeBlendDesc = new TextureDesc(scaledWidth, scaledHeight)
                        {
                            colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat,
                            name = "VoxelEdgeBlend"
                        };
                        TextureHandle edgeBlendTarget = renderGraph.CreateTexture(edgeBlendDesc);
                        compositeSource = _edgeBlendPass.Record(renderGraph, compositeSource, edgeResult.LowResResult, edgeBlendTarget, _edgeBlendMaterial, _settings.edgeWidthPercent);
                    }
                }

                // Write VoxelFrameData for downstream features
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
                voxelData.PrevViewProj = Matrix4x4.identity; // Will be set by TAA feature
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
```

**Important note about prevViewProj:** The old orchestrator used `CameraHistoryManager.TryGetPrevViewProj` to get the previous frame's view-projection matrix. This is needed for motion vectors in the raytrace pass AND for TAA. Since `CameraHistoryManager` is moving to the TAA feature, the raytrace feature passes `Matrix4x4.identity` as `prevViewProj` for now. The TAA feature will manage history and update `VoxelFrameData.PrevViewProj`.

**However**, the raytrace compute shader needs `prevViewProj` to compute motion vectors. So we need to keep a lightweight prev-viewproj tracker in the raytrace feature too. Let me revise — add a simple `Dictionary<Camera, Matrix4x4>` directly in the pass:

Replace `Matrix4x4.identity` references above. Add these fields to `VoxelRaytraceRenderPass`:

```csharp
private readonly Dictionary<Camera, Matrix4x4> _prevViewProj = new Dictionary<Camera, Matrix4x4>();
```

And in `RecordRenderGraph`, after computing `viewProj`:

```csharp
if (!_prevViewProj.TryGetValue(cam, out Matrix4x4 prevViewProj))
    prevViewProj = viewProj;
_prevViewProj[cam] = viewProj;
```

Then pass `prevViewProj` instead of `Matrix4x4.identity` to `_raytracePass.Record(...)` and store it in `voxelData.PrevViewProj`.

**Step 2: Create VoxelVegetationFeature**

```csharp
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelVegetationFeature : ScriptableRendererFeature
    {
        private VoxelVegetationRenderPass _pass;
        private Material _copyMaterial;

        public override void Create()
        {
            _pass = new VoxelVegetationRenderPass();
            _copyMaterial = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/Universal Render Pipeline/Blit"));
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            _pass.SetCopyMaterial(_copyMaterial);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_copyMaterial);
        }

        private sealed class VoxelVegetationRenderPass : ScriptableRenderPass
        {
            private readonly VoxelVegetationPass _vegetationPass = new VoxelVegetationPass();
            private Material _copyMaterial;

            public VoxelVegetationRenderPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
            }

            public void SetCopyMaterial(Material mat) => _copyMaterial = mat;

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!frameData.Contains<VoxelFrameData>()) return;
                var voxelData = frameData.Get<VoxelFrameData>();

                _vegetationPass.Record(renderGraph, voxelData.ScaledWidth, voxelData.ScaledHeight, voxelData.Color, voxelData.Depth, voxelData.Normals, _copyMaterial);
            }
        }
    }
}
```

**Step 3: Create VoxelGodRaysFeature**

```csharp
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelGodRaysFeature : ScriptableRendererFeature
    {
        public VoxelGodRaysSettings settings = new VoxelGodRaysSettings();

        private VoxelGodRaysRenderPass _pass;
        private Material _godRayMaterial;

        public override void Create()
        {
            _pass = new VoxelGodRaysRenderPass();

            if (settings.godRayShader != null)
                _godRayMaterial = new Material(settings.godRayShader);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_godRayMaterial == null) return;

            _pass.Setup(settings, _godRayMaterial);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_godRayMaterial);
        }

        private sealed class VoxelGodRaysRenderPass : ScriptableRenderPass
        {
            private VoxelGodRaysSettings _settings;
            private Material _godRayMaterial;
            private readonly GodRaysPass _godRaysPass = new GodRaysPass();

            public VoxelGodRaysRenderPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
            }

            public void Setup(VoxelGodRaysSettings settings, Material material)
            {
                _settings = settings;
                _godRayMaterial = material;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!frameData.Contains<VoxelFrameData>()) return;
                var voxelData = frameData.Get<VoxelFrameData>();
                var cameraData = frameData.Get<UniversalCameraData>();

                _godRaysPass.Record(renderGraph, cameraData, _settings, _godRayMaterial, voxelData.Depth, voxelData.Color, voxelData.MainLightPosition, voxelData.ScaledWidth, voxelData.ScaledHeight);
            }
        }
    }
}
```

**Step 4: Create VoxelTAAFeature**

```csharp
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelTAAFeature : ScriptableRendererFeature
    {
        public VoxelTAASettings settings = new VoxelTAASettings();

        private VoxelTAARenderPass _pass;
        private Material _taaMaterial;

        public override void Create()
        {
            _pass = new VoxelTAARenderPass();

            if (settings.taaShader != null)
                _taaMaterial = new Material(settings.taaShader);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_taaMaterial == null) return;

            _pass.Setup(settings, _taaMaterial);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_taaMaterial);
            _pass?.Dispose();
        }

        private sealed class VoxelTAARenderPass : ScriptableRenderPass
        {
            private VoxelTAASettings _settings;
            private Material _taaMaterial;
            private readonly TAAPass _taaPass = new TAAPass();
            private readonly CameraHistoryManager _cameraHistoryManager = new CameraHistoryManager();

            public VoxelTAARenderPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
            }

            public void Setup(VoxelTAASettings settings, Material material)
            {
                _settings = settings;
                _taaMaterial = material;
            }

            public void Dispose()
            {
                _cameraHistoryManager.Release();
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!frameData.Contains<VoxelFrameData>()) return;
                var voxelData = frameData.Get<VoxelFrameData>();

                _cameraHistoryManager.GetHistoryTextures(
                    frameData.Get<UniversalCameraData>().camera,
                    renderGraph,
                    voxelData.ScaledWidth,
                    voxelData.ScaledHeight,
                    out TextureHandle historyRead,
                    out TextureHandle historyWrite);

                voxelData.Color = _taaPass.Record(renderGraph, true, _taaMaterial, voxelData.Color, historyRead, historyWrite, voxelData.MotionVectors, _settings.taaBlend);
            }
        }
    }
}
```

**Step 5: Create VoxelCompositeFeature**

```csharp
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelCompositeFeature : ScriptableRendererFeature
    {
        public VoxelCompositeSettings settings = new VoxelCompositeSettings();

        private VoxelCompositeRenderPass _pass;
        private Material _compositeMaterial;
        private Material _fxaaMaterial;

        public override void Create()
        {
            _pass = new VoxelCompositeRenderPass();

            if (settings.compositeShader != null)
                _compositeMaterial = new Material(settings.compositeShader);
            else
                _compositeMaterial = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/Universal Render Pipeline/Blit"));

            if (settings.fxaaShader != null)
                _fxaaMaterial = new Material(settings.fxaaShader);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            _pass.Setup(settings, _compositeMaterial, _fxaaMaterial);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_compositeMaterial);
            CoreUtils.Destroy(_fxaaMaterial);
        }

        private sealed class VoxelCompositeRenderPass : ScriptableRenderPass
        {
            private VoxelCompositeSettings _settings;
            private Material _compositeMaterial;
            private Material _fxaaMaterial;
            private readonly CompositePass _compositePass = new CompositePass();
            private readonly FXAAPass _fxaaPass = new FXAAPass();

            public VoxelCompositeRenderPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
            }

            public void Setup(VoxelCompositeSettings settings, Material composite, Material fxaa)
            {
                _settings = settings;
                _compositeMaterial = composite;
                _fxaaMaterial = fxaa;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!frameData.Contains<VoxelFrameData>()) return;
                var voxelData = frameData.Get<VoxelFrameData>();
                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var cameraDesc = cameraData.cameraTargetDescriptor;

                bool useFXAA = _settings.enableFXAA && _fxaaMaterial != null;

                TextureHandle compositeOutput;
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

                _compositePass.Record(renderGraph, _settings, _compositeMaterial, voxelData.Color, voxelData.Depth, voxelData.Normals, compositeOutput, resourceData.activeDepthTexture, useFXAA, voxelData.MainLightPosition, voxelData.MainLightColor);
                _fxaaPass.Record(renderGraph, useFXAA, _fxaaMaterial, compositeOutput, resourceData.activeColorTexture);
            }
        }
    }
}
```

**Step 6: Commit**

```bash
git add Assets/Scripts/VoxelEngine/Rendering/Raytracing/Features/
git commit -m "feat: add 5 independent ScriptableRendererFeature classes"
```

---

### Task 4: Update VoxelRaytracePass to accept new settings type

**Files:**
- Modify: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/Passes/VoxelRaytracePass.cs`

**Step 1: Change VoxelRaytracerSettings references to VoxelRaytraceSettings**

The `VoxelRaytracePass` currently depends on `VoxelRaytracerSettings` (the old monolithic type). Change all references to `VoxelRaytraceSettings` (the new per-feature type). The field names are identical, so only the type name changes.

Replace every occurrence of `VoxelRaytracerSettings` with `VoxelRaytraceSettings` in this file.

Also replace every occurrence of `VoxelRaytracerFeature` with `VoxelRaytraceFeature` in this file (for `MousePosition` and `RaycastHitBuffer` references).

**Step 2: Commit**

```bash
git add Assets/Scripts/VoxelEngine/Rendering/Raytracing/Passes/VoxelRaytracePass.cs
git commit -m "refactor: update VoxelRaytracePass to use VoxelRaytraceSettings"
```

---

### Task 5: Update GodRaysPass to accept new settings type

**Files:**
- Modify: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/Passes/GodRaysPass.cs`

**Step 1: Change Record signature from VoxelRaytracerSettings to VoxelGodRaysSettings**

The current signature is:
```csharp
public void Record(RenderGraph renderGraph, UniversalCameraData cameraData, VoxelRaytracerSettings settings, Material godRayMaterial, ...)
```

Change to:
```csharp
public void Record(RenderGraph renderGraph, UniversalCameraData cameraData, VoxelGodRaysSettings settings, Material godRayMaterial, ...)
```

The field names on `VoxelGodRaysSettings` match the old ones (`enableGodRays` is removed since the feature being present = enabled, but the pass doesn't reference it — wait, it does). Check the guard:

```csharp
if (!settings.enableGodRays || godRayMaterial == null)
```

Since god rays being enabled is now controlled by the feature being present in the renderer, remove the `enableGodRays` check. The guard becomes:

```csharp
if (godRayMaterial == null)
```

All other field accesses (`dawnSunThreshold`, `noonSunThreshold`, `lightSourceColor`, `rayDensity`, `rayDecay`, `rayWeight`, `rayExposure`, `raySamples`) exist on `VoxelGodRaysSettings` with identical names.

**Step 2: Commit**

```bash
git add Assets/Scripts/VoxelEngine/Rendering/Raytracing/Passes/GodRaysPass.cs
git commit -m "refactor: update GodRaysPass to use VoxelGodRaysSettings"
```

---

### Task 6: Update CompositePass to accept new settings type

**Files:**
- Modify: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/Passes/CompositePass.cs`

**Step 1: Change Record signature from VoxelRaytracerSettings to VoxelCompositeSettings**

The current signature is:
```csharp
public void Record(RenderGraph renderGraph, VoxelRaytracerSettings settings, Material compositeMaterial, ...)
```

Change to:
```csharp
public void Record(RenderGraph renderGraph, VoxelCompositeSettings settings, Material compositeMaterial, ...)
```

Update the enum reference:
- `VoxelRaytracerSettings.UpscalingMode.SpatialFSR` → `VoxelCompositeSettings.UpscalingMode.SpatialFSR`

All field accesses (`sharpness`, `enableOutline`, `outlineColor`, `outlineThickness`, `outlineStrength`, `outlineShadowStrength`, `normalHighlightColor`, `normalHighlightStrength`, `normalThreshold`, `normalFadeDistance`, `upscalingMode`) exist on `VoxelCompositeSettings` with identical names.

**Step 2: Commit**

```bash
git add Assets/Scripts/VoxelEngine/Rendering/Raytracing/Passes/CompositePass.cs
git commit -m "refactor: update CompositePass to use VoxelCompositeSettings"
```

---

### Task 7: Update test and external references

**Files:**
- Modify: `Assets/Scripts/Tests/Rendering/EdgeBlurSettingsTests.cs`
- Verify (no changes needed): `Assets/Scripts/VoxelEngine/Editing/TerrainEditorTool.cs`
- Verify (no changes needed): `Assets/Scripts/VoxelEngine/Editing/DynamicTerrainEditorTool.cs`
- Verify (no changes needed): `Assets/Scripts/Tests/TestGlobalDispatch.cs`

**Step 1: Update EdgeBlurSettingsTests**

The test creates a `VoxelRaytracerSettings` to check edge blur defaults. Change to `VoxelRaytraceSettings`:

```csharp
using NUnit.Framework;
using VoxelEngine.Core.Rendering;

public class EdgeBlurSettingsTests
{
    [Test]
    public void Defaults_AreWithinExpectedRanges()
    {
        var settings = new VoxelRaytraceSettings();
        Assert.That(settings.edgeWidthPercent, Is.InRange(0.01f, 0.5f));
        Assert.That(settings.edgeRenderScale, Is.InRange(0.1f, 1.0f));
        Assert.That(settings.enableEdgeBlur, Is.True);
    }
}
```

**Step 2: Verify external references to VoxelRaytracerFeature statics**

`TerrainEditorTool.cs`, `DynamicTerrainEditorTool.cs`, and `TestGlobalDispatch.cs` reference `VoxelRaytracerFeature.MousePosition` and `VoxelRaytracerFeature.RaycastHitBuffer`. These statics now live on `VoxelRaytraceFeature` (the new class name). Update all references:

- `VoxelRaytracerFeature.MousePosition` → `VoxelRaytraceFeature.MousePosition`
- `VoxelRaytracerFeature.RaycastHitBuffer` → `VoxelRaytraceFeature.RaycastHitBuffer`

**Step 3: Commit**

```bash
git add Assets/Scripts/Tests/Rendering/EdgeBlurSettingsTests.cs Assets/Scripts/VoxelEngine/Editing/TerrainEditorTool.cs Assets/Scripts/VoxelEngine/Editing/DynamicTerrainEditorTool.cs Assets/Scripts/Tests/TestGlobalDispatch.cs
git commit -m "refactor: update external references to new feature/settings class names"
```

---

### Task 8: Delete old monolithic files

**Files:**
- Delete: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/VoxelRaytracerFeature.cs`
- Delete: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/VoxelRaytracerSettings.cs`

**Step 1: Delete old files**

```bash
git rm Assets/Scripts/VoxelEngine/Rendering/Raytracing/VoxelRaytracerFeature.cs
git rm Assets/Scripts/VoxelEngine/Rendering/Raytracing/VoxelRaytracerSettings.cs
```

**Step 2: Commit**

```bash
git commit -m "refactor: remove old monolithic VoxelRaytracerFeature and VoxelRaytracerSettings"
```

---

### Task 9: Verify compilation

**Step 1: Check for any remaining references to deleted types**

Search all `.cs` files for `VoxelRaytracerFeature` and `VoxelRaytracerSettings`. There should be zero matches (the `[MovedFrom]` attribute on the old settings class is deleted with it — if needed for asset migration, we can add one to the new class, but since we're doing manual migration this isn't necessary).

**Step 2: Verify no circular dependencies**

Confirm that:
- `VoxelFrameData` has no dependencies on any feature or pass
- Each feature depends only on its settings class, its pass class(es), and `VoxelFrameData`
- Pass classes depend only on their settings class, `PassDataClasses`, `ShaderParamIDs`, and Unity APIs
- No pass or feature imports the old `VoxelRaytracerSettings` or `VoxelRaytracerFeature`

**Step 3: Commit if any fixes were needed**

---

### Task 10: Final review and cleanup

**Step 1: Review the complete file tree**

Verify the final structure matches:
```
Assets/Scripts/VoxelEngine/Rendering/Raytracing/
├── VoxelFrameData.cs
├── CameraHistoryManager.cs
├── PassDataClasses.cs
├── ShaderParamIDs.cs
├── Features/
│   ├── VoxelRaytraceFeature.cs
│   ├── VoxelVegetationFeature.cs
│   ├── VoxelGodRaysFeature.cs
│   ├── VoxelTAAFeature.cs
│   └── VoxelCompositeFeature.cs
├── Settings/
│   ├── VoxelRaytraceSettings.cs
│   ├── VoxelGodRaysSettings.cs
│   ├── VoxelTAASettings.cs
│   └── VoxelCompositeSettings.cs
├── Passes/
│   ├── CompositePass.cs
│   ├── EdgeBlendPass.cs
│   ├── FXAAPass.cs
│   ├── GodRaysPass.cs
│   ├── TAAPass.cs
│   ├── VoxelRaytracePass.cs
│   └── VoxelVegetationPass.cs
```

**Step 2: Grep for any remaining TODO or placeholder values**

**Step 3: Final commit if needed**

```bash
git commit -m "chore: final cleanup of renderer feature split"
```
