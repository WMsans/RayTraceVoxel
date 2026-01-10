#ifndef GENERATION_CONTEXT_H
#define GENERATION_CONTEXT_H

struct GenerationContext {
    float3 position;      // World position being evaluated
    float sdf;            // Current Signed Distance Field value (minimized across stages)
    uint material;        // Material ID of the closest surface found so far
    uint lod;             // Level of Detail (0 = Highest Detail/Closest)
    float4 customData;    // Shared data slot for biomes, temperature, noise, etc.
};

void InitContext(inout GenerationContext ctx, float3 pos, uint lodLevel) {
    ctx.position = pos;
    ctx.sdf = 3.402823466e+38; // FLT_MAX
    ctx.material = 0;          // 0 usually represents air/empty
    ctx.lod = lodLevel;
    ctx.customData = float4(0, 0, 0, 0);
}

#endif
