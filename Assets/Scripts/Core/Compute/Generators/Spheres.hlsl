#ifndef SPHERES_GEN
#define SPHERES_GEN

float GetSpheresSDF(float3 p)
{
    float period = 120.0; 
    float3 cell = floor(p / period);
    float3 local = (p / period - cell) * period; 
    float3 center = float3(60, 60, 60);
    return length(local - center) - 30.0;
}

#endif
