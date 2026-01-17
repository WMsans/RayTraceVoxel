using UnityEngine;
using VoxelEngine.Core.Buffers;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Editing; 
using System.Collections.Generic;

namespace VoxelEngine.Core.Generators
{
    public static class SVOGenerator
    {
        public static void Build(ComputeShader shader, SVOBufferManager buffers, int resolution, Vector3 chunkOrigin, float chunkSize)
        {
            if (shader == null || buffers == null) return;
            
            int kernelInit = shader.FindKernel("InitDenseStructure");
            shader.SetBuffer(kernelInit, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kernelInit, "_CounterBuffer", buffers.CounterBuffer);
            shader.SetInt("_NodeOffset", buffers.NodeOffset);
            
            shader.Dispatch(kernelInit, 74, 1, 1);

            int kernelBuild = shader.FindKernel("BuildBricks");
            var sdfManager = DynamicSDFManager.Instance;
            
            if (sdfManager != null && sdfManager.IsReady)
            {
                shader.SetInt("_NumDynamicObjects", sdfManager.ObjectCount);
                shader.SetBuffer(kernelBuild, "_SDFObjectBuffer", sdfManager.SDFObjectBuffer);
                shader.SetBuffer(kernelBuild, "_LBVHNodeBuffer", sdfManager.LBVHNodeBuffer);
                shader.SetBuffer(kernelBuild, "_SDFObjectIndexBuffer", sdfManager.ObjectIndexBuffer);
            }
            else
            {
                shader.SetInt("_NumDynamicObjects", 0);
            }
            
            // Removed SDFShapeManager usage

            shader.SetBuffer(kernelBuild, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kernelBuild, "_PayloadBuffer", buffers.PayloadBuffer);
            
            // Merged Buffer Binding
            shader.SetBuffer(kernelBuild, "_BrickDataBuffer", buffers.BrickDataBuffer);
            
            shader.SetBuffer(kernelBuild, "_CounterBuffer", buffers.CounterBuffer);
            
            shader.SetInt("_NodeOffset", buffers.NodeOffset);
            shader.SetInt("_PayloadOffset", buffers.PayloadOffset);
            shader.SetInt("_BrickOffset", buffers.BrickDataOffset); 

            shader.SetInt("_GridSize", resolution); 
            shader.SetVector("_ChunkWorldOrigin", chunkOrigin);
            shader.SetFloat("_ChunkWorldSize", chunkSize);

            int numBricksPerAxis = Mathf.CeilToInt(resolution / 4.0f);
            int threadGroups = Mathf.CeilToInt(numBricksPerAxis / 4.0f);
            
            shader.Dispatch(kernelBuild, threadGroups, threadGroups, threadGroups);

            // --- Phase 4: Apply Saved Edits ---
            var editManager = VoxelEditManager.Instance;
            if (editManager != null)
            {
                // [FIX] Check Voxel Size Match
                // Only apply edits if the current chunk's voxel size matches the global editing size.
                float currentVoxelSize = chunkSize / resolution;
                if (Mathf.Abs(currentVoxelSize - editManager.voxelSize) < 0.001f)
                {
                    // 1. Determine Chunk Bounds (World Space)
                    Bounds chunkBounds = new Bounds(chunkOrigin + Vector3.one * (chunkSize * 0.5f), Vector3.one * chunkSize);
                    
                    // 2. Retrieve Relevant Edits
                    var edits = editManager.GetEditsInChunk(chunkBounds);
                    
                    if (edits != null && edits.Count > 0)
                    {
                        // [FIX] Use a robust base index with epsilon to prevent floating point drift (e.g. 63.999 -> 63)
                        Vector3Int chunkBaseIdx = editManager.GetGlobalBrickIndex(chunkOrigin + Vector3.one * (editManager.voxelSize * 0.01f));
                        
                        // [FIX] Filter edits to ensure they are strictly within this chunk's index space
                        List<uint> validIndices = new List<uint>();
                        List<uint> validData = new List<uint>();

                        for (int i = 0; i < edits.Count; i++)
                        {
                            var kvp = edits[i];
                            Vector3Int globalIdx = kvp.Key;
                            Vector3Int localIdx = globalIdx - chunkBaseIdx;

                            // Bounds check: prevents wrapping artifacts if GetEditsInChunk returned a neighbor's brick
                            if (localIdx.x >= 0 && localIdx.x < numBricksPerAxis &&
                                localIdx.y >= 0 && localIdx.y < numBricksPerAxis &&
                                localIdx.z >= 0 && localIdx.z < numBricksPerAxis)
                            {
                                // Pack Local Coordinate: x | y<<8 | z<<16
                                uint packedLoc = (uint)((localIdx.x & 0xFF) | ((localIdx.y & 0xFF) << 8) | ((localIdx.z & 0xFF) << 16));
                                validIndices.Add(packedLoc);

                                if (kvp.Value.data != null && kvp.Value.data.Length == 216)
                                {
                                    validData.AddRange(kvp.Value.data);
                                }
                                else
                                {
                                    // Fallback for corrupt data (shouldn't happen)
                                    validData.AddRange(new uint[216]); 
                                }
                            }
                        }

                        int editCount = validIndices.Count;
                        
                        if (editCount > 0)
                        {
                            // 3. Prepare Arrays for Upload
                            uint[] savedIndices = validIndices.ToArray();
                            uint[] savedData = validData.ToArray();
                            
                            // 4. Create Temporary Buffers
                            ComputeBuffer indicesBuffer = new ComputeBuffer(editCount, 4);
                            ComputeBuffer dataBuffer = new ComputeBuffer(savedData.Length, 4);
                            
                            indicesBuffer.SetData(savedIndices);
                            dataBuffer.SetData(savedData);
                            
                            // 5. Dispatch Kernel
                            int kernelEdit = shader.FindKernel("ApplySavedEdits");
                            shader.SetBuffer(kernelEdit, "_NodeBuffer", buffers.NodeBuffer);
                            shader.SetBuffer(kernelEdit, "_PayloadBuffer", buffers.PayloadBuffer);
                            shader.SetBuffer(kernelEdit, "_BrickDataBuffer", buffers.BrickDataBuffer);
                            shader.SetBuffer(kernelEdit, "_CounterBuffer", buffers.CounterBuffer);
                            
                            shader.SetBuffer(kernelEdit, "_SavedBrickIndices", indicesBuffer);
                            shader.SetBuffer(kernelEdit, "_SavedBrickData", dataBuffer);
                            
                            shader.SetInt("_NodeOffset", buffers.NodeOffset);
                            shader.SetInt("_PayloadOffset", buffers.PayloadOffset);
                            shader.SetInt("_BrickOffset", buffers.BrickDataOffset);
                            shader.SetInt("_SavedEditCount", editCount);
                            
                            int editGroups = Mathf.CeilToInt(editCount / 64.0f);
                            shader.Dispatch(kernelEdit, editGroups, 1, 1);
                            
                            // 6. Cleanup
                            indicesBuffer.Release();
                            dataBuffer.Release();
                        }
                    }
                }
            }
            // ----------------------------------

            int kernelProp = shader.FindKernel("PropagateLOD");
            shader.SetBuffer(kernelProp, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetInt("_NodeOffset", buffers.NodeOffset); 

            DispatchLOD(shader, kernelProp, 73, 512);
            DispatchLOD(shader, kernelProp, 9, 64);
            DispatchLOD(shader, kernelProp, 1, 8);
            DispatchLOD(shader, kernelProp, 0, 1);
        }

        private static void DispatchLOD(ComputeShader shader, int kernel, int offset, int count)
        {
            shader.SetInt("_TargetLevelOffset", offset);
            shader.SetInt("_TargetLevelCount", count);
            int groups = Mathf.CeilToInt(count / 64.0f);
            shader.Dispatch(kernel, groups, 1, 1);
        }
    }
}