using System.Collections.Generic;
using UnityEngine;
using VoxelEngine.Core.Buffers;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Editing; 

namespace VoxelEngine.Core.Generators
{
    public static class SVOGenerator
    {
        // --- Helper Structs for Downsampling ---
        struct DownsampleTargetInfo
        {
            public uint targetBrickIdx; // Packed Local Coord of the Target (Low Res) Brick
            public int metaStartIndex;  // Index into _SourceBrickMeta
            public int sourceCount;     // How many source bricks contribute to this target
        }

        struct SourceBrickMeta
        {
            public uint relativeOffset; // Packed x,y,z (0..scale-1) offset within target
            public int dataIndex;       // Index into _SavedBrickData
        }
        // ---------------------------------------

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
            
            shader.SetBuffer(kernelBuild, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kernelBuild, "_PayloadBuffer", buffers.PayloadBuffer);
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
                // 1. Determine Scale (LOD Level)
                float chunkVoxelSize = chunkSize / resolution;
                float globalVoxelSize = editManager.voxelSize;
                
                // Scale = How many global voxels fit in one chunk voxel?
                int scale = Mathf.Max(1, Mathf.RoundToInt(chunkVoxelSize / globalVoxelSize));

                // 2. Retrieve Edits
                Bounds chunkBounds = new Bounds(chunkOrigin + Vector3.one * (chunkSize * 0.5f), Vector3.one * chunkSize);
                var edits = editManager.GetEditsInChunk(chunkBounds);
                
