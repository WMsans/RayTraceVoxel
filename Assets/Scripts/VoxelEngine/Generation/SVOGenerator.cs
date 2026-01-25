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

            // --- Prepare Edits ---
            var editManager = VoxelEditManager.Instance;
            GraphicsBuffer editInfoBuffer = null;
            GraphicsBuffer editVoxelBuffer = null;
            int editCount = 0;

            if (editManager != null)
            {
                Bounds chunkBounds = new Bounds(chunkOrigin + Vector3.one * (chunkSize * 0.5f), Vector3.one * chunkSize);
                var edits = editManager.GetEdits(chunkBounds);
                editCount = edits.Count;

                if (editCount > 0)
                {
                    editInfoBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, editCount, 16); // int4 (x,y,z, dataIdx)
                    editVoxelBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, editCount * 216, 4); // uint

                    var infoArray = new int[editCount * 4];
                    var voxelArray = new uint[editCount * 216];

                    for (int i = 0; i < editCount; i++)
                    {
                        var e = edits[i];
                        infoArray[i * 4 + 0] = e.Coordinate.x;
                        infoArray[i * 4 + 1] = e.Coordinate.y;
                        infoArray[i * 4 + 2] = e.Coordinate.z;
                        infoArray[i * 4 + 3] = i * 216; // Start index

                        System.Array.Copy(e.VoxelData, 0, voxelArray, i * 216, 216);
                    }

                    editInfoBuffer.SetData(infoArray);
                    editVoxelBuffer.SetData(voxelArray);
                }
            }

            // Bind Edit Buffers (or safe fallbacks if empty)
            shader.SetBuffer(kernelBuild, "_EditInfoBuffer", editCount > 0 ? editInfoBuffer : buffers.NodeBuffer);
            shader.SetBuffer(kernelBuild, "_EditVoxelBuffer", editCount > 0 ? editVoxelBuffer : buffers.NodeBuffer);
            shader.SetInt("_EditCount", editCount);
            shader.SetFloat("_GlobalVoxelSize", editManager != null ? editManager.voxelSize : 1.0f);
            shader.SetInt("_GlobalBrickSize", 4);

            int numBricksPerAxis = Mathf.CeilToInt(resolution / 4.0f);
            int threadGroups = Mathf.CeilToInt(numBricksPerAxis / 4.0f);
            
            shader.Dispatch(kernelBuild, threadGroups, threadGroups, threadGroups);

            // Cleanup Edit Buffers
            if (editInfoBuffer != null) editInfoBuffer.Release();
            if (editVoxelBuffer != null) editVoxelBuffer.Release();

            // --- Saved Edits Removed (Temp VRAM only) ---

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