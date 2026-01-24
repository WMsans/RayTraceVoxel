#ifndef GENERATION_CONTEXT_H
#define GENERATION_CONTEXT_H

struct GenerationContext {
    float3 position;      // World position being evaluated
    float sdf;            // Current Signed Distance Field value (minimized across stages)
    float3 gradient;      // Gradient (normal) of the SDF
    uint material;        // Material ID of the closest surface found so far
    float4 customData;    // Shared data slot for biomes, temperature, noise, etc.
};

void InitContext(inout GenerationContext ctx, float3 pos) {
    ctx.position = pos;
    ctx.sdf = 3.402823466e+38; // FLT_MAX
    ctx.gradient = float3(0, 1, 0); // Default gradient (Up)
    ctx.material = 0;          // 0 usually represents air/empty
    ctx.customData = float4(0, 0, 0, 0);
}

float smin(float a, float b, float k, out float h)
{
    h = clamp(0.5 + 0.5 * (b - a) / k, 0.0, 1.0);
    return lerp(b, a, h) - k * h * (1.0 - h);
}

// Helper to apply Smooth Union to the context
void UnionSmooth(inout GenerationContext ctx, float d, float3 newGradient, uint matID, float smoothness)
{
    float h;
    // Blend the current world SDF (ctx.sdf) with the new object (d)
    ctx.sdf = smin(ctx.sdf, d, smoothness, h);

    // Blend the gradients based on the smooth min weight 'h'
    // h=1 means fully old surface (ctx.gradient), h=0 means fully new surface (newGradient)
    ctx.gradient = lerp(newGradient, ctx.gradient, h);

    // Material blending logic:
    // h < 0.5 means the 'd' (new object) is dominant.
    if (h < 0.5)
    {
        ctx.material = matID;
    }
}

#endif