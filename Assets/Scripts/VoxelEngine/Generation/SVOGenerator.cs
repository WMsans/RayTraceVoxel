using UnityEngine;
using VoxelEngine.Core.Buffers;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Editing; 
using System.Collections.Generic;

namespace VoxelEngine.Core.Generators
{
    public static class SVOGenerator
    {
        private static int kInit = -1;
        private static int kBuild = -1;
        private static int kProp = -1;

        private static void EnsureKernels(ComputeShader shader)
        {
            if (kInit == -1)
            {
                kInit = shader.FindKernel("InitDenseStructure");
                kBuild = shader.FindKernel("BuildBricks");
                kProp = shader.FindKernel("PropagateLOD");
            }
        }

        public static void Build(ComputeShader shader, SVOBufferManager buffers, int resolution, Vector3 chunkOrigin, float chunkSize)
        {
            if (shader == null || buffers == null) return;
            EnsureKernels(shader);
            
            shader.SetBuffer(kInit, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kInit, "_CounterBuffer", buffers.CounterBuffer);
            shader.SetBuffer(kInit, "_PageTableBuffer", buffers.PageTableBuffer); 
            shader.SetInt("_NodeOffset", buffers.PageTableOffset); 
            
            shader.Dispatch(kInit, 74, 1, 1);

            var sdfManager = DynamicSDFManager.Instance;
            
            if (sdfManager != null && sdfManager.IsReady)
            {
                shader.SetInt("_NumDynamicObjects", sdfManager.ObjectCount);
                shader.SetBuffer(kBuild, "_SDFObjectBuffer", sdfManager.SDFObjectBuffer);
                shader.SetBuffer(kBuild, "_LBVHNodeBuffer", sdfManager.LBVHNodeBuffer);
                shader.SetBuffer(kBuild, "_SDFObjectIndexBuffer", sdfManager.ObjectIndexBuffer);
            }
            else
            {
                shader.SetInt("_NumDynamicObjects", 0);
            }
            
            shader.SetBuffer(kBuild, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kBuild, "_PayloadBuffer", buffers.PayloadBuffer);
            shader.SetBuffer(kBuild, "_BrickDataBuffer", buffers.BrickDataBuffer);
            shader.SetBuffer(kBuild, "_CounterBuffer", buffers.CounterBuffer);
            shader.SetBuffer(kBuild, "_PageTableBuffer", buffers.PageTableBuffer); 
            
            shader.SetInt("_NodeOffset", buffers.PageTableOffset); 
            shader.SetInt("_PayloadOffset", buffers.PageTableOffset); 
            shader.SetInt("_BrickOffset", buffers.BrickDataOffset); 

            shader.SetInt("_GridSize", resolution); 
            shader.SetVector("_ChunkWorldOrigin", chunkOrigin);
            shader.SetFloat("_ChunkWorldSize", chunkSize);

            // --- Prepare Edits ---
            var editManager = VoxelEditManager.Instance;
            int editCount = 0;
            float editVoxelSize = 1.0f;

            if (editManager != null)
            {
                // Calculate LOD Level based on current chunk resolution
                float currentVoxelSize = chunkSize / resolution;
                int lodLevel = Mathf.RoundToInt(Mathf.Log(currentVoxelSize / editManager.voxelSize, 2));
                lodLevel = Mathf.Max(0, lodLevel);
                
                // Effective voxel size for the edits we are retrieving
                editVoxelSize = editManager.voxelSize * Mathf.Pow(2, lodLevel);

                Bounds chunkBounds = new Bounds(chunkOrigin + Vector3.one * (chunkSize * 0.5f), Vector3.one * chunkSize);
                var edits = editManager.GetEdits(chunkBounds, lodLevel);
                editCount = edits.Count;

                if (editCount > 0)
                {
                    editManager.PrepareGPUBuffers(editCount);
                    var infoArray = editManager.InfoArray;
                    var voxelArray = editManager.VoxelArray;

                    for (int i = 0; i < editCount; i++)
                    {
                        var e = edits[i];
                        int infoBase = i * 4;
                        infoArray[infoBase + 0] = e.Coordinate.x;
                        infoArray[infoBase + 1] = e.Coordinate.y;
                        infoArray[infoBase + 2] = e.Coordinate.z;
                        infoArray[infoBase + 3] = i * SVONode.BRICK_VOXEL_COUNT;

                        System.Array.Copy(e.VoxelData, 0, voxelArray, i * SVONode.BRICK_VOXEL_COUNT, SVONode.BRICK_VOXEL_COUNT);
                    }

                    editManager.EditInfoBuffer.SetData(infoArray, 0, 0, editCount * 4);
                    editManager.EditVoxelBuffer.SetData(voxelArray, 0, 0, editCount * SVONode.BRICK_VOXEL_COUNT);
                }
            }

            // Bind Edit Buffers (or safe fallbacks if empty)
            shader.SetBuffer(kBuild, "_EditInfoBuffer", editCount > 0 ? editManager.EditInfoBuffer : buffers.NodeBuffer);
            shader.SetBuffer(kBuild, "_EditVoxelBuffer", editCount > 0 ? editManager.EditVoxelBuffer : buffers.NodeBuffer);
            shader.SetInt("_EditCount", editCount);
            shader.SetFloat("_GlobalVoxelSize", editVoxelSize);
            shader.SetInt("_GlobalBrickSize", 4);

            int numBricksPerAxis = Mathf.CeilToInt(resolution / 4.0f);
            int threadGroups = Mathf.CeilToInt(numBricksPerAxis / 4.0f);
            
            shader.Dispatch(kBuild, threadGroups, threadGroups, threadGroups);

            shader.SetBuffer(kProp, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kProp, "_PageTableBuffer", buffers.PageTableBuffer); 
            shader.SetInt("_NodeOffset", buffers.PageTableOffset); 

            DispatchLOD(shader, kProp, 73, 512);
            DispatchLOD(shader, kProp, 9, 64);
            DispatchLOD(shader, kProp, 1, 8);
            DispatchLOD(shader, kProp, 0, 1);
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