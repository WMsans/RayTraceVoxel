#ifndef SINE_FLOOR_GEN
#define SINE_FLOOR_GEN

float GetSineFloorSDF(float3 p)
{
    float floorHeight = sin(p.x * 0.02) * 40.0 + cos(p.z * 0.02) * 40.0;
    return p.y - floorHeight;
}

#endif
