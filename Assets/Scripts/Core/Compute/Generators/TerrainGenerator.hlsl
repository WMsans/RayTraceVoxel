#ifndef TERRAIN_GEN
#define TERRAIN_GEN

#include "../Includes/GenerationContext.hlsl"
#include "../Includes/Noise.hlsl"

void Stage_Terrain(inout GenerationContext ctx)
{
    // --- Phase 1: The Coordinate Space & Domain Warping ---
    
    // Global Coordinate Resolution: Already in ctx.position (World Space)
    
    // Domain Warping (The "Overhang" Stage)
    // Sample a low-frequency, low-amplitude 3D noise vector.
    // Scale: 0.01 (Large structures), Amplitude: 20.0
    float3 warpOffset = float3(100, 0, 100); 
    float3 warp = float3(
        snoise(ctx.position * 0.008 + warpOffset),
        snoise(ctx.position * 0.008 + warpOffset * 2.0),
        snoise(ctx.position * 0.008 + warpOffset * 3.0)
    ) * 20.0;

    float3 warpPos = ctx.position + warp;

    // --- Phase 2: Base Continental Density (The "Shape" Stage) ---
    
    // 3D Noise Selection (Base Shape)
    // Low frequency noise.
    float noiseValue = snoise(warpPos * 0.004); // Scale 0.004 -> ~250 units
    
    // The Gradient Mask
    // BaseDensity = NoiseValue - (Height * VerticalFalloff).
    // Adjust VerticalFalloff to control world height/depth. 
    // Small falloff = taller mountains/deeper oceans.
    float verticalFalloff = 0.01;
    float baseDensity = noiseValue - (warpPos.y * verticalFalloff);

    // Early Exit (Optimization)
    // If significantly Air (< -0.5) or Solid (> 0.5), we skip details.
    // We must still update ctx.sdf to provide a distance estimate for raymarching.
    // Distance approx: -Density / Falloff
    
    float threshold = 0.6; // Tune this to control where details appear.
    
    if (baseDensity < -threshold) 
    {
        // Air - Estimate distance and return
        float dist = -baseDensity / verticalFalloff;
        ctx.sdf = min(ctx.sdf, dist);
        return; 
    }
    
    if (baseDensity > threshold)
    {
        // Deep Underground - Solid
        float dist = -baseDensity / verticalFalloff;
        if (dist < ctx.sdf)
        {
            ctx.sdf = dist;
            ctx.material = 2; // Terrain Material
        }
        return;
    }

    // --- Phase 3: Volumetric Detail (The "Erosion" Stage) ---
    
    // Voxels here are near the surface (-threshold < baseDensity < threshold).
    // Apply high-frequency details.
    
    // Additive/Subtractive FBM
    // 3 octaves, persistence 0.5, lacunarity 2.0, higher scale
    float detail = fbm(warpPos, 3, 0.5, 2.0, 0.02); // Scale 0.02 -> ~50 units
    
    // Composite
    // Multiplier 0.2 reduces the impact of detail so it doesn't overwhelm base shape
    float finalDensity = baseDensity + (detail * 0.2);

    // Convert to SDF
    // Near surface, the gradient is dominated by the noise falloff + vertical falloff.
    // We can just use a constant scaler or the same falloff approximation.
    // Using a slightly more conservative estimator for surface details.
    float d = -finalDensity * 20.0; // * 20.0 is an empirical scalar to match units roughly

    // Union
    if (d < ctx.sdf)
    {
        ctx.sdf = d;
        ctx.material = 3;
    }
}

#endif
