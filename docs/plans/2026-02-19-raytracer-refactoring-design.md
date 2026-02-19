# VoxelRaytracer Refactoring Design

## Goal
Refactor `VoxelRaytracerFeature.cs` (756 lines) and `VoxelRaytracer.compute` (961 lines) into smaller, maintainable modules with single responsibilities.

## Approach
Feature-based modularity with HLSL include files for the compute shader.

---

## C# Architecture

All files under `Assets/Scripts/VoxelEngine/Rendering/Raytracing/`:

### Core Files

| File | Lines | Responsibility |
|------|-------|----------------|
| `VoxelRaytracerFeature.cs` | ~150 | Orchestrator - creates passes, manages lifecycle |
| `VoxelRaytracerSettings.cs` | ~100 | All settings fields and enums |
| `ShaderParamIDs.cs` | ~70 | Static shader property ID constants |
| `PassDataClasses.cs` | ~80 | All PassData struct definitions |
| `CameraHistoryManager.cs` | ~50 | TAA history texture management |

### Passes Directory

| File | Lines | Responsibility |
|------|-------|----------------|
| `Passes/VoxelRaytracePass.cs` | ~200 | Compute dispatch, buffer binding |
| `Passes/VoxelVegetationPass.cs` | ~80 | Grass/leaf rasterization |
| `Passes/GodRaysPass.cs` | ~100 | Occluder, blur, blend passes |
| `Passes/TAAPass.cs` | ~80 | Temporal anti-aliasing |
| `Passes/CompositePass.cs` | ~100 | Upscaling + outline effects |
| `Passes/FXAAPass.cs` | ~40 | Fast approximate anti-aliasing |

---

## Compute Shader Architecture

All files under `Assets/Scripts/VoxelEngine/Rendering/Raytracing/Compute/`:

### Main File

| File | Lines | Responsibility |
|------|-------|----------------|
| `VoxelRaytracer.compute` | ~150 | Kernel entry point only |

### Includes Directory

| File | Lines | Responsibility |
|------|-------|----------------|
| `Includes/VoxelBuffers.hlsl` | ~50 | Buffer/texture declarations |
| `Includes/VoxelSampling.hlsl` | ~100 | Brick sampling functions |
| `Includes/PBRLighting.hlsl` | ~120 | PBR + cel shading lighting |
| `Includes/Triplanar.hlsl` | ~70 | Triplanar texture sampling |
| `Includes/SVOTraversal.hlsl` | ~250 | SVO/TLAS ray traversal |
| `Includes/RaytraceCommon.hlsl` | ~60 | Utility functions, constants |

---

## Data Flow

### Per-Frame Pipeline

```
1. VoxelRaytracePass (compute)
   ↓ lowResResult, lowResDepth, lowResNormals, motionVectorTex
   
2. VoxelVegetationPass (raster, conditional)
   ↓ modified buffers
   
3. GodRaysPass (raster, conditional)
   ↓ modified lowResResult
   
4. TAAPass (raster, conditional)
   ↓ historyWrite texture
   
5. CompositePass (raster)
   ↓ compositeOutput or activeColorTexture
   
6. FXAAPass (raster, conditional)
   ↓ activeColorTexture
```

### Compute Shader Flow

```
CSMain()
  → TraceScene() → TraceSVO()
  → TriplanarSampling()
  → LightingDirect() → PBR functions
  → GetUnityShadow()
  → Output textures
```

---

## Error Handling

### C#
- Null checks for materials, VoxelVolumePool.Instance
- Early exit when VisibleChunkCount == 0
- Proper RTHandle and GraphicsBuffer cleanup

### Compute
- _ChunkCount == 0 early exit with source texture blit
- Division-by-zero guards
- Bounds checking for shadow maps
- Octant handling in SVO traversal

---

## File Structure After Refactoring

```
Rendering/Raytracing/
├── VoxelRaytracerFeature.cs
├── VoxelRaytracerSettings.cs
├── ShaderParamIDs.cs
├── PassDataClasses.cs
├── CameraHistoryManager.cs
├── Passes/
│   ├── VoxelRaytracePass.cs
│   ├── VoxelVegetationPass.cs
│   ├── GodRaysPass.cs
│   ├── TAAPass.cs
│   ├── CompositePass.cs
│   └── FXAAPass.cs
└── Compute/
    ├── VoxelRaytracer.compute
    └── Includes/
        ├── VoxelBuffers.hlsl
        ├── VoxelSampling.hlsl
        ├── PBRLighting.hlsl
        ├── Triplanar.hlsl
        ├── SVOTraversal.hlsl
        └── RaytraceCommon.hlsl
```