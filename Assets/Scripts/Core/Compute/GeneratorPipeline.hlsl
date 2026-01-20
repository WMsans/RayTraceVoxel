#ifndef GENERATOR_PIPELINE_H
#define GENERATOR_PIPELINE_H

#include "./Includes/GenerationContext.hlsl"
#include "./Generators/TerrainGenerator.hlsl"
#include "./Generators/Spheres.hlsl"
#include "./Generators/SineFloor.hlsl"

// --- Global Dynamic SDF Resources ---
StructuredBuffer<SDFObject> _SDFObjectBuffer;

// --- NEW: Voxel Grid Atlas ---
// Bound by DynamicSDFManager
Texture3D<float4> _SDFChunkAtlas;
SamplerState sampler_SDFChunkAtlas; // Defaults to LinearClamp
float4 _SDFChunkAtlasParams; // x: resolution (32), y: numChunks (16)

float sdBox(float3 p, float3 b)
{
    float3 q = abs(p) - b;
    return length(max(q,0.0)) + min(max(q.x,max(q.y,q.z)),0.0);
}

float EvaluateSDFObject(SDFObject obj, float3 worldPos, out float3 gradient)
{
    // 1. Transform to Local Space
    float3 relPos = worldPos - obj.position;
    float3 localPos = RotateVector(relPos, InvertRotation(obj.rotation));
    
    // 2. Apply Scale (Local Pos is now relative to unscaled object)
    float3 safeScale = max(abs(obj.scale), 0.001);
    float3 p = localPos / safeScale;
    float minScale = min(safeScale.x, min(safeScale.y, safeScale.z));

    float d = 3.402823466e+38; 
    gradient = float3(0,1,0);

    if (obj.type == 0) // Sphere
    {
        d = (length(p) - 0.5) * minScale;
        gradient = normalize(RotateVector(p, obj.rotation));
    }
    else if (obj.type == 1) // Cube
    {
        d = sdBox(p, float3(0.5, 0.5, 0.5)) * minScale;
        float3 signP = sign(p);
        float3 absP = abs(p);
        float maxAxis = max(max(absP.x, absP.y), absP.z);
        float3 localNormal = float3(0,1,0);
        if (absP.x >= maxAxis - 1e-4) localNormal = float3(signP.x, 0, 0);
        else if (absP.y >= maxAxis - 1e-4) localNormal = float3(0, signP.y, 0);
        else localNormal = float3(0, 0, signP.z);
        gradient = normalize(RotateVector(localNormal, obj.rotation));
    }
    else if (obj.type == 2) // [UPDATED] Voxel Grid
    {
        // 1. Map p (range -0.5 to 0.5) to UVW (range 0 to 1)
        float3 uvw = p + 0.5;

        // 2. Bounds Check
        if (any(uvw < 0.0) || any(uvw > 1.0))
        {
            d = 10.0; // Outside
            gradient = float3(0, 1, 0);
        }
        else
        {
            // 3. Map UVW to Atlas Slice
            // Atlas is stacked in Z.
            float numChunks = _SDFChunkAtlasParams.y;
            float sliceThickness = 1.0 / numChunks;
            
            float zStart = (float)obj.textureIndex * sliceThickness;
            float zLocal = uvw.z * sliceThickness;
            
            // Apply slight padding to avoid bleeding? 
            // Better to rely on Clamp and border logic in generation, 
            // but here we just sample directly.
            float3 atlasUV = float3(uvw.x, uvw.y, zStart + zLocal);
            
            // 4. Sample SDF (R channel)
            float sampledSDF = _SDFChunkAtlas.SampleLevel(sampler_SDFChunkAtlas, atlasUV, 0).r;
            
            // 5. Scale Distance
            // Texture stores 'local' SDF. Scale by object size.
            d = sampledSDF * minScale;

            // 6. Calculate Gradient (Finite Difference)
            // Texel size in UV space
            float3 texelSize = float3(1.0/_SDFChunkAtlasParams.x, 1.0/_SDFChunkAtlasParams.x, sliceThickness/_SDFChunkAtlasParams.x);
            
            float dx = _SDFChunkAtlas.SampleLevel(sampler_SDFChunkAtlas, atlasUV + float3(texelSize.x,0,0), 0).r -
                       _SDFChunkAtlas.SampleLevel(sampler_SDFChunkAtlas, atlasUV - float3(texelSize.x,0,0), 0).r;
            float dy = _SDFChunkAtlas.SampleLevel(sampler_SDFChunkAtlas, atlasUV + float3(0,texelSize.y,0), 0).r -
                       _SDFChunkAtlas.SampleLevel(sampler_SDFChunkAtlas, atlasUV - float3(0,texelSize.y,0), 0).r;
            float dz = _SDFChunkAtlas.SampleLevel(sampler_SDFChunkAtlas, atlasUV + float3(0,0,texelSize.z), 0).r -
                       _SDFChunkAtlas.SampleLevel(sampler_SDFChunkAtlas, atlasUV - float3(0,0,texelSize.z), 0).r;
            
            float3 localGrad = normalize(float3(dx, dy, dz));
            gradient = normalize(RotateVector(localGrad, obj.rotation));
        }
    }

    return d;
}

GenerationContext RunGeneratorPipeline(float3 worldPos, uint activeObjects[32], int activeCount)
{
    GenerationContext ctx;
    InitContext(ctx, worldPos);
    Stage_Terrain(ctx);

    for(int i = 0; i < activeCount; i++)
    {
        SDFObject obj = _SDFObjectBuffer[activeObjects[i]];
        float3 objGradient;
        float d = EvaluateSDFObject(obj, worldPos, objGradient);

        if (obj.operation == 0) // Union
        {
            UnionSmooth(ctx, d, objGradient, obj.materialId, obj.blendFactor);
        }
        else if (obj.operation == 1) // Subtract
        {
            float k = obj.blendFactor;
            float d1 = ctx.sdf;
            float d2 = d;
            float h = clamp( 0.5 - 0.5 * (d1 + d2) / k, 0.0, 1.0 );
            ctx.sdf = lerp( d1, -d2, h ) + k * h * (1.0 - h);
            ctx.gradient = lerp(ctx.gradient, -objGradient, h);
        }
    }

    return ctx;
}

#endif