#ifndef OAK_TREES_GEN
#define OAK_TREES_GEN

#include "../../../Shared/Shaders/Includes/GenerationContext.hlsl"
#include "../../../Shared/Shaders/Includes/Noise.hlsl"
#include "TerrainGenerator.hlsl"

// --- Configuration ---
#define OAK_GRID_SIZE 12.0   // Much denser than the 64.0 used for big trees
#define OAK_CHANCE 0.65      // 65% chance per cell = dense forest
#define MAT_OAK_LOG 5
#define MAT_OAK_LEAVES 6

// --- Local Helpers (Renamed to avoid collision with Trees.hlsl) ---

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

// Simple hash for placement
float HashOak(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
}

// --- Oak Tree Logic ---
// Returns: dist, material, gradient (approx)
void GetOakTree(float3 p, float h, out float dist, out uint mat, out float3 grad)
{
    // 1. Trunk (Cylinder/Capsule)
    // Slightly tapered: base is r=0.9, top is r=0.7
    float r = lerp(0.9, 0.7, clamp(p.y / h, 0.0, 1.0));
    float dTrunk = sdVerticalCapsule(p, h * 0.8, r); // Trunk goes up to 80% of height
    
    // Trunk Gradient (Horizontal approximation)
    float3 gTrunk = normalize(float3(p.x, 0, p.z));

    // 2. Leaves (Ellipsoid with Noise)
    // Center the canopy near the top
    float3 leafCenter = float3(0, h * 0.85, 0);
    // Radius: Wide X/Z, shorter Y
    float3 leafRad = float3(4.0, 2.8, 4.0); 
    
    float3 pLeaf = p - leafCenter;
    float dLeaves = sdEllipsoidOak(pLeaf, leafRad);

    // Apply 3D Noise to Leaves for "Organic/Minecrafty" look
    // Only calculate noise if we are somewhat close to the shape to save perf
    if (dLeaves < 3.0) 
    {
        // Frequency 0.6, Amplitude 0.6
        float noiseVal = snoise(pLeaf * 0.6) * 0.6;
        dLeaves += noiseVal;
    }
    
    // 3. Union (Trunk + Leaves)
    // We use a slight smooth blend to glue them together
    float k = 0.5; // Blend factor
    float hMix = clamp(0.5 + 0.5 * (dTrunk - dLeaves) / k, 0.0, 1.0);
    dist = lerp(dTrunk, dLeaves, hMix) - k * hMix * (1.0 - hMix);

    // Material logic: If closer to leaves (hMix > 0.5), use leaves
    mat = (hMix > 0.5) ? MAT_OAK_LEAVES : MAT_OAK_LOG;

    // Gradient blending
    float3 gLeaves = normalize(pLeaf); // Approx gradient for ellipsoid
    grad = normalize(lerp(gTrunk, gLeaves, hMix));
}

void Stage_OakTrees(inout GenerationContext ctx)
{
    float2 currentGridId = floor(ctx.position.xz / OAK_GRID_SIZE);
    
    float minD = 1e5;
    uint bestMat = 0;
    float3 bestGrad = float3(0,1,0);
    bool found = false;

    // 3x3 Neighbor Search (Standard for grid-based scattering)
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float2 neighbor = float2(x, y);
            float2 cellId = currentGridId + neighbor;
            
            // Random check
            float h = HashOak(cellId);
            if (h < OAK_CHANCE)
            {
                // Jitter position within cell
                float2 offset = (float2(HashOak(cellId + 1.0), HashOak(cellId + 2.0)) * 0.5 + 0.25) * OAK_GRID_SIZE;
                float2 treeXZ = cellId * OAK_GRID_SIZE + offset;

                // Cheap bounding box check (Radius 8.0 is enough for Oaks)
                if (abs(ctx.position.x - treeXZ.x) > 8.0 || abs(ctx.position.z - treeXZ.y) > 8.0) continue;

                // Terrain Height Check
                float terrainH = GetHeight(treeXZ);
                float treeHeight = 7.0 + h * 4.0; // Height varies between 7 and 11
                
                // Vertical bounds check
                if (ctx.position.y < terrainH - 5.0 || ctx.position.y > terrainH + treeHeight + 10.0) continue;

                // Calculate Oak SDF
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

    // Union with global SDF
    if (found && minD < ctx.sdf)
    {
        ctx.sdf = minD;
        ctx.material = bestMat;
        ctx.gradient = bestGrad;
    }
}

#endif