using UnityEngine;
using VoxelEngine.Core.Buffers;

namespace VoxelEngine.Core.Generators
{
    public static class SVOGenerator
    {
        public static void Build(ComputeShader shader, SVOBufferManager buffers, int resolution)
        {
            if (shader == null || buffers == null) return;
            
            int kernelInit = shader.FindKernel("InitDenseStructure");
            shader.SetBuffer(kernelInit, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kernelInit, "_CounterBuffer", buffers.CounterBuffer);
            shader.Dispatch(kernelInit, 74, 1, 1);

            int kernelBuild = shader.FindKernel("BuildBricks");
            shader.SetBuffer(kernelBuild, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kernelBuild, "_PayloadBuffer", buffers.PayloadBuffer);
            shader.SetBuffer(kernelBuild, "_BrickBuffer", buffers.BrickBuffer);
            shader.SetBuffer(kernelBuild, "_BrickMaterialBuffer", buffers.BrickMaterialBuffer);
            shader.SetBuffer(kernelBuild, "_CounterBuffer", buffers.CounterBuffer);
            shader.SetInt("_GridSize", resolution); 
            
            int numBricksPerAxis = Mathf.CeilToInt(resolution / 4.0f);
            int threadGroups = Mathf.CeilToInt(numBricksPerAxis / 8.0f);
            
            shader.Dispatch(kernelBuild, threadGroups, threadGroups, threadGroups);
            
            Debug.Log($"SVO Generation Dispatched. Grid: {resolution}");
        }
    }
}
