#ifndef SPHERES_GEN
#define SPHERES_GEN

#include "../../../Shared/Shaders/Includes/GenerationContext.hlsl"

void Stage_Spheres(inout GenerationContext ctx)
{
    float3 p = ctx.position;
    float period = 120.0;
    float3 cell = floor(p / period);
    float3 local = (p / period - cell) * period;
    float3 center = float3(60, 60, 60);
    
    // 1. Calculate the sphere distance as usual
    float3 diff = local - center;
    float d = length(diff) - 30.0;
    
    // 2. Calculate Gradient (Normal) for the sphere
    float3 sphereGradient = normalize(diff);

    // 3. Define the material and blend radius
    uint sphereMat = 3; // Example: Purple material
    float blendRadius = 15.0; // LARGE value = smooth blend, SMALL value = sharp blend

    // Pass the gradient to UnionSmooth
    UnionSmooth(ctx, d, sphereGradient, sphereMat, blendRadius);
}

#endif