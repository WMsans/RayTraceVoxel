#ifndef VOXEL_STRUCTURES_INCLUDED
#define VOXEL_STRUCTURES_INCLUDED

// --- Updated Constants ---
#define BRICK_SIZE 4
#define BRICK_PADDING 1
#define BRICK_STORAGE_SIZE 6 // BRICK_SIZE + 2*PADDING
#define BRICK_VOXEL_COUNT 216 // 6*6*6

// Structs
struct SVONode 
{ 
    uint topology; 
    uint payloadIndex;
    uint lodColor; 
    uint lodMaterial; 
};

// ... [Rest of VoxelStructures.hlsl remains unchanged]
struct VoxelPayload 
{ 
    uint brickDataIndex; 
};

struct VoxelTypeGPU
{
    uint sideAlbedoIndex; 
    uint sideNormalIndex;
    uint sideMaskIndex;
    
    uint topAlbedoIndex; 
    uint topNormalIndex; 
    uint topMaskIndex;
    
    float sideMetallic; 
    float topMetallic; 
    
    uint renderType;
};

struct VoxelLight 
{ 
    float4 position; 
    float4 color; 
    float4 attenuation; 
};

struct ChunkDef
{
    float3 boundsMin;
    uint nodeOffset;
    
    float3 boundsMax;
    uint payloadOffset;
    uint brickOffset;
    float3 padding; 
};

// Helper Functions
uint GetNodeIndex(uint level, uint3 gridPos)
{
    uint offset = 0;
    if (level > 0) offset += 1;
    if (level > 1) offset += 8;
    if (level > 2) offset += 64;
    if (level > 3) offset += 512;

    uint3 p = gridPos;
    if (level == 0) p = uint3(0,0,0);
    else if (level == 1) p = p >> 3;
    else if (level == 2) p = p >> 2;
    else if (level == 3) p = p >> 1;

    uint m = 0;
    for (int i = 0; i < 4; i++) 
    {
        uint mask = 1 << i;
        m |= ((p.x & mask) ? (1 << (3*i)) : 0);
        m |= ((p.y & mask) ? (1 << (3*i + 1)) : 0);
        m |= ((p.z & mask) ? (1 << (3*i + 2)) : 0);
    }
    return offset + m;
}

uint PackColor(float4 c)
{
    uint r = (uint)(saturate(c.r) * 255.0);
    uint g = (uint)(saturate(c.g) * 255.0);
    uint b = (uint)(saturate(c.b) * 255.0);
    uint a = (uint)(saturate(c.a) * 255.0);
    return (r << 24) | (g << 16) | (b << 8) | a;
}

float4 UnpackColor(uint packedCol)
{
    float r = (float)((packedCol >> 24) & 0xFF) / 255.0;
    float g = (float)((packedCol >> 16) & 0xFF) / 255.0;
    float b = (float)((packedCol >> 8) & 0xFF) / 255.0;
    float a = (float)(packedCol & 0xFF) / 255.0;
    return float4(r, g, b, a);
}

// --- Normal Packing (8-bit per channel) ---
uint PackNormal(float3 n)
{
    float3 sn = n * 0.5 + 0.5;
    uint x = (uint)(saturate(sn.x) * 255.0);
    uint y = (uint)(saturate(sn.y) * 255.0);
    uint z = (uint)(saturate(sn.z) * 255.0);
    return (x << 16) | (y << 8) | z;
}

float3 UnpackNormal(uint packedNormal)
{
    float x = (float)((packedNormal >> 16) & 0xFF) / 255.0;
    float y = (float)((packedNormal >> 8) & 0xFF) / 255.0;
    float z = (float)(packedNormal & 0xFF) / 255.0;
    return normalize(float3(x, y, z) * 2.0 - 1.0);
}

#endif