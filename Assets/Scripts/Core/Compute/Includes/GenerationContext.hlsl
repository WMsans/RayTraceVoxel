#ifndef GENERATION_CONTEXT_H
#define GENERATION_CONTEXT_H

struct GenerationContext {
    float3 position;      // World position being evaluated
    float sdf;            // Current Signed Distance Field value (minimized across stages)
    uint material;        // Material ID of the closest surface found so far
    float4 customData;    // Shared data slot for biomes, temperature, noise, etc.
};

void InitContext(inout GenerationContext ctx, float3 pos) {
    ctx.position = pos;
    ctx.sdf = 3.402823466e+38; // FLT_MAX
    ctx.material = 0;          // 0 usually represents air/empty in many voxel systems, or unassigned
    ctx.customData = float4(0, 0, 0, 0);
}

float smin(float a, float b, float k, out float h)
{
    h = clamp(0.5 + 0.5 * (b - a) / k, 0.0, 1.0);
    return lerp(b, a, h) - k * h * (1.0 - h);
}

// Helper to apply Smooth Union to the context
void UnionSmooth(inout GenerationContext ctx, float d, uint matID, float smoothness)
{
    float h;
    // Blend the current world SDF (ctx.sdf) with the new object (d)
    ctx.sdf = smin(ctx.sdf, d, smoothness, h);
    
    // Material blending logic:
    // h is the mix factor. 
    // h > 0.5 means the 'ctx.sdf' (existing world) is dominant.
    // h < 0.5 means the 'd' (new object) is dominant.
    // This creates a clean material line along the smooth curve.
    if (h < 0.5)
    {
        ctx.material = matID;
    }
}

#endif
