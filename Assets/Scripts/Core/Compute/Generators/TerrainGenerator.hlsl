#ifndef TERRAIN_GEN
#define TERRAIN_GEN

#include "../Includes/GenerationContext.hlsl"

// Adapted from Inigo Quilez - https://iquilezles.org/
#define SC 1.0 

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

// Restored Helper Function
float GetHeight(float2 pos)
{
    return terrainM(pos).x;
}

void Stage_Terrain(inout GenerationContext ctx)
{
    // 1. Calculate Base Height for the SDF
    // We keep this to calculate the actual distance 'd' for the current pixel
    float height = GetHeight(ctx.position.xz);
    
    // 2. Vertical Signed Distance (The "Map" function)
    float d = (ctx.position.y - height) * 0.5;

    // 3. Tetrahedral Normal Calculation
    // ---------------------------------------------------------
    // Define the offset (epsilon). 
    // In the GLSL example: vec2 e = vec2(-1.0, 1.0) * 0.01;
    float2 e = float2(-1.0, 1.0) * 0.1; // 0.1 matches your previous 'eps' size
    
    // We need to evaluate the SDF at 4 specific corners of a tetrahedron.
    // The SDF function is: f(p) = (p.y - GetHeight(p.xz)) * 0.5
    
    // Corner 1: e.yxx (1, -1, -1)
    float3 p1 = ctx.position + float3(e.y, e.x, e.x);
    float v1 = p1.y - GetHeight(p1.xz); // * 0.5 optimization: removed (cancels out)
    
    // Corner 2: e.xxy (-1, -1, 1)
    float3 p2 = ctx.position + float3(e.x, e.x, e.y);
    float v2 = p2.y - GetHeight(p2.xz);
    
    // Corner 3: e.xyx (-1, 1, -1)
    float3 p3 = ctx.position + float3(e.x, e.y, e.x);
    float v3 = p3.y - GetHeight(p3.xz);
    
    // Corner 4: e.yyy (1, 1, 1)
    float3 p4 = ctx.position + float3(e.y, e.y, e.y);
    float v4 = p4.y - GetHeight(p4.xz);
    
    // Sum the vectors weighted by the sampled values
    float3 normal = normalize(
        float3(e.y, e.x, e.x) * v1 +
        float3(e.x, e.x, e.y) * v2 +
        float3(e.x, e.y, e.x) * v3 +
        float3(e.y, e.y, e.y) * v4
    );
    // ---------------------------------------------------------

    // 4. Union with existing SDF
    if (d < ctx.sdf)
    {
        ctx.sdf = d;
        ctx.gradient = normal;
        ctx.material = 4; 
    }
}

#endif