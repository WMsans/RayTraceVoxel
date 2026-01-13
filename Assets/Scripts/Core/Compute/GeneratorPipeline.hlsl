#ifndef GENERATOR_PIPELINE_H
#define GENERATOR_PIPELINE_H

#include "./Includes/GenerationContext.hlsl"
#include "./Generators/TerrainGenerator.hlsl"
#include "./Generators/Spheres.hlsl"
#include "./Generators/SineFloor.hlsl"

// --- Global Dynamic SDF Resources ---
// These are bound by VoxelVolume or DynamicSDFManager
StructuredBuffer<SDFObject> _SDFObjectBuffer;
Texture3D<float> _SDFAtlas;
SamplerState sampler_LinearClamp; 
float4 _SDFAtlasParams; // x=Resolution, y=TotalDepth, z=ShapeCount, w=Unused

// --- SDF Primitives ---

float sdBox(float3 p, float3 b)
{
    float3 q = abs(p) - b;
    return length(max(q,0.0)) + min(max(q.x,max(q.y,q.z)),0.0);
}

// Evaluates a single SDF Object
// Returns float4(distance, normal.x, normal.y, normal.z)
// Note: Normal calculation for mesh is approximated or needs extra samples.
// For now we assume analytical or up-vector for simple integration.
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
        float3 distToEdge = 0.5 - absP;
        float maxAxis = max(max(absP.x, absP.y), absP.z);
        float3 localNormal = float3(0,1,0);
        
        if (absP.x >= maxAxis - 1e-4) localNormal = float3(signP.x, 0, 0);
        else if (absP.y >= maxAxis - 1e-4) localNormal = float3(0, signP.y, 0);
        else localNormal = float3(0, 0, signP.z);
        
        gradient = normalize(RotateVector(localNormal, obj.rotation));
    }
    else if (obj.type == 2) // Mesh (Texture3D)
    {
        if (all(abs(p) < 0.5))
        {
            float3 uvw = p + 0.5;
            float shapeCount = _SDFAtlasParams.z;
            uvw.z = (uvw.z + (float)obj.textureIndex) / shapeCount;
            
            float val = _SDFAtlas.SampleLevel(sampler_LinearClamp, uvw, 0).r;
            float signedDist = val * 2.0 - 1.0; 
            
            d = signedDist * minScale;

            float e = 0.01;
            float3 uvwX = uvw + float3(e, 0, 0);
            float3 uvwY = uvw + float3(0, e, 0);
            float3 uvwZ = uvw + float3(0, 0, e/shapeCount);

            float dX = (_SDFAtlas.SampleLevel(sampler_LinearClamp, uvwX, 0).r * 2.0 - 1.0) * minScale;
            float dY = (_SDFAtlas.SampleLevel(sampler_LinearClamp, uvwY, 0).r * 2.0 - 1.0) * minScale;
            float dZ = (_SDFAtlas.SampleLevel(sampler_LinearClamp, uvwZ, 0).r * 2.0 - 1.0) * minScale;
            
            float3 localGrad = normalize(float3(dX - d, dY - d, dZ - d));
            gradient = normalize(RotateVector(localGrad, obj.rotation));
        }
    }
    
    return d;
}

GenerationContext RunGeneratorPipeline(float3 worldPos, uint activeObjects[32], int activeCount)
{
    GenerationContext ctx;
    InitContext(ctx, worldPos);

    // --- 1. Base Stage (Terrain) ---
    Stage_Terrain(ctx);
    // Stage_SineFloor(ctx);

    // --- 2. Dynamic Objects Stage ---
    // Iterate over the culled list of objects
    for(int i = 0; i < activeCount; i++)
    {
        SDFObject obj = _SDFObjectBuffer[activeObjects[i]];
        
        float3 objGradient;
        float d = EvaluateSDFObject(obj, worldPos, objGradient);

        // --- Combine ---
        if (obj.operation == 0) // Union
        {
            UnionSmooth(ctx, d, objGradient, obj.materialId, obj.blendFactor);
        }
        else if (obj.operation == 1) // Subtract
        {
            // Smooth Subtract: smax(a, -b, k) = -smin(-a, b, k)
            float h;
            float negativeD = -d;
            
            // Invert gradient for subtraction
            float3 subGradient = -objGradient;
            
            // smin(-ctx.sdf, d, k) -> we want smax so we negate everything
            // Standard smooth subtraction: Max(A, -B)
            
            // Simple hard subtract for now to ensure correctness, then smooth
            // float result = max(ctx.sdf, -d);
            
            // Smooth:
            float k = obj.blendFactor;
            float h2 = clamp( 0.5 - 0.5*(ctx.sdf + d)/k, 0.0, 1.0 );
            ctx.sdf = lerp( ctx.sdf, -d, h2 ) + k*h2*(1.0-h2);
            
            // Gradient blending approximation
            if (h2 > 0.5) ctx.gradient = subGradient;
            
            // Material: If we subtract, we expose the "inside" of the subtractor? 
            // Usually we keep the original material or set to 0 (Air) if d is dominant.
            // If the resulting surface is defined by the subtractor, it's actually just 'Air' usually.
        }
        // Add other operations (Intersect, Paint) as needed
    }
    
    return ctx;
}

#endif