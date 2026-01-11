#ifndef TERRAIN_GEN
#define TERRAIN_GEN

#include "../Includes/GenerationContext.hlsl"

// Adapted from Inigo Quilez - https://iquilezles.org/
#define SC 1.0 

// [Keep your existing hash, noised, and terrainM functions exactly as they were]
float hash(float2 p)
{
    float3 p3  = frac(float3(p.xyx) * .1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float3 noised(float2 x)
{
    float2 f = frac(x);
    float2 u = f*f*f*(f*(f*6.0-15.0)+10.0);
    float2 du = 30.0*f*f*(f*(f-2.0)+1.0);
    
    float2 p = floor(x);
    float a = hash(p + float2(0,0));
    float b = hash(p + float2(1,0));
    float c = hash(p + float2(0,1));
    float d = hash(p + float2(1,1));
    
    float k0 = a;
    float k1 = b - a;
    float k2 = c - a;
    float k3 = a - b - c + d;
    float val = k0 + k1*u.x + k2*u.y + k3*u.x*u.y;
    float2 grad = du * (float2(k1, k2) + k3*u.yx);
    return float3(val, grad);
}

static const float2x2 m2 = float2x2(0.8, 0.6, -0.6, 0.8);
float3 terrainM(float2 x)
{
    float2 p = x * 0.003 / SC;
    float a = 0.0;
    float b = 1.0;
    float2 d = float2(0.0, 0.0);
    
    for(int i = 0; i < 9; i++)
    {
        float3 n = noised(p);
        d += n.yz;
        a += b * n.x / (1.0 + dot(d,d));
        b *= 0.5;
        p = mul(m2, p) * 2.0;
    }
    
    return float3(SC * 120.0 * a, d);
}

// --- NEW HELPER FUNCTION ---
// Extracts just the height to simplify the normal calculation code
float GetHeight(float2 pos)
{
    return terrainM(pos).x;
}

void Stage_Terrain(inout GenerationContext ctx)
{
    // 1. Calculate the Base Height at the current position
    // We can still use the full terrainM call here to get the height
    float height = GetHeight(ctx.position.xz);
    
    // 2. Vertical Signed Distance (Positive = Air, Negative = Ground)
    float d = (ctx.position.y - height) * 0.5;

    // 3. Calculate Normal using Finite Differences
    // This samples the terrain 2 extra times to get the EXACT geometric slope.
    // epsilon: Small offset (0.1 is usually good for world-scale terrain)
    float eps = 0.1; 
    
    // Sample height slightly to the East (X+) and North (Z+)
    float h_x = GetHeight(ctx.position.xz + float2(eps, 0));
    float h_z = GetHeight(ctx.position.xz + float2(0, eps));
    
    // Calculate the slope vectors
    // The surface drops by (height - h_x) over the distance 'eps'
    float3 normal = normalize(float3(height - h_x, eps, height - h_z));

    // 4. Union with existing SDF
    if (d < ctx.sdf)
    {
        ctx.sdf = d;
        ctx.gradient = normal;
        ctx.material = 2; // Terrain Material
    }
}

#endif