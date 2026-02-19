using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

namespace VoxelEngine.Core.Streaming
{
    /// <summary>
    /// Blittable struct representing a node for Burst operations.
    /// </summary>
    public struct OctreeNodeStruct
    {
        public float3 center;
        public float size;
        public int depth;
        public bool isLeaf;
        
        // State tracking
        public bool isOccupied; // Is this slot in the array valid?
        
        // Helper for Culling
        public float3 extents => new float3(size * 0.5f);
    }

    /// <summary>
    /// Blittable Plane struct for Burst Frustum Culling.
    /// </summary>
    public struct BurstPlane
    {
        public float3 normal;
        public float distance;

        public static implicit operator BurstPlane(Plane p)
        {
            return new BurstPlane { normal = p.normal, distance = p.distance };
        }
    }

    [BurstCompile]
    public struct OctreeTraversalJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<OctreeNodeStruct> nodes;
        [ReadOnly] public NativeArray<BurstPlane> planes;
        
        public float3 viewerPos;
        public float shadowDistanceSq;
        public float splitFactor;
        public float mergeFactor;
        public int maxDepth;
        public bool cullEnabled;

        // Output Queues
        public NativeQueue<int>.ParallelWriter splitQueue;
        public NativeQueue<int>.ParallelWriter mergeQueue;
        public NativeList<int>.ParallelWriter visibleNodes;
        public NativeList<int>.ParallelWriter invisibleNodes;

        public void Execute(int index)
        {
            var node = nodes[index];
            if (!node.isOccupied) return;

            // 1. Frustum Culling
            bool isVisible = true;
            bool inShadowRange = false;
            float distSq = math.distancesq(node.center, viewerPos);

            if (cullEnabled)
            {
                isVisible = IsInFrustum(node, planes);
                if (!isVisible)
                {
                    // Check Shadow Distance
                    // Approx closest point check: simple sphere check is faster than AABB closest point
                    // but for large chunks AABB logic is safer.
                    // Fast Approx: Center dist - radius < shadowDist
                    float radius = node.size * 0.866f; // sqrt(3)/2 approx
                    float distToSphere = math.sqrt(distSq) - radius;
                    if (distToSphere < math.sqrt(shadowDistanceSq))
                    {
                        inShadowRange = true;
                    }
                }
            }

            bool effectivelyVisible = isVisible || inShadowRange;

            // 2. Output Visibility
            if (effectivelyVisible) visibleNodes.AddNoResize(index);
            else invisibleNodes.AddNoResize(index);

            // 3. LOD Logic
            // Split: If Leaf + Close + (Visible or Shadow) + Not Max Depth
            // Merge: If Not Leaf + (Far OR Not Visible)
            
            float size = node.size;
            
            if (node.isLeaf)
            {
                if (node.depth < maxDepth && effectivelyVisible)
                {
                    float splitDist = size * splitFactor;
                    if (distSq < splitDist * splitDist)
                    {
                        splitQueue.Enqueue(index);
                    }
                }
            }
            else // Branch
            {
                bool shouldMerge = false;
                
                // [FIX] Removed strict frustum culling merge. 
                // Previously: if (!effectivelyVisible) shouldMerge = true;
                // This caused chunks to unload immediately when looking away, causing lag when looking back.
                // Now we only merge based on distance, keeping invisible chunks in memory.

                float mergeDist = size * mergeFactor;
                if (distSq > mergeDist * mergeDist)
                {
                    shouldMerge = true;
                }

                if (shouldMerge)
                {
                    mergeQueue.Enqueue(index);
                }
            }
        }

        private bool IsInFrustum(OctreeNodeStruct node, NativeArray<BurstPlane> planes)
        {
            float3 center = node.center;
            float3 extents = new float3(node.size * 0.5f);

            for (int i = 0; i < planes.Length; i++)
            {
                BurstPlane p = planes[i];
                
                // Plane-AABB Intersection
                // r = dot(abs(normal), extents)
                // d = dot(normal, center) + distance
                // if (d + r < 0) -> Outside
                
                float3 absNormal = math.abs(p.normal);
                float r = math.dot(absNormal, extents);
                float d = math.dot(p.normal, center) + p.distance;

                if (d + r < 0) return false;
            }
            return true;
        }
    }
}