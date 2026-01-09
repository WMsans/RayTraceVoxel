using UnityEngine;
using VoxelEngine.Core.Buffers;

namespace VoxelEngine.Core.Generators
{
    public static class SVOGenerator
    {
        public static void Build(ComputeShader shader, SVOBufferManager buffers, int resolution, Vector3 chunkOrigin, float chunkSize)
        {
            if (shader == null || buffers == null) return;
            
            // 1. Init Structure
            int kernelInit = shader.FindKernel("InitDenseStructure");
            shader.SetBuffer(kernelInit, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kernelInit, "_CounterBuffer", buffers.CounterBuffer);
            shader.SetInt("_NodeOffset", buffers.NodeOffset); // Offset into Global Buffer
            
            shader.Dispatch(kernelInit, 74, 1, 1);

            // 2. Build Bricks
            int kernelBuild = shader.FindKernel("BuildBricks");
            shader.SetBuffer(kernelBuild, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kernelBuild, "_PayloadBuffer", buffers.PayloadBuffer);
            shader.SetBuffer(kernelBuild, "_BrickBuffer", buffers.BrickBuffer);
            shader.SetBuffer(kernelBuild, "_BrickMaterialBuffer", buffers.BrickMaterialBuffer);
            shader.SetBuffer(kernelBuild, "_CounterBuffer", buffers.CounterBuffer);
            
            // Offsets
            shader.SetInt("_NodeOffset", buffers.NodeOffset);
            shader.SetInt("_PayloadOffset", buffers.PayloadOffset);
            shader.SetInt("_BrickOffset", buffers.BrickOffset);

            shader.SetInt("_GridSize", resolution); 
            shader.SetVector("_ChunkWorldOrigin", chunkOrigin);
            shader.SetFloat("_ChunkWorldSize", chunkSize);

            int numBricksPerAxis = Mathf.CeilToInt(resolution / 4.0f);
            int threadGroups = Mathf.CeilToInt(numBricksPerAxis / 8.0f);
            
            shader.Dispatch(kernelBuild, threadGroups, threadGroups, threadGroups);
        }
    }
}