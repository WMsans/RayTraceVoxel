using UnityEngine;
using VoxelEngine.Core.Buffers;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Editing; // Added for Phase 4

namespace VoxelEngine.Core.Generators
{
    public static class SVOGenerator
    {
        public static void Build(ComputeShader shader, SVOBufferManager buffers, int resolution, Vector3 chunkOrigin, float chunkSize, int depth)
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
            shader.SetInt("_BrickOffset", buffers.BrickDataOffset); // Changed

            shader.SetInt("_GridSize", resolution); 
            shader.SetVector("_ChunkWorldOrigin", chunkOrigin);
            shader.SetFloat("_ChunkWorldSize", chunkSize);
            shader.SetInt("_ChunkDepth", depth);

            int numBricksPerAxis = Mathf.CeilToInt(resolution / 4.0f);
            int threadGroups = Mathf.CeilToInt(numBricksPerAxis / 4.0f);
            
            shader.Dispatch(kernelBuild, threadGroups, threadGroups, threadGroups);

            // --- Phase 4: Apply Saved Edits ---
            var editManager = VoxelEditManager.Instance;
            if (editManager != null)
            {
                // 1. Determine Chunk Bounds (World Space)
                Bounds chunkBounds = new Bounds(chunkOrigin + Vector3.one * (chunkSize * 0.5f), Vector3.one * chunkSize);
                
                // 2. Retrieve Relevant Edits
                var edits = editManager.GetEditsInChunk(chunkBounds);
                
                if (edits != null && edits.Count > 0)
                {
                    int editCount = edits.Count;
                    
                    // 3. Prepare Arrays for Upload
                    // Index Buffer: 1 uint per edit (Packed Local Coordinate)
                    // Data Buffer: 216 uints per edit (Raw Voxel Data)
                    uint[] savedIndices = new uint[editCount];
                    uint[] savedData = new uint[editCount * 216];
                    
                    Vector3Int chunkBaseIdx = editManager.GetGlobalBrickIndex(chunkOrigin);
                    
                    for (int i = 0; i < editCount; i++)
                    {
                        var kvp = edits[i];
                        Vector3Int globalIdx = kvp.Key;
                        Vector3Int localIdx = globalIdx - chunkBaseIdx;
                        
                        // Pack Local Coordinate: x | y<<8 | z<<16
                        // Ensure bounds safety (0-255)
                        uint packedLoc = (uint)((localIdx.x & 0xFF) | ((localIdx.y & 0xFF) << 8) | ((localIdx.z & 0xFF) << 16));
                        savedIndices[i] = packedLoc;
                        
                        // Flatten Data
                        if (kvp.Value.data != null && kvp.Value.data.Length == 216)
                        {
                            System.Array.Copy(kvp.Value.data, 0, savedData, i * 216, 216);
                        }
                    }
                    
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
                    shader.SetInt("_ChunkDepth", depth);
                    
                    int editGroups = Mathf.CeilToInt(editCount / 64.0f);
                    shader.Dispatch(kernelEdit, editGroups, 1, 1);
                    
                    // 6. Cleanup
                    indicesBuffer.Release();
                    dataBuffer.Release();
                }
            }

            // --- Phase 4b: Stitch Brick Borders ---
            // Run this if we applied edits OR just generally to fix procedural seams (if wanted, but critical for edits)
            // It's safe to run always, but optimized to only run if we have neighbors.
            // For now, run always to ensure robustness.
            
            // Dispatch over Brick Grid (Resolution / 4)
            int numBricks = resolution / 4; // e.g. 64/4 = 16
            // Kernel size is [4, 4, 4], so groups needed = 16/4 = 4
            int stitchGroups = Mathf.CeilToInt(numBricks / 4.0f);
            
            int kernelStitch = shader.FindKernel("StitchBrickBorders");
            // Set Buffers (Node, Payload, BrickData are needed)
            shader.SetBuffer(kernelStitch, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kernelStitch, "_PayloadBuffer", buffers.PayloadBuffer);
            shader.SetBuffer(kernelStitch, "_BrickDataBuffer", buffers.BrickDataBuffer);
            
            shader.SetInt("_NodeOffset", buffers.NodeOffset);
            shader.SetInt("_PayloadOffset", buffers.PayloadOffset);
            shader.SetInt("_BrickOffset", buffers.BrickDataOffset);
            shader.SetInt("_GridSize", resolution);
            shader.SetInt("_ChunkDepth", depth);

            shader.Dispatch(kernelStitch, stitchGroups, stitchGroups, stitchGroups);
            // --------------------------------------

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