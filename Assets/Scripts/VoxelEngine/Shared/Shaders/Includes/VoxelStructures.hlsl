#ifndef VOXEL_STRUCTURES_INCLUDED
#define VOXEL_STRUCTURES_INCLUDED

// --- Updated Constants ---
#define BRICK_SIZE 4
#define BRICK_PADDING 1
#define BRICK_STORAGE_SIZE 6 // BRICK_SIZE + 2*PADDING
#define BRICK_VOXEL_COUNT 216 // 6*6*6

#define PAGE_SIZE 2048

// Packing Constants
#define MAX_SDF_RANGE 4.0 // Clamp SDF to +/- 4.0 voxels before packing

// --- FLAGS ---
// Bit 31 of packedInfo indicates a uniform solid node (no payload/brick data)
#define NODE_FLAG_SOLID (1u << 31)

// Structs
struct SVONode 
{ 
    uint topology;
    uint lodColor; 
    uint packedInfo; // [Flag 1] [Unused 7] [Material 8] [Payload 16] 
    // Note: bit 31 is Flag. Payload is bits 0-15. Material is 16-23.
};

// Helper to unpack node info
void UnpackNode(SVONode node, out uint payloadIndex, out uint materialID)
{
    payloadIndex = node.packedInfo & 0xFFFF;
    materialID = (node.packedInfo >> 16) & 0xFF;
}

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
    uint pageTableOffset;
    float3 boundsMax;
    uint payloadPageTableOffset;
    uint brickOffset;
    float3 padding; 
    float4x4 worldToLocal;
    float4x4 localToWorld;
};

uint GetPhysicalIndex(uint virtualIndex, uint pageTableOffset, StructuredBuffer<uint> pageTable)
{
    uint pageID = virtualIndex / PAGE_SIZE;
    uint offsetInPage = virtualIndex % PAGE_SIZE;
    uint physicalPageStart = pageTable[pageTableOffset + pageID];
    return physicalPageStart + offsetInPage;
}

struct SDFObject
{
    float3 position;
    float pad0;
    float4 rotation;
    float3 scale;
    float pad1;
    float3 boundsMin;
    float pad2;
    float3 boundsMax;
    float pad3;
    int type;      
    int operation;
    float blendFactor;
    int materialId;
    int padUnused;
    float3 padding;
};

struct LBVHNode
{
    float3 boundsMin;
    int leftChild; 
    float3 boundsMax;
    int rightChild;
};

struct TLASCell
{
    uint offset;
    uint count;
};

// --- Math Helpers ---

float3 RotateVector(float3 v, float4 q)
{
    float3 t = 2.0 * cross(q.xyz, v);
    return v + q.w * t + cross(q.xyz, t);
}

float4 InvertRotation(float4 q)
{
    return float4(-q.xyz, q.w);
}

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

// Octahedral Encoding (16-bit)
uint PackNormalOct(float3 n)
{
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    float2 oct = n.z >= 0 ? n.xy : (1.0 - abs(n.yx)) * (n.xy >= 0 ? 1.0 : -1.0);
    uint2 packed = (uint2)(saturate(oct * 0.5 + 0.5) * 255.0);
    return packed.x | (packed.y << 8);
}

float3 UnpackNormalOct(uint p)
{
    float2 oct = float2(p & 0xFF, (p >> 8) & 0xFF) / 255.0;
    oct = oct * 2.0 - 1.0;
    
    float3 n = float3(oct.x, oct.y, 1.0 - abs(oct.x) - abs(oct.y));
    float t = saturate(-n.z);
    n.xy += n.xy >= 0.0 ? -t : t;
    return normalize(n);
}

// Packs Material(8), SDF(8), Normal(16) -> uint(32)
uint PackVoxelData(float sdf, float3 normal, uint materialID)
{
    uint mat = materialID & 0xFF;
    float normalizedSDF = clamp(sdf / MAX_SDF_RANGE, -1.0, 1.0);
    uint sdfInt = (uint)((normalizedSDF * 0.5 + 0.5) * 255.0);
    uint norm = PackNormalOct(normal);
    return mat | (sdfInt << 8) | (norm << 16);
}

void UnpackVoxelData(uint data, out float sdf, out float3 normal, out uint materialID)
{
    materialID = data & 0xFF;
    uint sdfInt = (data >> 8) & 0xFF;
    float normalizedSDF = (sdfInt / 255.0) * 2.0 - 1.0;
    sdf = normalizedSDF * MAX_SDF_RANGE;
    normal = UnpackNormalOct(data >> 16);
}

#endif