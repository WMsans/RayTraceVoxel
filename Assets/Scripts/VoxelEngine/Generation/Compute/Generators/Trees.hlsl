#ifndef TREES_GEN
#define TREES_GEN

#include "../../../Shared/Shaders/Includes/GenerationContext.hlsl"
#include "../../../Shared/Shaders/Includes/Noise.hlsl"
#include "TerrainGenerator.hlsl"

// --- Constants ---
#define TREE_GRID_SIZE 64.0
#define TREE_CHANCE 0.65
// Materials as requested: 5 for Trunk, 6 for Branches
#define MAT_LOG 5
#define MAT_LEAVES 6

// --- SDF Primitives ---

// Tapered Cylinder (Cone segment)
float sdCappedCone(float3 p, float h, float r1, float r2)
{
    float2 q = float2(length(p.xz), p.y);
    float2 k1 = float2(r2, h);
    float2 k2 = float2(r2 - r1, 2.0 * h);
    float2 ca = float2(q.x - min(q.x, (q.y < 0.0) ? r1 : r2), abs(q.y) - h);
    float2 cb = q - k1 + k2 * clamp(dot(k1 - q, k2) / dot(k2, k2), 0.0, 1.0);
    float s = (cb.x < 0.0 && ca.y < 0.0) ? -1.0 : 1.0;
    return s * sqrt(min(dot(ca, ca), dot(cb, cb)));
}

// Ellipsoid
float sdEllipsoid(float3 p, float3 r)
{
    float k0 = length(p / r);
    float k1 = length(p / (r * r));
    return k0 * (k0 - 1.0) / k1;
}

// Box (Required for compilation safety, though not strictly used in the optimized tree logic)
float sdBox(float3 p, float3 b)
{
    float3 q = abs(p) - b;
    return length(max(q,0.0)) + min(max(q.x,max(q.y,q.z)),0.0);
}

// Capsule for branches
// Returns distance and outputs a local gradient approximation
float sdCapsule(float3 p, float3 a, float3 b, float r, out float3 grad)
{
    float3 pa = p - a, ba = b - a;
    float h = clamp( dot(pa,ba)/dot(ba,ba), 0.0, 1.0 );
    float3 diff = pa - ba*h;
    float d = length(diff);
    // Gradient points away from the central line segment
    grad = (d > 0.0001) ?
           diff / d : float3(0,1,0); 
    return d - r;
}

// --- Combined Tree Logic (Distance + Analytical Gradient) ---

// Rotates a 2D vector p by angle a
float2 rotate(float2 p, float a) 
{
    float s = sin(a);
    float c = cos(a);
    return float2(p.x * c - p.y * s, p.x * s + p.y * c);
}

// Simple deterministic hash
float Hash2D(float2 p)
{
    return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
}

