#ifndef VOXEL_STRUCTURES_INCLUDED
#define VOXEL_STRUCTURES_INCLUDED

// Constants
#define BRICK_SIZE 4
#define BRICK_VOXEL_COUNT 64

// Structs
struct SVONode 
{ 
    uint topology; 
    uint payloadIndex; 
};

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

#endif
