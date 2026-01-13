using UnityEngine;
using VoxelEngine.Core.Buffers;
using VoxelEngine.Core.Data;

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
            shader.SetInt("_BrickOffset", buffers.BrickDataOffset); // Changed

            shader.SetInt("_GridSize", resolution); 
            shader.SetVector("_ChunkWorldOrigin", chunkOrigin);
            shader.SetFloat("_ChunkWorldSize", chunkSize);

            int numBricksPerAxis = Mathf.CeilToInt(resolution / 4.0f);
            int threadGroups = Mathf.CeilToInt(numBricksPerAxis / 4.0f);
            
            shader.Dispatch(kernelBuild, threadGroups, threadGroups, threadGroups);

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