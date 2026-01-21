#ifndef TREES_GEN
#define TREES_GEN

#include "../Includes/GenerationContext.hlsl"
#include "../Includes/Noise.hlsl" // Required for snoise/fbm
#include "TerrainGenerator.hlsl"

// --- Constants ---
#define TREE_GRID_SIZE 32.0
#define TREE_CHANCE 0.65
#define MAT_LOG 5
#define MAT_LEAVES 6

// --- SDF Primitives ---

// Box
float sdBox(float3 p, float3 b)
{
    float3 q = abs(p) - b;
    return length(max(q,0.0)) + min(max(q.x,max(q.y,q.z)),0.0);
}

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

// --- Tree Definition ---

// Calculates distance and material for a single tree instance
float GetTreeSDF(float3 p, float h, out uint mat)
{
    // --- 1. Organic Trunk ---
    // Apply slight bend to the trunk using sine waves
    float3 pTrunk = p;
    pTrunk.x += sin(p.y * 0.05) * 2.0;
    pTrunk.z += cos(p.y * 0.04) * 2.0;

    // Dimensions
    float rBottom = 3.5;
    float rTop = 1.2;
    float trunkHeight = h * 0.6; // Trunk is 60% of total height

    // Offset so base is at 0
    float dTrunk = sdCappedCone(pTrunk - float3(0, trunkHeight * 0.5, 0), trunkHeight * 0.5, rBottom, rTop);

    // Add bark texture (micro-noise)
    dTrunk += snoise(pTrunk * 0.8) * 0.15;

    // --- 2. Leaf Canopy ---
    // Position canopy near the top
    float3 pLeaves = p - float3(0, h * 0.85, 0);

    // Domain Warp: Distort the coordinate space to make the sphere "lumpy"
    float3 warp = float3(
        snoise(pLeaves * 0.08),
        snoise(pLeaves * 0.08 + float3(10,0,0)),
        snoise(pLeaves * 0.08 + float3(0,0,10))
    );
    pLeaves += warp * 4.0; 

    // Ellipsoid Shape
    float3 leafSize = float3(16.0, 12.0, 16.0) * (h / 70.0);
    float dLeaves = sdEllipsoid(pLeaves, leafSize);

    // Surface Detail: Roughen the leaves
    dLeaves += snoise(p * 0.25) * 1.5; // Large lumps
    dLeaves += snoise(p * 0.8) * 0.5;  // Small detail

    // --- 3. Blending ---
    // Smoothly blend trunk and leaves
    float blendStrength = 4.0;
    float hBlend = clamp(0.5 + 0.5 * (dLeaves - dTrunk) / blendStrength, 0.0, 1.0);
    float dFinal = lerp(dLeaves, dTrunk, hBlend) - blendStrength * hBlend * (1.0 - hBlend);

    // Material Logic: If closer to trunk shape, use Log, else Leaves
    // Use a biased comparison to let leaves cover the top branches
    if (dTrunk < dLeaves + 1.0) mat = MAT_LOG;
    else mat = MAT_LEAVES;

    return dFinal;
}

// Helper: Calculate normal using Finite Difference (expensive but necessary for organic noise)
float3 GetTreeNormal(float3 p, float h)
{
    float2 e = float2(1.0, -1.0) * 0.01; // Epsilon
    uint m; // dummy
    return normalize(
        e.xyy * GetTreeSDF(p + e.xyy, h, m) +
        e.yyx * GetTreeSDF(p + e.yyx, h, m) +
        e.yxy * GetTreeSDF(p + e.yxy, h, m) +
        e.xxx * GetTreeSDF(p + e.xxx, h, m)
    );
}

// Simple deterministic hash
float Hash2D(float2 p)
{
    return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
}

void Stage_Trees(inout GenerationContext ctx)
{
    // 1. Grid Traversal
    float2 currentGridId = floor(ctx.position.xz / TREE_GRID_SIZE);

    // Track the closest tree surface found in this search
    float minD = 1e5;
    uint bestMat = 0;
    float3 bestTreePos = float3(0,0,0);
    float bestTreeHeight = 0;
    bool foundTree = false;

    // Search 3x3 neighbor cells
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float2 neighbor = float2(x, y);
            float2 cellId = currentGridId + neighbor;
            
            // 2. Deterministic Placement
            float h = Hash2D(cellId);
            if (h < TREE_CHANCE)
            {
                // Random position within cell
                float2 offset = (float2(Hash2D(cellId + 1.13), Hash2D(cellId + 3.51)) * 0.6 + 0.2) * TREE_GRID_SIZE;
                float2 treeXZ = cellId * TREE_GRID_SIZE + offset;
                
                // Get Terrain Height
                float terrainH = GetHeight(treeXZ);
                float3 treeBase = float3(treeXZ.x, terrainH, treeXZ.y);
                
                // Tree Parameters
                float treeHeight = 65.0 + h * 40.0; // Range: 65 - 105

                // Evaluate Distance
                uint mat;
                float d = GetTreeSDF(ctx.position - treeBase, treeHeight, mat);
                
                // Union (Min)
                if (d < minD)
                {
                    minD = d;
                    bestMat = mat;
                    bestTreePos = treeBase;
                    bestTreeHeight = treeHeight;
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
        // Calculate smooth normal for the closest tree
        ctx.gradient = GetTreeNormal(ctx.position - bestTreePos, bestTreeHeight);
    }
}

#endif
