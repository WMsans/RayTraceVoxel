#ifndef TERRAIN_GEN
#define TERRAIN_GEN

#include "../../../Shared/Shaders/Includes/GenerationContext.hlsl"

// --- Adapted Constants and Noise Functions ---

// Matrix from GLSL: mat2(1.6, -1.2, 1.2, 1.6)
// Note: GLSL mat2 columns are (1.6, -1.2) and (1.2, 1.6). 
// We implement the transform explicitly to ensure exact parity.

float noi(float2 p)
{
    return 0.5 * (cos(6.2831 * p.x) + cos(6.2831 * p.y));
}

float terrainMed(float2 p)
{
    p *= 0.0013;
    float s = 1.0;
    float t = 0.0;
    for (int i = 0; i < 6; i++)
    {
        t += s * noi(p);
        s *= 0.5 + 0.1 * t;
        
        float2 nextP;
        nextP.x = 1.6 * p.x + 1.2 * p.y;
        nextP.y = -1.2 * p.x + 1.6 * p.y;
        
        p = 0.97 * nextP + (t - 0.5) * 0.2;
    }
    return t * 55.0;
}

// Helper for SVOBuilder optimization (Estimates surface height)
float GetHeight(float2 pos)
{
    return terrainMed(pos);
}

float tubes(float3 pos)
{
    float sep = 400.0;
    
    // Noise distortion
    pos.z -= sep * 0.025 * noi(0.005 * pos.xz * float2(0.5, 1.5));
    pos.x -= sep * 0.050 * noi(0.005 * pos.zy * float2(0.5, 1.5));
    
    // Domain Repetition (Modulo logic)
    // GLSL: qos = mod(pos + sep*0.5, sep) - sep*0.5
    float2 posShifted = pos.xz + sep * 0.5;
    float2 qosXZ = posShifted - floor(posShifted / sep) * sep - sep * 0.5;
    
    float3 qos = float3(qosXZ.x, pos.y - 70.0, qosXZ.y);
    
    qos.x += sep * 0.3 * cos(0.01 * pos.z);
    qos.y += sep * 0.1 * cos(0.01 * pos.x);

    float sph = length(qos.xy) - sep * 0.012;
    
    // Surface detail on tubes
    sph -= (1.0 - 0.8 * smoothstep(-10.0, 0.0, qos.y)) * sep * 0.003 * noi(0.15 * pos.xy * float2(0.2, 1.0));
    
    return sph;
}

// Combined SDF Function
// Returns x: SDF Distance, y: Blend Factor (0=Terrain, 1=Tubes)
float2 MapTerrain(float3 pos)
{
    float h = pos.y - terrainMed(pos.xz);
    float sph = tubes(pos);
    
    float k = 60.0;
    // Smooth Union / Mix logic
    float w = clamp(0.5 + 0.5 * (h - sph) / k, 0.0, 1.0);
    
    // smin logic: mix(h, sph, w) - k*w*(1.0-w)
    float finalSDF = lerp(h, sph, w) - k * w * (1.0 - w);
    
    return float2(finalSDF, w);
}

void Stage_Terrain(inout GenerationContext ctx)
{
    // 1. Evaluate blended SDF
    float2 res = MapTerrain(ctx.position);
    float d = res.x;
    float w = res.y;

    // 2. Union with existing SDF
    if (d < ctx.sdf)
    {
        ctx.sdf = d;
        
        // 3. Material Assignment
        // w close to 0 is Terrain, w close to 1 is Tubes.
        // We assign ID 4 for Terrain, ID 3 for Tubes.
        ctx.material = (w > 0.5) ? 3 : 4; 

        // 4. Tetrahedral Normal Calculation
        // Calculate the gradient of the MapTerrain function
        float2 e = float2(-1.0, 1.0) * 0.1;
        
        float v1 = MapTerrain(ctx.position + float3(e.y, e.x, e.x)).x;
        float v2 = MapTerrain(ctx.position + float3(e.x, e.x, e.y)).x;
        float v3 = MapTerrain(ctx.position + float3(e.x, e.y, e.x)).x;
        float v4 = MapTerrain(ctx.position + float3(e.y, e.y, e.y)).x;
        
        ctx.gradient = normalize(
            float3(e.y, e.x, e.x) * v1 +
            float3(e.x, e.x, e.y) * v2 +
            float3(e.x, e.y, e.x) * v3 +
            float3(e.y, e.y, e.y) * v4
        );
    }
}

#endif