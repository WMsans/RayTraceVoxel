using UnityEngine;
using VoxelEngine.Core.Buffers;

namespace VoxelEngine.Physics
{
    public static class PhysicsGenerator
    {
        private static int kGenerate = -1;

        /// <summary>
        /// Dispatches the PhysicsGen compute shader to generate a simplified mesh via Marching Cubes.
        /// </summary>
        /// <param name="shader">The PhysicsGen.compute shader.</param>
        /// <param name="buffers">The SVO buffer manager containing voxel data.</param>
        /// <param name="vertexOutput">An AppendStructuredBuffer<Vector3> to store generated vertices.</param>
        /// <param name="countBuffer">A ComputeBuffer (type IndirectArguments or Raw) to copy the vertex count into. Must be at least 4 bytes.</param>
        /// <param name="resolution">The resolution of the voxel chunk (e.g. 64).</param>
        /// <param name="stride">The sampling stride (e.g. 2 or 4).</param>
        /// <param name="chunkOrigin">World space origin of the chunk.</param>
        /// <param name="chunkSize">World space size of the chunk.</param>
        public static void Generate(ComputeShader shader, SVOBufferManager buffers, ComputeBuffer vertexOutput, ComputeBuffer countBuffer, int resolution, int stride, Vector3 chunkOrigin, float chunkSize, ComputeBuffer edgeTable, ComputeBuffer triTable)
        {
            if (shader == null || buffers == null || vertexOutput == null) return;

            if (kGenerate == -1) kGenerate = shader.FindKernel("GeneratePhysicsMesh");

            // Reset the append buffer counter to 0 before generation
            vertexOutput.SetCounterValue(0);

            // Bind SVO Buffers
            shader.SetBuffer(kGenerate, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kGenerate, "_PayloadBuffer", buffers.PayloadBuffer);
            shader.SetBuffer(kGenerate, "_BrickDataBuffer", buffers.BrickDataBuffer);
            shader.SetBuffer(kGenerate, "_PageTableBuffer", buffers.PageTableBuffer);
            
            // Bind Tables
            shader.SetBuffer(kGenerate, "_EdgeTable", edgeTable);
            shader.SetBuffer(kGenerate, "_TriTable", triTable);

            // Bind Output
            shader.SetBuffer(kGenerate, "_VertexOutput", vertexOutput);

            // Bind Uniforms
            shader.SetInt("_Stride", stride);
            shader.SetInt("_GridSize", resolution);
            shader.SetVector("_ChunkWorldOrigin", chunkOrigin);
            shader.SetFloat("_ChunkWorldSize", chunkSize);

            shader.SetInt("_NodeOffset", buffers.PageTableOffset);
            shader.SetInt("_PayloadOffset", buffers.PageTableOffset);
            shader.SetInt("_BrickOffset", buffers.BrickDataOffset);

            // Calculate Dispatch Groups
            // The compute shader uses [numthreads(8, 8, 8)]
            // We process a grid of size (resolution / stride)
            int dim = Mathf.CeilToInt(resolution / (float)stride);
            int groups = Mathf.CeilToInt(dim / 8.0f);
            
            shader.Dispatch(kGenerate, groups, groups, groups);

            // Copy the AppendBuffer count to the countBuffer
            // This allows the CPU to read the count later (or use it in indirect draw)
            if (countBuffer != null)
            {
                ComputeBuffer.CopyCount(vertexOutput, countBuffer, 0);
            }
        }
    }
}
