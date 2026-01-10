#ifndef TERRAIN_GEN
#define TERRAIN_GEN

#include "../Includes/GenerationContext.hlsl"

// Adapted from Inigo Quilez - https://iquilezles.org/
// Scale factor adapted for Voxel World (1 unit = 1 meter approx)
// Original reference SC was 250.0 for kilometers-scale landscapes.
#define SC 1.0 

// Hash function to replace texture lookup (Procedural Value Noise Source)
float hash(float2 p)
{
    float3 p3  = frac(float3(p.xyx) * .1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// Value Noise with Derivatives
float3 noised(float2 x)
{
    float2 f = frac(x);
    // Quintic interpolation curve
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

// Rotation matrix (0.8, 0.6, -0.6, 0.8) matching the GLSL reference
static const float2x2 m2 = float2x2(0.8, 0.6, -0.6, 0.8);

float terrainM(float2 x)
{
    float2 p = x * 0.003 / SC;
    float a = 0.0;
    float b = 1.0;
    float2 d = float2(0.0, 0.0);
    
    // 9 Octaves of Erosion-Noise
    for(int i = 0; i < 9; i++)
    {
        float3 n = noised(p);
        d += n.yz;
        a += b * n.x / (1.0 + dot(d,d));
        b *= 0.5;
        p = mul(m2, p) * 2.0;
    }
    
    return SC * 120.0 * a;
}

void Stage_Terrain(inout GenerationContext ctx)
{
    // Height calculation
    float height = terrainM(ctx.position.xz);
    
    // Vertical Signed Distance (Positive = Air, Negative = Ground)
    // Multiplier 0.5 helps avoid raymarching artifacts on steep slopes
    float d = (ctx.position.y - height) * 0.5;
    
    // Union with existing SDF
    if (d < ctx.sdf)
    {
        ctx.sdf = d;
        ctx.material = 2; // Terrain Material
    }
}

#endif