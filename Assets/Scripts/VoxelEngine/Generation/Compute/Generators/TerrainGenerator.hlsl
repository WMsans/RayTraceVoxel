#ifndef TERRAIN_GEN
#define TERRAIN_GEN

#include "../../../Shared/Shaders/Includes/GenerationContext.hlsl"

// ==========================================================================================
// TUBE TERRAIN LAYER
// ==========================================================================================

float tube_noi(float2 p)
{
    return 0.5 * (cos(6.2831 * p.x) + cos(6.2831 * p.y));
}

float tube_terrainMed(float2 p)
{
    p *= 0.0013;
    float s = 1.0;
    float t = 0.0;
    for (int i = 0; i < 6; i++)
    {
        t += s * tube_noi(p);
        s *= 0.5 + 0.1 * t;

        float2 nextP;
        nextP.x = 1.6 * p.x + 1.2 * p.y;
        nextP.y = -1.2 * p.x + 1.6 * p.y;
        
        p = 0.97 * nextP + (t - 0.5) * 0.2;
    }
    return t * 55.0;
}

float tube_shape(float3 pos)
{
    float sep = 400.0;
    // Noise distortion
    pos.z -= sep * 0.025 * tube_noi(0.005 * pos.xz * float2(0.5, 1.5));
    pos.x -= sep * 0.050 * tube_noi(0.005 * pos.zy * float2(0.5, 1.5));
    // Domain Repetition
    float2 posShifted = pos.xz + sep * 0.5;
    float2 qosXZ = posShifted - floor(posShifted / sep) * sep - sep * 0.5;
    float3 qos = float3(qosXZ.x, pos.y - 70.0, qosXZ.y);

    qos.x += sep * 0.3 * cos(0.01 * pos.z);
    qos.y += sep * 0.1 * cos(0.01 * pos.x);

    float sph = length(qos.xy) - sep * 0.012;
    // Surface detail
    sph -= (1.0 - 0.8 * smoothstep(-10.0, 0.0, qos.y)) * sep * 0.003 * tube_noi(0.15 * pos.xy * float2(0.2, 1.0));
    return sph;
}

// Combined SDF Function
float2 MapTubeTerrain(float3 pos)
{
    float h = pos.y - tube_terrainMed(pos.xz);
    float sph = tube_shape(pos);

    float k = 60.0;
    float w = clamp(0.5 + 0.5 * (h - sph) / k, 0.0, 1.0);
    float finalSDF = lerp(h, sph, w) - k * w * (1.0 - w);
    return float2(finalSDF, w);
}

void Stage_TubeTerrain(inout GenerationContext ctx)
{
    float2 res = MapTubeTerrain(ctx.position);
    float d = res.x;
    float w = res.y;

    if (d < ctx.sdf)
    {
        ctx.sdf = d;
        ctx.material = (w > 0.5) ? 3 : 4;
        
        float2 e = float2(-1.0, 1.0) * 0.1;
        float v1 = MapTubeTerrain(ctx.position + float3(e.y, e.x, e.x)).x;
        float v2 = MapTubeTerrain(ctx.position + float3(e.x, e.x, e.y)).x;
        float v3 = MapTubeTerrain(ctx.position + float3(e.x, e.y, e.x)).x;
        float v4 = MapTubeTerrain(ctx.position + float3(e.y, e.y, e.y)).x;
        ctx.gradient = normalize(
            float3(e.y, e.x, e.x) * v1 +
            float3(e.x, e.x, e.y) * v2 +
            float3(e.x, e.y, e.x) * v3 +
            float3(e.y, e.y, e.y) * v4
        );
    }
}

// ==========================================================================================
// NEW TERRAIN IMPLEMENTATION (Reference Port)
// ==========================================================================================

// --- Hashes ---
float hash1(float2 p)
{
    // FIX: Using bitwise integer hash instead of sine.
    // This is stable for negative coordinates AND high precision at large world distances.
    uint2 q = (uint2)int2(p);
    uint h = q.x * 374761393U + q.y * 668265263U;
    h = (h ^ (h >> 13)) * 1274126177U;
    return float(h ^ (h >> 16)) / 4294967296.0;
}

// --- Noise (Value Noise) ---
float noise(float2 x)
{
    float2 p = floor(x);
    float2 w = frac(x);

    // Quintic interpolation curve
    float2 u = w * w * w * (w * (w * 6.0 - 15.0) + 10.0);
    float a = hash1(p + float2(0, 0));
    float b = hash1(p + float2(1, 0));
    float c = hash1(p + float2(0, 1));
    float d = hash1(p + float2(1, 1));
    return -1.0 + 2.0 * (a + (b - a) * u.x + (c - a) * u.y + (a - b - c + d) * u.x * u.y);
}

// --- FBM Construction ---
float2 mul_m2(float2 x)
{
    return float2(0.8 * x.x - 0.6 * x.y, 0.6 * x.x + 0.8 * x.y);
}

float fbm_9(float2 x)
{
    float f = 1.9;
    float s = 0.55;
    float a = 0.0;
    float b = 0.5;
    for (int i = 0; i < 9; i++)
    {
        float n = noise(x);
        a += b * n;
        b *= s;
        x = mul_m2(x * f);
    }
    return a;
}

// --- Main Height Logic ---
float GetNewTerrainHeight(float2 p)
{
    float e = fbm_9(p / 2000.0 + float2(1.0, -2.0));
    e = 600.0 * e + 600.0;
    e += 90.0 * smoothstep(552.0, 594.0, e);
    return e - 500.0;
}

// Exposed Helper
float GetHeight(float2 pos)
{
    return GetNewTerrainHeight(pos);
}

void Stage_Terrain(inout GenerationContext ctx)
{
    float h = GetNewTerrainHeight(ctx.position.xz);
    
    // 1. Calculate the Vertical Distance (Heightmap distance)
    float verticalDist = ctx.position.y - h;

    // 2. Calculate the Normal (Gradient)
    float2 e = float2(0.1, 0.0);
    float h1 = GetNewTerrainHeight(ctx.position.xz - e.xy);
    float h2 = GetNewTerrainHeight(ctx.position.xz + e.xy);
    float h3 = GetNewTerrainHeight(ctx.position.xz - e.yx);
    float h4 = GetNewTerrainHeight(ctx.position.xz + e.yx);

    // Note: The gradient vector (dh/dx, 1, dh/dz) is unnormalized surface normal
    float3 unnormalizedNormal = float3(h1 - h2, 2.0 * e.x, h3 - h4);
    float3 normal = normalize(unnormalizedNormal);

    // 3. FIX: Convert Vertical Distance to True Perpendicular SDF Distance
    // We multiply by dot(N, Up). Since Up is (0,1,0), this is just normal.y
    float trueSDF = verticalDist * normal.y;

    if (trueSDF < ctx.sdf)
    {
        ctx.sdf = trueSDF;
        ctx.material = 4; // Generic terrain material
        ctx.gradient = normal;
    }
}

#endif