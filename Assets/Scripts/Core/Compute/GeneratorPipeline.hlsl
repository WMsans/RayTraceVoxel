#ifndef GENERATOR_PIPELINE_H
#define GENERATOR_PIPELINE_H

#include "./Includes/GenerationContext.hlsl"

// --- Stage Includes ---
// Add new generator file includes here
#include "./Generators/TerrainGenerator.hlsl"

GenerationContext RunGeneratorPipeline(float3 worldPos, uint lod)
{
    GenerationContext ctx;
    InitContext(ctx, worldPos, lod);

    // --- Pipeline Execution ---
    // You can reorder these, add conditions, or use ctx.customData to pass info between them.
    
    // Stage_SineFloor(ctx);
    // Stage_Spheres(ctx);
    Stage_Terrain(ctx);
    
    return ctx;
}

#endif