// Calculates distance, material, and gradient in a SINGLE PASS
void GetTreeData(float3 p, float h, float angle, out float dist, out uint mat, out float3 grad)
{
    // --- Transform to Tree Local Space ---
    // We rotate the position inverse to the tree rotation
    float3 pRot = p;
    pRot.xz = rotate(p.xz, -angle);

    // --- 1. Sequoia Trunk (The Primary Shape) ---
    float rBottom = 5.0 + (h * 0.03);
    float rTop = 2.0;
    float trunkH = h * 0.98;
    float halfH = trunkH * 0.5;
    // Primitive Distance (Cheap)
    float dTrunk = sdCappedCone(pRot - float3(0, halfH, 0), halfH, rBottom, rTop);

    // [OPTIMIZATION] Conditional Noise
    // Only apply expensive noise if we are close to the surface (< 3.0 units)
    // This saves calculating noise for the 90% of voxels that are just "air".
    if (dTrunk < 3.0) 
    {
        float fluting = sin(atan2(pRot.z, pRot.x) * 12.0) * exp(-pRot.y * 0.12) * 1.5;
        float barkNoise = snoise(pRot * 0.3) * 0.4;
        dTrunk += barkNoise + fluting;
    }

    // Trunk Gradient (Approximation)
    float3 gTrunkLocal = normalize(float3(pRot.x, 0, pRot.z));

    // Initialize Result with Trunk
    dist = dTrunk;
    float3 gLocal = gTrunkLocal;
    mat = MAT_LOG;

    // --- 2. Branches (Sequoia Style) ---

    // [OPTIMIZATION] Branch Zone Culling
    // Branches only exist in the upper canopy.
    // If the voxel is near the bottom of the tree, OR too far horizontally, 
    // we SKIP the entire branch loop.
    float branchStartH = h * 0.45;  // Branches start 45% up
    float branchEndH = h * 0.95;    // Branches end near top
    float maxBranchLen = 16.0;      // Longest branch length
    float padding = 4.0;            // Padding for blend/SDF safety

    bool inBranchZone = (pRot.y > branchStartH - padding) && 
                        (pRot.y < branchEndH + padding) &&
                        (length(pRot.xz) < maxBranchLen + padding);
    
    if (inBranchZone)
    {
        int branchCount = 10;
        float blend = 2.5;

        for(int i = 0; i < branchCount; i++)
        {
            float t = (float)i / (float)(branchCount - 1);
            float yPos = lerp(branchStartH, branchEndH, t);
            
            // Quick Vertical Check for this specific branch
            // If the voxel is too far from this specific branch height, skip it.
            if (abs(pRot.y - yPos) > blend + 2.0) continue;

            float branchAng = t * 25.0;
            float bLen = lerp(16.0, 5.0, t);
            float bRad = lerp(1.2, 0.4, t);

            // Construct Branch in Local Space
            float3 pBranch = pRot;
            pBranch.y -= yPos;
            pBranch.xz = rotate(pBranch.xz, -branchAng);
            float curve = pBranch.x * pBranch.x * 0.015;
            pBranch.y -= curve;
            
            float3 bGradLocal;
            float dB = sdCapsule(pBranch, float3(0.5, 0, 0), float3(bLen, 0, 0), bRad, bGradLocal);
            
            // [OPTIMIZATION] Skip expensive branch operations if too far to blend
            if (dB > blend) continue;
            
            // [OPTIMIZATION] Conditional Branch Noise
            if (dB < 1.5) 
            {
                dB += snoise(pBranch * 0.6) * 0.15;
            }

            // Correct Gradient Rotation
            bGradLocal.xz = rotate(bGradLocal.xz, branchAng);

            // Smooth Union
            // hMix approaches 1.0 when Branch (dB) is dominant, 0.0 when Trunk (dist) is dominant
            float hMix = clamp(0.5 + 0.5 * (dist - dB) / blend, 0.0, 1.0);
            dist = lerp(dist, dB, hMix) - blend * hMix * (1.0 - hMix);
            gLocal = normalize(lerp(gLocal, bGradLocal, hMix));
            
            // FIX: If hMix > 0.5, we are closer to the Branch, so apply LEAVES material
            if (hMix > 0.5) mat = MAT_LEAVES;
        }
    }

    // --- 3. Final Transform ---
    grad = gLocal;
    grad.xz = rotate(gLocal.xz, angle);
}

void Stage_Trees(inout GenerationContext ctx)
{
    // 1. Grid Traversal
    float2 currentGridId = floor(ctx.position.xz / TREE_GRID_SIZE);
    float minD = 1e5;
    uint bestMat = 0;
    float3 bestGrad = float3(0,1,0);
    bool foundTree = false;

    // Search 3x3 neighbor cells
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float2 neighbor = float2(x, y);
            float2 cellId = currentGridId + neighbor;
            
            float h = Hash2D(cellId);
            
            if (h < TREE_CHANCE)
            {
                // Jitter position
                float2 offset = (float2(Hash2D(cellId + 1.13), Hash2D(cellId + 3.51)) * 0.6 + 0.2) * TREE_GRID_SIZE;
                float2 treeXZ = cellId * TREE_GRID_SIZE + offset;
                
                // BOUNDS CHECK: 
                // 40.0 radius covers a huge area.
                // We use Manhattan distance for a slightly cheaper pre-check before expensive math
                float dx = abs(ctx.position.x - treeXZ.x);
                float dz = abs(ctx.position.z - treeXZ.y);
                if (dx > 40.0 || dz > 40.0) continue;

                // [OPTIMIZATION] Only calculate Terrain Height if XZ check passes
                float terrainH = GetHeight(treeXZ);
                float3 treeBase = float3(treeXZ.x, terrainH, treeXZ.y);
                
                float treeHeight = 80.0 + h * 50.0;
                
                // Vertical Bounds Check (Important!)
                if (ctx.position.y < terrainH - 10.0 || ctx.position.y > terrainH + treeHeight + 30.0) continue;

                // Random Rotation
                float rotationAngle = Hash2D(cellId + 5.7) * 6.28318;
                
                // Calculate Tree Data
                float d;
                uint mat;
                float3 g;
                // Pass relative position to helper
                GetTreeData(ctx.position - treeBase, treeHeight, rotationAngle, d, mat, g);
                
                // Union (Min)
                if (d < minD)
                {
                    minD = d;
                    bestMat = mat;
                    bestGrad = g;
                    foundTree = true;
                }
            }
        }
    }

    // 3. Apply to Context
    if (foundTree && minD < ctx.sdf)
    {
        ctx.sdf = minD;
        ctx.material = bestMat;
        ctx.gradient = bestGrad;
    }
}

#endif