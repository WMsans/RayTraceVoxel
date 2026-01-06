using UnityEngine;
using VoxelEngine.Core.Buffers;

namespace VoxelEngine.Core.Generators
{
    public static class SVOGenerator
    {
        /// <summary>
        /// Builds the SVO using a procedural shape (Sphere) defined in the shader.
        /// </summary>
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
            
            Debug.Log($"SVO Generation Dispatched (Procedural). Grid: {resolution}");
        }

        /// <summary>
        /// Builds the SVO using a Dense SDF buffer (Phase 3).
        /// </summary>
        /// <param name="shader">The MeshToSVO.compute shader.</param>
        /// <param name="buffers">The SVO Buffers to populate.</param>
        /// <param name="resolution">Grid resolution.</param>
        /// <param name="sdfBuffer">Buffer containing the dense SDF floats.</param>
        /// <param name="materialId">The material ID to assign to solid voxels.</param>
        public static void BuildFromSDF(ComputeShader shader, SVOBufferManager buffers, int resolution, GraphicsBuffer sdfBuffer, int materialId)
        {
            if (shader == null || buffers == null || sdfBuffer == null)
            {
                Debug.LogError("SVOGenerator: Missing resources for BuildFromSDF.");
                return;
            }

            // 1. Initialize Node Structure
            int kernelInit = shader.FindKernel("InitDenseStructure");
            shader.SetBuffer(kernelInit, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kernelInit, "_CounterBuffer", buffers.CounterBuffer);
            // Dispatch enough threads for the node hierarchy (same as procedural)
            shader.Dispatch(kernelInit, 74, 1, 1);

            // 2. Build Bricks from SDF Data
            int kernelBuild = shader.FindKernel("BuildBricks");
            shader.SetBuffer(kernelBuild, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kernelBuild, "_PayloadBuffer", buffers.PayloadBuffer);
            shader.SetBuffer(kernelBuild, "_BrickBuffer", buffers.BrickBuffer);
            shader.SetBuffer(kernelBuild, "_BrickMaterialBuffer", buffers.BrickMaterialBuffer);
            shader.SetBuffer(kernelBuild, "_CounterBuffer", buffers.CounterBuffer);
            
            // New Inputs
            shader.SetBuffer(kernelBuild, "_DenseSDFBuffer", sdfBuffer);
            shader.SetInt("_TargetMaterialID", materialId);
            shader.SetInt("_GridSize", resolution);

            // Calculate Thread Groups (Bricks per axis / 8)
            int numBricksPerAxis = Mathf.CeilToInt(resolution / 4.0f);
            int threadGroups = Mathf.CeilToInt(numBricksPerAxis / 8.0f);

            shader.Dispatch(kernelBuild, threadGroups, threadGroups, threadGroups);

            Debug.Log($"SVO Generation Dispatched (From SDF). Grid: {resolution}, MatID: {materialId}");
        }
    }
}