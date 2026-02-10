#ifndef OAK_TREES_GEN
#define OAK_TREES_GEN

#include "../../../Shared/Shaders/Includes/GenerationContext.hlsl"
#include "../../../Shared/Shaders/Includes/Noise.hlsl"
#include "TerrainGenerator.hlsl"

// --- Configuration ---
#define OAK_GRID_SIZE 24.0   
#define OAK_CHANCE 0.65      
#define MAT_OAK_LOG 5
#define MAT_OAK_LEAVES 6

// --- Local Helpers ---

float sdEllipsoidOak(float3 p, float3 r)
{
    float k0 = length(p / r);
    float k1 = length(p / (r * r));
    return k0 * (k0 - 1.0) / k1;
}

float sdVerticalCapsule(float3 p, float h, float r)
{
    p.y -= clamp(p.y, 0.0, h);
    return length(p) - r;
}

float HashOak(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
}

// --- Oak Tree Logic ---
void GetOakTree(float3 p, float h, out float dist, out uint mat, out float3 grad)
{
    // 1. Trunk (Cylinder/Capsule)
    // Scaled up: Base radius ~2.5, Top ~1.8
    float r = lerp(2.5, 1.8, clamp(p.y / h, 0.0, 1.0));
    float dTrunk = sdVerticalCapsule(p, h * 0.85, r); 
    
    // Trunk Gradient
    float3 gTrunk = normalize(float3(p.x, 0, p.z));

    // 2. Leaves (Ellipsoid)
    // Center canopy higher up
    float3 leafCenter = float3(0, h * 0.9, 0);

    // HUGE Canopy: ~20 units wide, ~12 units tall
    float3 leafRad = float3(10.0, 6.0, 10.0);
    float3 pLeaf = p - leafCenter;
    float dLeaves = sdEllipsoidOak(pLeaf, leafRad);

    // [PERFORMANCE CHANGE] 
    // Removed 3D Noise application here to improve performance.
    // The conditional block 'if (dLeaves < 4.0) { ... snoise ... }' was deleted.
    
    // 3. Union (Trunk + Leaves)
    float k = 1.2;
    // Smoother blend for larger shapes
    float hMix = clamp(0.5 + 0.5 * (dTrunk - dLeaves) / k, 0.0, 1.0);
    dist = lerp(dTrunk, dLeaves, hMix) - k * hMix * (1.0 - hMix);

    mat = (hMix > 0.5) ? MAT_OAK_LEAVES : MAT_OAK_LOG;

    // Gradient blending
    float3 gLeaves = normalize(pLeaf);
    grad = normalize(lerp(gTrunk, gLeaves, hMix));
}

void Stage_OakTrees(inout GenerationContext ctx)
{
    float2 currentGridId = floor(ctx.position.xz / OAK_GRID_SIZE);
    
    float minD = 1e5;
    uint bestMat = 0;
    float3 bestGrad = float3(0,1,0);
    bool found = false;

    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float2 neighbor = float2(x, y);
            float2 cellId = currentGridId + neighbor;
            
            float h = HashOak(cellId);

            if (h < OAK_CHANCE)
            {
                // Jitter
                float2 offset = (float2(HashOak(cellId + 1.0), HashOak(cellId + 2.0)) * 0.5 + 0.25) * OAK_GRID_SIZE;
                float2 treeXZ = cellId * OAK_GRID_SIZE + offset;

                // Radius check increased to 18.0 for larger canopies
                // if (abs(ctx.position.x - treeXZ.x) > 18.0 || abs(ctx.position.z - treeXZ.y) > 18.0) continue;

                float terrainH = GetHeight(treeXZ);
                
                // Height increased: Range 18.0 to 30.0
                float treeHeight = 18.0 + h * 12.0;

                // Vertical bounds check
                if (ctx.position.y < terrainH - 5.0 || ctx.position.y > terrainH + treeHeight + 15.0) continue;

                float d; uint mat; float3 g;
                GetOakTree(ctx.position - float3(treeXZ.x, terrainH, treeXZ.y), treeHeight, d, mat, g);

                if (d < minD)
                {
                    minD = d;
                    bestMat = mat;
                    bestGrad = g;
                    found = true;
                }
            }
        }
    }

    if (found && minD < ctx.sdf)
    {
        ctx.sdf = minD;
        ctx.material = bestMat;
        ctx.gradient = bestGrad;
    }
}

#endif