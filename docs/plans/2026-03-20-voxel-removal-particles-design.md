# Voxel Removal Particles Design

## Summary

Add VFX Graph-based debris particles that spawn when the player subtracts voxels with the brush tool. Particles are textured with the voxel material and burst outward with gravity before fading.

## Requirements

- Particles spawn on brush subtraction only (not structural debris removal)
- Particles are textured with the removed voxel's material via Texture2DArray sampling
- Motion: burst outward + gravity + fade over 1-2 seconds
- Spawn point: brush center with random offset within brush radius
- No physics — VFX Graph handles all motion
- No GPU readback needed — sample one material ID + position + radius from brush data

## Architecture

### Trigger Point

Hook into `TerrainEditorTool` after brush subtraction dispatch. Capture brush center, radius, and sample material ID at that position.

### Data Flow

```
TerrainEditorTool.OnBrushSubtract()
    → Get brush center, radius
    → Sample material ID at brush center
    → VoxelModifier.EditVoxelsSphere() [existing]
    → VoxelVFXManager.SpawnDebris(position, radius, materialID)
        → VisualEffect.SetVector3("Position", position)
        → VisualEffect.SetFloat("Radius", radius)
        → VisualEffect.SetInt("MaterialID", materialID)
        → VisualEffect.SendEvent("OnSpawn")
```

### VFX Graph Structure

```
VoxelDebris.vfx
├── Properties (CPU-set)
│   ├── Position (Vector3)
│   ├── Radius (float)
│   └── MaterialID (int)
├── Spawn System
│   └── Burst on "OnSpawn" event
├── Initialize Context
│   ├── Position: Position + random offset within Radius sphere
│   ├── Velocity: outward direction + upward bias
│   └── Lifetime: 1.0-2.0s random
├── Update Context
│   ├── Gravity: -9.8 m/s²
│   ├── Drag: 0.5
│   └── Color: fade alpha over lifetime
└── Output (Quad)
    └── Custom shader: sample Texture2DArray[MaterialID]
```

### Particle Shader

Custom output shader in VFX Graph:
1. Bind `VoxelAlbedoArray` (Texture2DArray from VoxelDefinitionManager)
2. Sample slice index = MaterialID
3. Standard UV or spherical UV for particle quads

## Files

| File | Action | Description |
|------|--------|-------------|
| `Assets/VFX/VoxelDebris.vfx` | Create | VFX Graph asset |
| `Assets/VFX/Shaders/VoxelDebris.shader` | Create | Custom output shader for texture array |
| `Assets/Scripts/VoxelEngine/Effects/VoxelVFXManager.cs` | Create | Manages VFX instances, spawn API |
| `Assets/Scripts/VoxelEngine/Editing/TerrainEditorTool.cs` | Modify | Add VFX spawn hook after subtraction |

## VFX Instance Management

Single pooled `VisualEffect` component attached to a manager GameObject. VFX Graph handles internal particle pooling. No per-particle object pooling needed.

## Material ID Sampling

Sample material ID by querying the SVO at brush center position. Use existing voxel sampling utilities or raycast hit data if already available from the brush interaction.
