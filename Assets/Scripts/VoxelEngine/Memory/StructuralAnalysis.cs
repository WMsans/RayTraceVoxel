using UnityEngine;
using System.Collections.Generic;
using VoxelEngine.Core.Data;

namespace VoxelEngine.Core.Structural
{
    public static class StructuralAnalysis
    {
        // Voxel Data Constants from your files
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

            // 1. Unpack Solid State for the inner 4x4x4 core
            // We only care about voxels [1..4] in each dimension.
            bool[,,] isSolid = new bool[BRICK_SIZE, BRICK_SIZE, BRICK_SIZE];
            
            for (int z = 0; z < BRICK_SIZE; z++)
            {
                for (int y = 0; y < BRICK_SIZE; y++)
                {
                    for (int x = 0; x < BRICK_SIZE; x++)
                    {
                        // Map 0..3 to 1..4 (accounting for padding)
                        int rawIndex = (z + PADDING) * STRIDE_Z + (y + PADDING) * STRIDE_Y + (x + PADDING) * STRIDE_X;
                        isSolid[x, y, z] = IsVoxelSolid(brickData[rawIndex]);
                    }
                }
            }

            // 2. Component Labeling (Flood Fill)
            // Assign a Component ID to every solid voxel.
            int[,,] labels = new int[BRICK_SIZE, BRICK_SIZE, BRICK_SIZE];
            for (int i = 0; i < BRICK_SIZE; i++) 
                for (int j = 0; j < BRICK_SIZE; j++) 
                    for (int k = 0; k < BRICK_SIZE; k++) labels[i, j, k] = 0;

            int currentLabel = 1;
            Dictionary<int, HashSet<StructuralFace>> labelToFaces = new Dictionary<int, HashSet<StructuralFace>>();

            for (int z = 0; z < BRICK_SIZE; z++)
            {
                for (int y = 0; y < BRICK_SIZE; y++)
                {
                    for (int x = 0; x < BRICK_SIZE; x++)
                    {
                        if (isSolid[x, y, z] && labels[x, y, z] == 0)
                        {
                            // Found a new component, flood fill it
                            var touchedFaces = new HashSet<StructuralFace>();
                            FloodFill(x, y, z, isSolid, labels, currentLabel, touchedFaces);
                            labelToFaces[currentLabel] = touchedFaces;
                            currentLabel++;
                        }
                    }
                }
            }

            // 3. Build Connectivity Mask
            ulong mask = 0;
            foreach (var kvp in labelToFaces)
            {
                HashSet<StructuralFace> faces = kvp.Value;
                // If a component touches Face A and Face B, then A <-> B is connected.
                foreach (var f1 in faces)
                {
                    foreach (var f2 in faces)
                    {
                        int bit = (int)f1 * 6 + (int)f2;
                        mask |= (1UL << bit);
                    }
                }
            }

            return mask;
        }

        private static void FloodFill(int startX, int startY, int startZ, bool[,,] isSolid, int[,,] labels, int label, HashSet<StructuralFace> touchedFaces)
        {
            Stack<Vector3Int> stack = new Stack<Vector3Int>();
            stack.Push(new Vector3Int(startX, startY, startZ));
            labels[startX, startY, startZ] = label;

            while (stack.Count > 0)
            {
                Vector3Int p = stack.Pop();

                // Check Boundary Touches
                if (p.x == 0) touchedFaces.Add(StructuralFace.Left);
                if (p.x == BRICK_SIZE - 1) touchedFaces.Add(StructuralFace.Right);
                if (p.y == 0) touchedFaces.Add(StructuralFace.Down);
                if (p.y == BRICK_SIZE - 1) touchedFaces.Add(StructuralFace.Up);
                if (p.z == 0) touchedFaces.Add(StructuralFace.Back);
                if (p.z == BRICK_SIZE - 1) touchedFaces.Add(StructuralFace.Forward);

                // Neighbors (6-way)
                CheckAndPush(p.x + 1, p.y, p.z, stack, isSolid, labels, label);
                CheckAndPush(p.x - 1, p.y, p.z, stack, isSolid, labels, label);
                CheckAndPush(p.x, p.y + 1, p.z, stack, isSolid, labels, label);
                CheckAndPush(p.x, p.y - 1, p.z, stack, isSolid, labels, label);
                CheckAndPush(p.x, p.y, p.z + 1, stack, isSolid, labels, label);
                CheckAndPush(p.x, p.y, p.z - 1, stack, isSolid, labels, label);
            }
        }

        private static void CheckAndPush(int x, int y, int z, Stack<Vector3Int> stack, bool[,,] isSolid, int[,,] labels, int label)
        {
            if (x >= 0 && x < BRICK_SIZE && y >= 0 && y < BRICK_SIZE && z >= 0 && z < BRICK_SIZE)
            {
                if (isSolid[x, y, z] && labels[x, y, z] == 0)
                {
                    labels[x, y, z] = label;
                    stack.Push(new Vector3Int(x, y, z));
                }
            }
        }

        private static bool IsVoxelSolid(uint data)
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
