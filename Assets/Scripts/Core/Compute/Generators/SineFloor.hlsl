#ifndef SINE_FLOOR_GEN
#define SINE_FLOOR_GEN

#include "../Includes/GenerationContext.hlsl"

void Stage_SineFloor(inout GenerationContext ctx)
{
    float floorHeight = sin(ctx.position.x * 0.02) * 40.0 + cos(ctx.position.z * 0.02) * 40.0;
    float d = ctx.position.y - floorHeight;

    // Union operation: keep the closest surface
    if (d < ctx.sdf)
    {
        ctx.sdf = d;
        ctx.material = 1; // Assign generic floor material ID
    }
}

#endif