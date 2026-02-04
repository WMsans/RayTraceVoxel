#ifndef SINE_FLOOR_GEN
#define SINE_FLOOR_GEN

#include "../../../Shared/Shaders/Includes/GenerationContext.hlsl"

void Stage_SineFloor(inout GenerationContext ctx)
{
    float floorHeight = sin(ctx.position.x * 0.02) * 40.0 + cos(ctx.position.z * 0.02) * 40.0;
    float d = ctx.position.y - floorHeight;

    // Calculate derivative
    // H(x, z) = 40sin(0.02x) + 40cos(0.02z)
    // H'x = 40 * 0.02 * cos(0.02x) = 0.8 * cos(0.02x)
    // H'z = 40 * 0.02 * -sin(0.02z) = -0.8 * sin(0.02z)
    // Normal = normalize(-H'x, 1, -H'z)
    float hx = 0.8 * cos(ctx.position.x * 0.02);
    float hz = -0.8 * sin(ctx.position.z * 0.02);
    float3 floorGrad = normalize(float3(-hx, 1.0, -hz));

    // Union operation: keep the closest surface
    if (d < ctx.sdf)
    {
        ctx.sdf = d;
        ctx.gradient = floorGrad;
        ctx.material = 4; // Assign generic floor material ID
    }
}

#endif