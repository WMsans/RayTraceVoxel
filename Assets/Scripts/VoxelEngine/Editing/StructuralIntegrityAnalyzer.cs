using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using System.Collections.Generic;
using VoxelEngine.Core;

namespace VoxelEngine.Core.Editing
{
    public class StructuralIntegrityAnalyzer : MonoBehaviour
    {
        public ComputeShader analysisShader;
        
        // Singleton or attached to Tool?
        // We'll assume it's attached or found by TerrainEditorTool
        
        public void Analyze(VoxelVolume volume)
        {
            if (analysisShader == null || volume == null || !volume.IsReady) return;

            int res = volume.Resolution;
            int totalVoxels = res * res * res;
            int bufferSize = Mathf.CeilToInt(totalVoxels / 32.0f);

            ComputeBuffer topologyBuffer = new ComputeBuffer(bufferSize, 4);
            // Clear buffer (0 = Air)
            uint[] clearData = new uint[bufferSize];
            topologyBuffer.SetData(clearData); // Or use a Clear kernel

            int kernel = analysisShader.FindKernel("ExtractTopology");

            // Set Buffers
            analysisShader.SetBuffer(kernel, "_GlobalNodeBuffer", volume.NodeBuffer);
            analysisShader.SetBuffer(kernel, "_GlobalPayloadBuffer", volume.PayloadBuffer);
            analysisShader.SetBuffer(kernel, "_GlobalBrickDataBuffer", volume.BrickDataBuffer);
            analysisShader.SetBuffer(kernel, "_PageTableBuffer", volume.BufferManager.PageTableBuffer);
            analysisShader.SetBuffer(kernel, "_TopologyBuffer", topologyBuffer);

            // Set Uniforms
            analysisShader.SetInt("_Resolution", res);
            analysisShader.SetInt("_PageTableOffset", volume.BufferManager.PageTableOffset);
            analysisShader.SetInt("_BrickOffset", volume.BufferManager.BrickDataOffset);

            // Dispatch
            int threadGroups = Mathf.CeilToInt(res / 8.0f);
            analysisShader.Dispatch(kernel, threadGroups, threadGroups, threadGroups);

            // Request Readback
            AsyncGPUReadback.Request(topologyBuffer, (request) => OnReadback(request, res, topologyBuffer, volume));
        }

        private void OnReadback(AsyncGPUReadbackRequest request, int resolution, ComputeBuffer bufferToRelease, VoxelVolume vol)
        {
            bufferToRelease.Release();

            if (request.hasError)
            {
                Debug.LogError("Structural Analysis Readback Failed");
                return;
            }

            using (NativeArray<uint> data = request.GetData<uint>())
            {
                PerformBFS(data, resolution, vol);
            }
        }

        private void PerformBFS(NativeArray<uint> packedData, int resolution, VoxelVolume vol)
        {
            // Unpack bitmask to verify connectivity
            // We want to find Connected Components.
            // 3D Array is flattened: z * res*res + y * res + x
            
            // Using a simple BitArray for 'Visited'
            int totalVoxels = resolution * resolution * resolution;
            System.Collections.BitArray visited = new System.Collections.BitArray(totalVoxels);
            
            List<int> componentSizes = new List<int>();
            
            // Helper to check if solid
            bool IsSolid(int idx)
            {
                return (packedData[idx / 32] & (1u << (idx % 32))) != 0;
            }

            // Neighbor Offsets
            int[] neighborOffsets = new int[] {
                1, -1,
                resolution, -resolution,
                resolution * resolution, -(resolution * resolution)
            };

            for (int i = 0; i < totalVoxels; i++)
            {
                if (!visited[i] && IsSolid(i))
                {
                    // Found a new component
                    int size = 0;
                    Queue<int> q = new Queue<int>();
                    q.Enqueue(i);
                    visited[i] = true;
                    
                    while (q.Count > 0)
                    {
                        int curr = q.Dequeue();
                        size++;
                        
                        // Check 6 neighbors
                        // Need to verify boundary checks to avoid wrapping!
                        // Decompose to x,y,z
                        int z = curr / (resolution * resolution);
                        int rem = curr % (resolution * resolution);
                        int y = rem / resolution;
                        int x = rem % resolution;

                        // Neighbors
                        // X+
                        if (x < resolution - 1) CheckNeighbor(curr + 1);
                        // X-
                        if (x > 0) CheckNeighbor(curr - 1);
                        // Y+
                        if (y < resolution - 1) CheckNeighbor(curr + resolution);
                        // Y-
                        if (y > 0) CheckNeighbor(curr - resolution);
                        // Z+
                        if (z < resolution - 1) CheckNeighbor(curr + resolution * resolution);
                        // Z-
                        if (z > 0) CheckNeighbor(curr - resolution * resolution);

                        void CheckNeighbor(int nIdx)
                        {
                            if (!visited[nIdx] && IsSolid(nIdx))
                            {
                                visited[nIdx] = true;
                                q.Enqueue(nIdx);
                            }
                        }
                    }
                    componentSizes.Add(size);
                }
            }
            Debug.Log(componentSizes.Count);
            // Report Results
            if (componentSizes.Count > 1)
            {
                componentSizes.Sort();
                string sizesStr = string.Join(", ", componentSizes);
                Debug.Log($"[Structural Analysis] Found {componentSizes.Count} disconnected components. Sizes: {sizesStr}. Smallest (Floating) Size: {componentSizes[0]}");
                
                // Here we would identify 'Floating' as anything that isn't the largest chunk (assuming Ground is largest)
                // Or check connectivity to y=0?
                // For now, just logging as requested: "determine if any voxels have been left floating"
                if (componentSizes.Count > 1)
                {
                   // Identify floating
                }
            }
            else if (componentSizes.Count == 1)
            {
                 // All connected
                 // Debug.Log("[Structural Analysis] Volume is fully connected.");
            }
        }
    }
}
