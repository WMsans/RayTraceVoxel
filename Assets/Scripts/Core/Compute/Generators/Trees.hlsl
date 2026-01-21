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

// --- SDF Primitives (Distance Only) ---

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

// --- Combined Tree Logic (Distance + Analytical Gradient) ---

// Rotates a 2D vector p by angle a
float2 rotate(float2 p, float a) 
{
    float s = sin(a);
    float c = cos(a);
    return float2(p.x * c - p.y * s, p.x * s + p.y * c);
}

// Calculates distance, material, and gradient in a SINGLE PASS
// This replaces the expensive finite-difference normal calculation.
void GetTreeData(float3 p, float h, float angle, out float dist, out uint mat, out float3 grad)
{
    // Apply Rotation
    // We rotate the position inverse to the tree rotation
    float3 pRot = p;
    pRot.xz = rotate(p.xz, -angle);

    // --- 1. Organic Trunk ---
    float3 pTrunk = pRot;
    // Simple bend (cheap)
    float bend = sin(pRot.y * 0.05);
    pTrunk.x += bend * 2.0;
    
    // Trunk Dimensions
    float rBottom = 3.5;
    float rTop = 1.2;
    float trunkHeight = h * 0.8;
    float halfTrunkH = trunkHeight * 0.5;

    // Trunk SDF
    // Note: We use a simplified vertical capsule/cone approximation for the gradient to save cost
    // instead of the exact derivative of sdCappedCone which is complex.
    float dTrunk = sdCappedCone(pTrunk - float3(0, halfTrunkH, 0), halfTrunkH, rBottom, rTop);
    
    // Cheap Bark Noise (Single octave, low frequency)
    // We add this to distance but IGNORE it for gradient to keep it cheap and smooth-shaded
    float barkNoise = snoise(pTrunk * 0.4) * 0.3;
    dTrunk += barkNoise;

    // Approx Trunk Gradient: Horizontal vector away from center + slight up/down tilt for taper
    // This is "good enough" for organic shapes.
    // Ensure gradient is calculated in rotated space then rotated back!
    float3 gTrunkLocal = normalize(float3(pTrunk.x, 0, pTrunk.z)); 
    // Rotate gradient back to world space
    float3 gTrunk = gTrunkLocal;
    gTrunk.xz = rotate(gTrunkLocal.xz, angle);

    // --- 2. Leaf Canopy ---
    float3 pLeaves = pRot - float3(0, h * 0.85, 0);

    // Cheap Domain Warp (Single call instead of 3)
    // Displaces the lookup point to make the ellipsoid lumpy
    float warp = snoise(pLeaves * 0.05); 
    float3 pLeavesWarped = pLeaves + warp * 3.0;

    // Leaf Dimensions
    float3 leafSize = float3(16.0, 12.0, 16.0) * (h / 70.0);
    
    // Leaf SDF
    float dLeaves = sdEllipsoid(pLeavesWarped, leafSize);

    // Leaf Surface Detail (Single octave)
    // Again, added to distance, ignored for gradient
    // Use non-rotated p for global noise consistency (optional, but looks better if leaves "swim" slightly through noise space)
    // Actually, let's use rotated p so noise sticks to the tree
    float leafNoise = snoise(pRot * 0.15) * 1.2;
    dLeaves += leafNoise;

    // Leaf Gradient (Analytical gradient of an ellipsoid)
    // Gradient is p / r^2
    float3 gLeavesLocal = normalize(pLeavesWarped / (leafSize * leafSize));
    // Rotate gradient back to world space
    float3 gLeaves = gLeavesLocal;
    gLeaves.xz = rotate(gLeavesLocal.xz, angle);

    // --- 3. Blending ---
    float blendStrength = 4.0;
    // Calculate mix factor hBlend
    float hBlend = clamp(0.5 + 0.5 * (dLeaves - dTrunk) / blendStrength, 0.0, 1.0);
    
    // Blend Distance
    dist = lerp(dLeaves, dTrunk, hBlend) - blendStrength * hBlend * (1.0 - hBlend);
    
    // Blend Gradient (Fast approximation)
    grad = normalize(lerp(gLeaves, gTrunk, hBlend));

    // Material Logic
    if (dTrunk < dLeaves + 1.0) mat = MAT_LOG;
    else mat = MAT_LEAVES;
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

    float minD = 1e5;
    uint bestMat = 0;
    float3 bestGrad = float3(0,1,0);
    bool foundTree = false;

    // Search 3x3 neighbor cells
    // Optimization: Unroll or keep tight
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
                // Tree Parameters
                float2 offset = (float2(Hash2D(cellId + 1.13), Hash2D(cellId + 3.51)) * 0.6 + 0.2) * TREE_GRID_SIZE;
                float2 treeXZ = cellId * TREE_GRID_SIZE + offset;
                
                // OPTIMIZATION: Bounding Box Check
                // Max tree height ~105, Max leaf radius ~20
                // If we are far from this tree's column, SKIP IT.
                // This saves massive amounts of SDF evaluations.
                if (abs(ctx.position.x - treeXZ.x) > 25.0 || abs(ctx.position.z - treeXZ.y) > 25.0) continue;

                // Get Terrain Height
                float terrainH = GetHeight(treeXZ);
                float3 treeBase = float3(treeXZ.x, terrainH, treeXZ.y);
                
                // Vertical Bounds Check
                float treeHeight = 65.0 + h * 40.0;
                if (ctx.position.y < terrainH - 5.0 || ctx.position.y > terrainH + treeHeight + 20.0) continue;

                // Random Rotation (0 to 2*PI)
                float rotationAngle = Hash2D(cellId + 5.7) * 6.28318;

                // Calculate Tree Data
                float d;
                uint mat;
                float3 g;
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
        ctx.gradient = bestGrad; // Direct assignment, no finite difference!
    }
}

#endif
