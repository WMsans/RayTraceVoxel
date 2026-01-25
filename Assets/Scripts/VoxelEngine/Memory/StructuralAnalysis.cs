using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using VoxelEngine.Core.Data;

namespace VoxelEngine.Core.Structural
{
    [BurstCompile]
    public static class StructuralAnalysis
    {
        // Voxel Data Constants
        const int BRICK_SIZE = 4;
        const int PADDING = 1;
        const int STRIDE_X = 1;
        const int STRIDE_Y = 6;
        const int STRIDE_Z = 36;
        const float MAX_SDF_RANGE = 4.0f;

        /// <summary>
        /// Analyzes a raw brick (6x6x6) and calculates the internal connectivity mask.
        /// </summary>
        public static ulong CalculateConnectivity(uint[] brickData)
        {
            if (brickData == null || brickData.Length != 216) return 0;

            // Use Allocator.TempJob for short-lived allocations
            using (var nativeBrickData = new NativeArray<uint>(brickData, Allocator.TempJob))
            using (var result = new NativeReference<ulong>(Allocator.TempJob))
            {
                var job = new ConnectivityJob
                {
                    BrickData = nativeBrickData,
                    Result = result
                };

                // Execute immediately on the calling thread
                job.Run();

                return result.Value;
            }
        }

        [BurstCompile]
        struct ConnectivityJob : IJob
        {
            [ReadOnly] public NativeArray<uint> BrickData;
            public NativeReference<ulong> Result;

            public unsafe void Execute()
            {
                // 1. Unpack Solid State for the inner 4x4x4 core
                // Flattened size for 4x4x4 is 64
                // Stack allocation is fast and doesn't require disposal
                bool* isSolid = stackalloc bool[64];
                int* labels = stackalloc int[64];
                
                // Initialize labels to 0
                UnsafeUtility.MemClear(labels, 64 * sizeof(int));

                // Populate isSolid
                // Flattening the 3D loop for better performance / simplicity in unsafe context
                for (int z = 0; z < BRICK_SIZE; z++)
                {
                    int zOffsetCore = z * BRICK_SIZE * BRICK_SIZE;
                    int zOffsetRaw = (z + PADDING) * STRIDE_Z;
                    
                    for (int y = 0; y < BRICK_SIZE; y++)
                    {
                        int yOffsetCore = y * BRICK_SIZE;
                        int yOffsetRaw = (y + PADDING) * STRIDE_Y;
                        
                        for (int x = 0; x < BRICK_SIZE; x++)
                        {
                            int index = x + yOffsetCore + zOffsetCore;
                            int rawIndex = zOffsetRaw + yOffsetRaw + (x + PADDING) * STRIDE_X;
                            
                            isSolid[index] = IsVoxelSolid(BrickData[rawIndex]);
                        }
                    }
                }

                int currentLabel = 1;
                // Store face masks for each label. Max possible labels is 32 (checkerboard), 65 is safe.
                // Index is label ID. Mask is bitfield of StructuralFace (0..5).
                int* labelFaceMasks = stackalloc int[65];
                UnsafeUtility.MemClear(labelFaceMasks, 65 * sizeof(int));
                
                // Stack for flood fill (max 64 items)
                int* stackBuffer = stackalloc int[64];

                // 2. Component Labeling (Flood Fill)
                for (int i = 0; i < 64; i++)
                {
                    if (isSolid[i] && labels[i] == 0)
                    {
                        // New component
                        FloodFill(i, isSolid, labels, currentLabel, labelFaceMasks, stackBuffer);
                        currentLabel++;
                    }
                }

                // 3. Build Connectivity Mask
                ulong mask = 0;
                // Iterate over all found labels
                for (int l = 1; l < currentLabel; l++)
                {
                    int faces = labelFaceMasks[l];
                    if (faces == 0) continue;

                    // If a component touches Face A and Face B, then A <-> B is connected.
                    // We iterate all pairs of faces.
                    for (int f1 = 0; f1 < 6; f1++)
                    {
                        if ((faces & (1 << f1)) != 0)
                        {
                            for (int f2 = 0; f2 < 6; f2++)
                            {
                                if ((faces & (1 << f2)) != 0)
                                {
                                    int bit = f1 * 6 + f2;
                                    mask |= (1UL << bit);
                                }
                            }
                        }
                    }
                }

                Result.Value = mask;
            }

            private unsafe void FloodFill(int startIndex, bool* isSolid, int* labels, int label, int* labelFaceMasks, int* stackBuffer)
            {
                int stackCount = 0;
                stackBuffer[stackCount++] = startIndex;
                labels[startIndex] = label;

                while (stackCount > 0)
                {
                    int index = stackBuffer[--stackCount]; // Pop
                    
                    // Decode index to x,y,z
                    int z = index / 16;
                    int rem = index % 16;
                    int y = rem / 4;
                    int x = rem % 4;

                    // Check Boundary Touches and accumulate mask
                    int facesMask = 0;
                    if (x == 0) facesMask |= (1 << (int)StructuralFace.Left);
                    if (x == BRICK_SIZE - 1) facesMask |= (1 << (int)StructuralFace.Right);
                    if (y == 0) facesMask |= (1 << (int)StructuralFace.Down);
                    if (y == BRICK_SIZE - 1) facesMask |= (1 << (int)StructuralFace.Up);
                    if (z == 0) facesMask |= (1 << (int)StructuralFace.Back);
                    if (z == BRICK_SIZE - 1) facesMask |= (1 << (int)StructuralFace.Forward);

                    labelFaceMasks[label] |= facesMask;

                    // Neighbors (6-way)
                    // We manually check bounds and push neighbors
                    
                    // x+1 (Right)
                    if (x + 1 < BRICK_SIZE) CheckAndPush(index + 1, isSolid, labels, label, stackBuffer, ref stackCount);
                    // x-1 (Left)
                    if (x - 1 >= 0) CheckAndPush(index - 1, isSolid, labels, label, stackBuffer, ref stackCount);
                    
                    // y+1 (Up) - Offset +4
                    if (y + 1 < BRICK_SIZE) CheckAndPush(index + 4, isSolid, labels, label, stackBuffer, ref stackCount);
                    // y-1 (Down) - Offset -4
                    if (y - 1 >= 0) CheckAndPush(index - 4, isSolid, labels, label, stackBuffer, ref stackCount);
                    
                    // z+1 (Forward) - Offset +16
                    if (z + 1 < BRICK_SIZE) CheckAndPush(index + 16, isSolid, labels, label, stackBuffer, ref stackCount);
                    // z-1 (Back) - Offset -16
                    if (z - 1 >= 0) CheckAndPush(index - 16, isSolid, labels, label, stackBuffer, ref stackCount);
                }
            }

            private unsafe void CheckAndPush(int index, bool* isSolid, int* labels, int label, int* stackBuffer, ref int stackCount)
            {
                if (isSolid[index] && labels[index] == 0)
                {
                    labels[index] = label;
                    stackBuffer[stackCount++] = index;
                }
            }

            private bool IsVoxelSolid(uint data)
            {
                // Unpack SDF (Bits 8-15)
                uint sdfInt = (data >> 8) & 0xFF;
                float normalizedSDF = (sdfInt / 255.0f) * 2.0f - 1.0f;
                float sdf = normalizedSDF * MAX_SDF_RANGE;
                
                // Standard SDF: Negative is inside (solid), Positive is outside (air)
                return sdf <= 0.0f; 
            }
        }
    }
}