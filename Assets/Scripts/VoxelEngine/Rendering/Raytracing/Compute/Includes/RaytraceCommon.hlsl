// -----------------------------------------------------------------------------
// Helper Functions
// -----------------------------------------------------------------------------
#define EPSILON 0.01

float GetInterleavedGradientNoise(float2 pixelPos) 
{ 
    float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
    return frac(magic.z * frac(dot(pixelPos, magic.xy)));
}

float GetDither(uint2 pixelPos) 
{ 
    uint width, height; 
    _BlueNoiseTexture.GetDimensions(width, height);
    if (width > 0) 
        return _BlueNoiseTexture[pixelPos % uint2(width, height)].r;
    else 
        return GetInterleavedGradientNoise(float2(pixelPos));
}

float2 IntersectBox(float3 rayOrigin, float3 rayDir, float3 boxMin, float3 boxMax) 
{ 
    float3 tMin = (boxMin - rayOrigin) / rayDir;
    float3 tMax = (boxMax - rayOrigin) / rayDir; 
    float3 t1 = min(tMin, tMax); 
    float3 t2 = max(tMin, tMax);
    float tNear = max(max(t1.x, t1.y), t1.z); 
    float tFar = min(min(t2.x, t2.y), t2.z); 
    return float2(tNear, tFar);
}

float3 GetLODNormal(float3 hitPos, float3 boxPos, float3 boxSize) 
{ 
    float3 center = boxPos + boxSize * 0.5;
    float3 p = hitPos - center; 
    float3 signP = sign(p); 
    float3 absP = abs(p); 
        
    float maxAxis = max(max(absP.x, absP.y), absP.z);
    if (absP.x >= maxAxis - 1e-4) return float3(signP.x, 0, 0);
    if (absP.y >= maxAxis - 1e-4) return float3(0, signP.y, 0);
    return float3(0, 0, signP.z);
}

float3 GetRandomColor(uint seed) 
{ 
    float3 p3 = frac(float3(seed, seed, seed) * float3(.1031, .1030, .0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}
