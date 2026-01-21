#ifndef TREES_GEN
#define TREES_GEN

#include "../Includes/GenerationContext.hlsl"
#include "TerrainGenerator.hlsl"

// --- Constants ---
#define TREE_GRID_SIZE 32.0
#define TREE_CHANCE 0.8
#define MAT_LOG 5
#define MAT_LEAVES 6

// --- Helpers ---

// Simple deterministic hash
float Hash2D(float2 p)
{
    return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
}

// Box SDF
float sdBox(float3 p, float3 b)
{
    float3 q = abs(p) - b;
    return length(max(q,0.0)) + min(max(q.x,max(q.y,q.z)),0.0);
}

// Analytical Normal for Box
float3 sdBoxNormal(float3 p, float3 b)
{
    float3 pAbs = abs(p);
    float3 pSign = sign(p);
    // Find the axis with the largest deviation relative to box dimensions
    // (Simplification for axis-aligned boxes)
    float3 d = pAbs - b;
    float maxD = max(max(d.x, d.y), d.z);
    
    // Default up
    float3 n = float3(0,1,0); 
    
    // If outside or on boundary, pick the face we are closest to
    if (d.x >= maxD - 1e-4) n = float3(pSign.x, 0, 0);
    else if (d.y >= maxD - 1e-4) n = float3(0, pSign.y, 0);
    else if (d.z >= maxD - 1e-4) n = float3(0, 0, pSign.z);
    
    return n;
}

void Stage_Trees(inout GenerationContext ctx)
{
    // 1. Grid Traversal
    // Check current and neighboring cells to handle trees overlapping cell boundaries
    float2 currentGridId = floor(ctx.position.xz / TREE_GRID_SIZE);

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
                // 3. Tree Parameters
                // Random position within cell
                float2 offset = (float2(Hash2D(cellId + 1.13), Hash2D(cellId + 3.51)) * 0.6 + 0.2) * TREE_GRID_SIZE;
                float2 treeXZ = cellId * TREE_GRID_SIZE + offset;
                
                // Get Terrain Height at tree position
                float terrainH = GetHeight(treeXZ);
                float3 treeBase = float3(treeXZ.x, terrainH, treeXZ.y);
                
                // Tree Dimensions (Variation based on hash)
                float trunkHeight = 70.0 + h * 30.0;
                
                float trunkWidth = 6.0;

                // Note: These are half-extents (radius), so the foliage box is 36x24x36
                float3 leavesSize = float3(18.0, 12.0, 18.0); 
                
                // --- Construct SDF ---
                float3 p = ctx.position - treeBase;
                
                // A. Trunk (Box)
                // Center the trunk box vertically
                float3 pTrunk = p - float3(0, trunkHeight * 0.5, 0);
                float dTrunk = sdBox(pTrunk, float3(trunkWidth, trunkHeight * 0.5, trunkWidth));
                
                // B. Leaves (Box)
                // Place on top of trunk
                float3 pLeaves = p - float3(0, trunkHeight + leavesSize.y - 1.0, 0);
                float dLeaves = sdBox(pLeaves, leavesSize);
                
                // 4. Combine parts
                float dTree = dLeaves;
                uint matTree = MAT_LEAVES;
                float3 nTree = sdBoxNormal(pLeaves, leavesSize);
                
                // Union Trunk
                if (dTrunk < dLeaves)
                {
                    dTree = dTrunk;
                    matTree = MAT_LOG;
                    nTree = sdBoxNormal(pTrunk, float3(trunkWidth, trunkHeight * 0.5, trunkWidth));
                }
                
                // 5. Union with World
                // Using hard union (min) for blocky look
                if (dTree < ctx.sdf)
                {
                    ctx.sdf = dTree;
                    ctx.material = matTree;
                    ctx.gradient = nTree;
                }
            }
        }
    }
}

#endif