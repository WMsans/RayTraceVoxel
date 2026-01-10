#ifndef GENERATOR_PIPELINE_H
#define GENERATOR_PIPELINE_H

#include "./Includes/GenerationContext.hlsl"

// --- Stage Includes ---
// Add new generator file includes here
#include "./Generators/SineFloor.hlsl"
#include "./Generators/Spheres.hlsl"

GenerationContext RunGeneratorPipeline(float3 worldPos)
{
    GenerationContext ctx;
    InitContext(ctx, worldPos);

    // --- Pipeline Execution ---
    // You can reorder these, add conditions, or use ctx.customData to pass info between them.
    
    Stage_SineFloor(ctx);
    Stage_Spheres(ctx);
    
    return ctx;
}

#endif