                if (edits != null && edits.Count > 0)
                {
                    // Calculate Global Brick Index of the Chunk Origin (LOD 0 coords)
                    Vector3Int chunkBaseIdx = editManager.GetGlobalBrickIndex(chunkOrigin);

                    if (scale == 1)
                    {
                        // --- 1:1 Scale (LOD 0) ---
                        // Standard application of edits
                        int editCount = edits.Count;
                        uint[] savedIndices = new uint[editCount];
                        uint[] savedData = new uint[editCount * 216];
                        
                        for (int i = 0; i < editCount; i++)
                        {
                            var kvp = edits[i];
                            Vector3Int localIdx = kvp.Key - chunkBaseIdx;
                            
                            // Pack Local Coordinate
                            uint packedLoc = (uint)((localIdx.x & 0xFF) | ((localIdx.y & 0xFF) << 8) | ((localIdx.z & 0xFF) << 16));
                            savedIndices[i] = packedLoc;
                            
                            if (kvp.Value.data != null)
                                System.Array.Copy(kvp.Value.data, 0, savedData, i * 216, 216);
                        }
                        
                        ComputeBuffer indicesBuffer = new ComputeBuffer(editCount, 4);
                        ComputeBuffer dataBuffer = new ComputeBuffer(savedData.Length, 4);
                        indicesBuffer.SetData(savedIndices);
                        dataBuffer.SetData(savedData);
                        
                        int kernelEdit = shader.FindKernel("ApplySavedEdits");
                        SetCommonEditBuffers(shader, kernelEdit, buffers);
                        shader.SetBuffer(kernelEdit, "_SavedBrickIndices", indicesBuffer);
                        shader.SetBuffer(kernelEdit, "_SavedBrickData", dataBuffer);
                        shader.SetInt("_SavedEditCount", editCount);
                        
                        int editGroups = Mathf.CeilToInt(editCount / 64.0f);
                        shader.Dispatch(kernelEdit, editGroups, 1, 1);
                        
                        indicesBuffer.Release();
                        dataBuffer.Release();
                    }
                    else
                    {
                        // --- Downsampling (LOD > 0) ---
                        // We must group multiple source bricks (LOD 0) into one target brick (LOD N)
                        
                        // Map: TargetBrickPacked -> List of Source Bricks
                        Dictionary<uint, List<SourceBrickMeta>> jobs = new Dictionary<uint, List<SourceBrickMeta>>();
                        
                        // We will flatten all data into one big array
                        List<uint> flattenedData = new List<uint>(edits.Count * 216);
                        
                        for (int i = 0; i < edits.Count; i++)
                        {
                            var kvp = edits[i];
                            // Position in LOD 0 units relative to chunk origin
                            Vector3Int localLOD0 = kvp.Key - chunkBaseIdx;
                            
                            // 1. Calculate Target Brick Index (Low Res)
                            // A chunk brick covers 'scale' source bricks
                            Vector3Int targetBrickIdx = new Vector3Int(
                                localLOD0.x / scale,
                                localLOD0.y / scale,
                                localLOD0.z / scale
                            );
                            
                            // 2. Calculate Offset within that Target Brick (0 .. scale-1)
                            Vector3Int relativeOffset = new Vector3Int(
                                localLOD0.x % scale,
                                localLOD0.y % scale,
                                localLOD0.z % scale
                            );
                            
                            // Pack Keys
                            uint targetPacked = (uint)((targetBrickIdx.x & 0xFF) | ((targetBrickIdx.y & 0xFF) << 8) | ((targetBrickIdx.z & 0xFF) << 16));
                            uint relativePacked = (uint)((relativeOffset.x & 0xFF) | ((relativeOffset.y & 0xFF) << 8) | ((relativeOffset.z & 0xFF) << 16));
                            
                            // Add Data
                            if (!jobs.ContainsKey(targetPacked))
                                jobs[targetPacked] = new List<SourceBrickMeta>();
                                
                            int dataPtr = flattenedData.Count; // Start index in the big array
                            if (kvp.Value.data != null)
                                flattenedData.AddRange(kvp.Value.data);
                            else
                                flattenedData.AddRange(new uint[216]); // Safety padding
                                
                            jobs[targetPacked].Add(new SourceBrickMeta 
                            { 
                                relativeOffset = relativePacked, 
                                dataIndex = dataPtr 
                            });
                        }
                        
                        // Flatten Structure for GPU
                        int jobCount = jobs.Count;
                        DownsampleTargetInfo[] targetArray = new DownsampleTargetInfo[jobCount];
                        List<SourceBrickMeta> metaList = new List<SourceBrickMeta>();
                        
                        int jobIdx = 0;
                        foreach (var kvp in jobs)
                        {
                            targetArray[jobIdx] = new DownsampleTargetInfo
                            {
                                targetBrickIdx = kvp.Key,
                                metaStartIndex = metaList.Count,
                                sourceCount = kvp.Value.Count
                            };
                            metaList.AddRange(kvp.Value);
                            jobIdx++;
                        }
                        
                        // Upload
                        if (jobCount > 0)
                        {
                            // 12 bytes stride
                            ComputeBuffer targetBuffer = new ComputeBuffer(jobCount, 12); 
                            // 8 bytes stride
                            ComputeBuffer metaBuffer = new ComputeBuffer(metaList.Count, 8); 
                            // 4 bytes stride
                            ComputeBuffer dataBuffer = new ComputeBuffer(flattenedData.Count, 4);
                            
                            targetBuffer.SetData(targetArray);
                            metaBuffer.SetData(metaList.ToArray());
                            dataBuffer.SetData(flattenedData.ToArray());
                            
                            int kernelDown = shader.FindKernel("ApplyDownsampledEdits"); // Future Kernel
                            if (kernelDown >= 0) // Only dispatch if kernel exists (once implemented)
                            {
                                SetCommonEditBuffers(shader, kernelDown, buffers);
                                shader.SetBuffer(kernelDown, "_DownsampleTargets", targetBuffer);
                                shader.SetBuffer(kernelDown, "_SourceBrickMeta", metaBuffer);
                                shader.SetBuffer(kernelDown, "_SavedBrickData", dataBuffer);
                                shader.SetInt("_DownsampleJobCount", jobCount);
                                shader.SetInt("_DownsampleScale", scale);
                                
                                shader.Dispatch(kernelDown, jobCount, 1, 1);
                            }
                            
                            targetBuffer.Release();
                            metaBuffer.Release();
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

        private static void SetCommonEditBuffers(ComputeShader shader, int kernel, SVOBufferManager buffers)
        {
            shader.SetBuffer(kernel, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kernel, "_PayloadBuffer", buffers.PayloadBuffer);
            shader.SetBuffer(kernel, "_BrickDataBuffer", buffers.BrickDataBuffer);
            shader.SetBuffer(kernel, "_CounterBuffer", buffers.CounterBuffer);
            
            shader.SetInt("_NodeOffset", buffers.NodeOffset);
            shader.SetInt("_PayloadOffset", buffers.PayloadOffset);
            shader.SetInt("_BrickOffset", buffers.BrickDataOffset);
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