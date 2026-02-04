#ifndef GENERATOR_PIPELINE_H
#define GENERATOR_PIPELINE_H

#include "../../Shared/Shaders/Includes/GenerationContext.hlsl"
#include "./Generators/TerrainGenerator.hlsl"
#include "./Generators/Spheres.hlsl"
#include "./Generators/SineFloor.hlsl"
#include "./Generators/Trees.hlsl"

// --- Global Dynamic SDF Resources ---
// These are bound by VoxelVolume or DynamicSDFManager
StructuredBuffer<SDFObject> _SDFObjectBuffer;

float EvaluateSDFObject(SDFObject obj, float3 worldPos, out float3 gradient)
{
    // 1. Transform to Local Space
    float3 relPos = worldPos - obj.position;
    float3 localPos = RotateVector(relPos, InvertRotation(obj.rotation));
    
    // 2. Apply Scale
    // Fix: Protect against zero scale to avoid NaN
    float3 safeScale = max(abs(obj.scale), 0.001);
    float3 p = localPos / safeScale;
    float minScale = min(safeScale.x, min(safeScale.y, safeScale.z));

    float d = 3.402823466e+38; 
    gradient = float3(0,1,0);
    if (obj.type == 0) // Sphere
    {
        d = (length(p) - 0.5) * minScale;
        gradient = normalize(RotateVector(p, obj.rotation)); // Rotate local normal back to world
    }
    else if (obj.type == 1) // Cube
    {
        d = sdBox(p, float3(0.5, 0.5, 0.5)) * minScale;
        // Analytical Cube Gradient (Local)
        float3 signP = sign(p);
        float3 absP = abs(p);
        float maxAxis = max(max(absP.x, absP.y), absP.z);
        float3 localNormal = float3(0,1,0);
        if (absP.x >= maxAxis - 1e-4) localNormal = float3(signP.x, 0, 0);
        else if (absP.y >= maxAxis - 1e-4) localNormal = float3(0, signP.y, 0);
        else localNormal = float3(0, 0, signP.z);
        gradient = normalize(RotateVector(localNormal, obj.rotation));
    }

    return d;
}

void ApplyDynamicObjects(inout GenerationContext ctx, float3 worldPos, uint activeObjects[32], int activeCount)
{
    if (activeCount > 0)
    {
        for(int i = 0; i < activeCount; i++)
        {
            SDFObject obj = _SDFObjectBuffer[activeObjects[i]];
            float3 objGradient;
            float d = EvaluateSDFObject(obj, worldPos, objGradient);

            if (obj.operation == 0) 
            {
                UnionSmooth(ctx, d, objGradient, obj.materialId, obj.blendFactor);
            }
            else if (obj.operation == 1) 
            {
                float k = obj.blendFactor;
                float d1 = ctx.sdf;
                float d2 = d;
                float h = clamp( 0.5 - 0.5 * (d1 + d2) / k, 0.0, 1.0 );
                ctx.sdf = lerp( d1, -d2, h ) + k * h * (1.0 - h);
                ctx.gradient = lerp(ctx.gradient, -objGradient, h);
            }
        }
    }
}

GenerationContext RunGeneratorPipeline(float3 worldPos, uint activeObjects[32], int activeCount)
{
    GenerationContext ctx;
    InitContext(ctx, worldPos);
    
    // --- 1. Base Stage (Terrain) ---
    Stage_Terrain(ctx);
    
    // --- 2. Trees Stage (Minecraft-like) ---
    Stage_Trees(ctx);
    
    // --- 3. Dynamic Objects Stage ---
    ApplyDynamicObjects(ctx, worldPos, activeObjects, activeCount);

    return ctx;
}

#endif