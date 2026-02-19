# Raytracer Edge Blur Design

## Goal
Reduce raytracing workload at screen edges by rendering edges at a lower scale and blending with a smooth radial mask, preserving center quality while improving performance.

## Approach
Use a radial edge mask (outer 10% feather) to blend between a full-scale raytrace result and a reduced-scale raytrace result. The blended output continues through the existing TAA, composite, and FXAA pipeline.

## Architecture
- Extend the raytracer orchestration to schedule two raytrace passes: full scale and edge scale.
- Add an edge blend step that upsamples the edge pass and blends with the full pass using a smooth mask.
- Keep TAA/FXAA and downstream passes unchanged, using the blended result as input.

## Components
- `VoxelRaytracerFeature.cs`: orchestrator scheduling two raytrace passes and the edge blend.
- `VoxelRaytracerSettings`: edge settings (`edgeWidthPercent`, `edgeRenderScale`, optional `edgeBlurStrength`).
- Edge blend shader/material (new): computes radial mask and blends full/edge textures.
- RenderGraph: extra low-res render target for the edge raytrace output and a blended output target.

## Data Flow
1. Compute edge mask from screen UV: center 0, outer 10% feather to 1 at edges.
2. Raytrace full-scale result.
3. Raytrace edge-scale result at `edgeRenderScale`.
4. Upsample edge result and blend: `final = lerp(full, edge, edgeMask)`.
5. Feed `final` into TAA -> composite -> FXAA as normal.

## Error Handling / Guards
- If edge settings are disabled or `edgeRenderScale >= 1`, skip edge pass and blend.
- If edge RT allocation fails or size is 0, fall back to full-scale output.
- If edge blend shader/material is missing, log warning once and bypass blending.

## Testing
- Visual check: center remains crisp, edges softly blurred.
- GPU profiler: compare frame time and ray dispatch cost before/after.
- Toggle edge settings off or scale to 1 to confirm baseline match.
- Ensure TAA/FXAA behavior unchanged.
