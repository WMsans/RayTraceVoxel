# Split VoxelRaytracerFeature into Multiple Renderer Features

## Problem

The single `VoxelRaytracerFeature` orchestrates 7 sub-passes through one monolithic `ScriptableRenderPass`. This makes it impossible to toggle individual effects in the URP renderer inspector, reuse subsets across renderer configs (e.g. mobile), or reason about each effect in isolation. A single `VoxelRaytracerSettings` class holds settings for all passes.

## Decision

Split into 5 independent `ScriptableRendererFeature`s. Share intermediate data via a custom `ContextItem` in URP's `ContextContainer`. Each feature owns its own serialized settings class.

## Features

Execution order is controlled by feature order in the renderer asset's feature list.

### 1. VoxelRaytraceFeature

**Passes:** `VoxelRaytracePass`, `EdgeBlendPass`

**Responsibilities:**
- Frustum culling via `VoxelVolumePool.Instance.UpdateVisibility()`
- Quality scale determination (High / Low / Custom)
- Halton jitter computation for TAA
- Main light extraction from `UniversalLightData`
- LOD pixel spread computation
- Core raytrace compute dispatch at scaled resolution
- Optional secondary raytrace dispatch at edge scale + edge blend
- Writes `VoxelFrameData` to `ContextContainer`

**Settings (`VoxelRaytraceSettings`):**
- `raytraceShader` (ComputeShader)
- `edgeBlendShader` (Shader)
- `injectionPoint` (RenderPassEvent)
- `qualityLevel`, `renderScale`, `textureScale`, `iterations`, `marchSteps`
- `bounceCount`, `lodBias`
- `enableAtmosphere`, `atmosphereColor`, `atmosphereDensity`
- `celSteps`, `shadowBrightness`
- `blueNoiseTexture`, `debugMode`
- `cullFrustum`, `useCameraFarPlane`, `shadowDistance`
- `enableEdgeBlur`, `edgeRenderScale`, `edgeWidthPercent`

### 2. VoxelVegetationFeature

**Passes:** `VoxelVegetationPass`

**Responsibilities:**
- Renders grass and leaf geometry onto the raytrace color/depth/normal buffers
- Creates a blit material internally for depth copy

**Settings:** None. Feature presence in the renderer = enabled.

### 3. VoxelGodRaysFeature

**Passes:** `GodRaysPass`

**Responsibilities:**
- Screen-space volumetric god rays (3 sub-passes: occluder extraction, radial blur, additive blend)
- Dynamic sun threshold based on sun height

**Settings (`VoxelGodRaysSettings`):**
- `godRayShader` (Shader)
- `enableGodRays` (bool)
- `noonSunThreshold`, `dawnSunThreshold`
- `rayDensity`, `rayDecay`, `rayWeight`, `rayExposure`, `raySamples`
- `lightSourceColor`

### 4. VoxelTAAFeature

**Passes:** `TAAPass`

**Responsibilities:**
- Temporal anti-aliasing with motion vector reprojection
- Per-camera double-buffered history management via `CameraHistoryManager`

**Settings (`VoxelTAASettings`):**
- `taaShader` (Shader)
- `enableTAA` (bool)
- `taaBlend` (float)

### 5. VoxelCompositeFeature

**Passes:** `CompositePass`, `FXAAPass`

**Responsibilities:**
- Upscale from render scale to full resolution (bilinear or FSR)
- Depth-based outline rendering
- Normal-based highlight edges
- Depth write to active depth texture
- Optional FXAA as final blit

**Settings (`VoxelCompositeSettings`):**
- `compositeShader` (Shader)
- `fxaaShader` (Shader)
- `upscalingMode`, `sharpness`
- `enableFXAA`
- `enableOutline`, `outlineThickness`, `outlineShadowStrength`, `outlineStrength`, `outlineColor`
- `normalHighlightStrength`, `normalThreshold`, `normalFadeDistance`, `normalHighlightColor`

## Data Sharing

A `VoxelFrameData` class extending `ContextItem` is written to the `ContextContainer` by `VoxelRaytraceFeature` and read by all downstream features.

```csharp
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

    public override void Reset()
    {
        Color = Depth = Normals = MotionVectors = TextureHandle.nullHandle;
    }
}
```

## Ordering

All features use the same `renderPassEvent`. URP executes features with the same event in the order they appear in the renderer asset's feature list. Required order:

1. VoxelRaytraceFeature
2. VoxelVegetationFeature
3. VoxelGodRaysFeature
4. VoxelTAAFeature
5. VoxelCompositeFeature

## Error Handling

- **Missing raytrace feature:** Downstream features check `frameData.Contains<VoxelFrameData>()` and early-out if absent.
- **Incorrect ordering:** Same guard prevents crashes. A `Debug.LogWarning` on first occurrence helps the user notice misconfiguration.
- **No visible chunks:** Raytrace feature skips writing `VoxelFrameData`, so all downstream features skip automatically.
- **Multiple cameras:** `CameraHistoryManager` already handles per-camera state via dictionary. No change needed.

## File Structure

```
Assets/Scripts/VoxelEngine/Rendering/Raytracing/
├── VoxelFrameData.cs                    (NEW)
├── CameraHistoryManager.cs              (KEEP)
├── PassDataClasses.cs                   (KEEP)
├── ShaderParamIDs.cs                    (KEEP)
├── Features/                            (NEW)
│   ├── VoxelRaytraceFeature.cs
│   ├── VoxelVegetationFeature.cs
│   ├── VoxelGodRaysFeature.cs
│   ├── VoxelTAAFeature.cs
│   └── VoxelCompositeFeature.cs
├── Settings/                            (NEW)
│   ├── VoxelRaytraceSettings.cs
│   ├── VoxelGodRaysSettings.cs
│   ├── VoxelTAASettings.cs
│   └── VoxelCompositeSettings.cs
├── Passes/                              (KEEP)
│   ├── CompositePass.cs
│   ├── EdgeBlendPass.cs
│   ├── FXAAPass.cs
│   ├── GodRaysPass.cs
│   ├── TAAPass.cs
│   ├── VoxelRaytracePass.cs
│   └── VoxelVegetationPass.cs
```

Deleted files: `VoxelRaytracerFeature.cs`, `VoxelRaytracerSettings.cs`

## Migration

The `PC_Renderer.asset` must be re-configured manually in the Unity editor:
1. Remove the old `VoxelRaytracerFeature` from the renderer
2. Add the 5 new features in the correct order
3. Assign shader references and configure settings on each feature
