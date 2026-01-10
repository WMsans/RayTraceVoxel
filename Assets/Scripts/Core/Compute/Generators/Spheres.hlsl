#ifndef SPHERES_GEN
#define SPHERES_GEN

#include "../Includes/GenerationContext.hlsl"

void Stage_Spheres(inout GenerationContext ctx)
{
    float3 p = ctx.position;
    float period = 120.0; 
    float3 cell = floor(p / period);
    float3 local = (p / period - cell) * period; 
    float3 center = float3(60, 60, 60);
    float d = length(local - center) - 30.0;

    // Union operation
    if (d < ctx.sdf)
    {
        ctx.sdf = d;
        ctx.material = 2; // Assign generic sphere material ID
    }
}

#endif