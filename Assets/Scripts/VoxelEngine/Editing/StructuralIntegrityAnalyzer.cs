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

        private List<Vector3> _floatingVoxelPositions = new List<Vector3>();
        private float _debugVoxelSize = 1.0f;

        private void PerformBFS(NativeArray<uint> packedData, int resolution, VoxelVolume vol)
        {
            if (vol == null) return;

            // Unpack bitmask to verify connectivity
            // We want to find Connected Components.
            // 3D Array is flattened: z * res*res + y * res + x
            
            // Using a simple BitArray for 'Visited'
            int totalVoxels = resolution * resolution * resolution;
            System.Collections.BitArray visited = new System.Collections.BitArray(totalVoxels);
            
            List<List<int>> components = new List<List<int>>();
            
            // Helper to check if solid
            bool IsSolid(int idx)
            {
                return (packedData[idx / 32] & (1u << (idx % 32))) != 0;
            }

            int totalSolid = 0;
            for(int i=0; i<totalVoxels; i++) { if(IsSolid(i)) totalSolid++; }
            Debug.Log($"[Structural Analysis] Total Solid Voxels: {totalSolid}");

            // Neighbor Offsets
            // Note: Boundary checks are handled inside the loop
            
            for (int i = 0; i < totalVoxels; i++)
            {
                if (!visited[i] && IsSolid(i))
                {
                    // Found a new component
                    List<int> currentComponent = new List<int>();
                    Queue<int> q = new Queue<int>();
                    
                    q.Enqueue(i);
                    visited[i] = true;
                    
                    while (q.Count > 0)
                    {
                        int curr = q.Dequeue();
                        currentComponent.Add(curr);
                        
                        // Check 6 neighbors
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
                    components.Add(currentComponent);
                }
            }
            
            _floatingVoxelPositions.Clear();

            // Report Results
            if (components.Count > 1)
            {
                // Sort by size descending (Largest is presumed 'ground')
                components.Sort((a, b) => b.Count.CompareTo(a.Count));
                
                Debug.Log($"[Structural Analysis] Found {components.Count} disconnected components. Largest: {components[0].Count}.");

                // Collect floating voxels (all components except the first/largest)
                _debugVoxelSize = vol.WorldSize / resolution;
                
                // Cache transform matrix to capture current state
                Matrix4x4 localToWorld = vol.transform.localToWorldMatrix;
                
                for (int c = 1; c < components.Count; c++)
                {
                    List<int> comp = components[c];
                    Debug.Log($"[Structural Analysis] Floating Component {c}: {comp.Count} voxels.");
                    
                    foreach (int idx in comp)
                    {
                        int z = idx / (resolution * resolution);
                        int rem = idx % (resolution * resolution);
                        int y = rem / resolution;
                        int x = rem % resolution;
                        
                        // Center of voxel in local space
                        Vector3 localPos = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
                        // Transform to world
                        Vector3 worldPos = localToWorld.MultiplyPoint3x4(localPos);
                        
                        _floatingVoxelPositions.Add(worldPos);
                    }
                }
            }
            else if (components.Count == 1)
            {
                 Debug.Log("[Structural Analysis] Volume is fully connected.");
            }
        }

        private void OnDrawGizmos()
        {
            if (_floatingVoxelPositions.Count > 0)
            {
                Gizmos.color = Color.red;
                Vector3 size = Vector3.one * _debugVoxelSize;
                foreach (var pos in _floatingVoxelPositions)
                {
                    Gizmos.DrawWireCube(pos, size);
                }
            }
        }
    }
}
