#ifndef SINE_FLOOR_GEN
#define SINE_FLOOR_GEN

#include "../../../Shared/Shaders/Includes/GenerationContext.hlsl"

void Stage_SineFloor(inout GenerationContext ctx)
{
    float floorHeight = sin(ctx.position.x * 0.02) * 40.0 + cos(ctx.position.z * 0.02) * 40.0;
    float d = ctx.position.y - floorHeight;

    float hx = 0.8 * cos(ctx.position.x * 0.02);
    float hz = -0.8 * sin(ctx.position.z * 0.02);
    float3 floorGrad = normalize(float3(-hx, 1.0, -hz));

    // Union operation: keep the closest surface
    if (d < ctx.sdf)
    {
        ctx.sdf = d;
        ctx.gradient = floorGrad;
        ctx.material = 3;
    }
}

#endif