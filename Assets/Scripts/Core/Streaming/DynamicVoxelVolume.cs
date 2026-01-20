using UnityEngine;
using System.Runtime.InteropServices;
using VoxelEngine.Core.Buffers;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Interfaces;

namespace VoxelEngine.Core.Streaming
{
    // A standalone Voxel Volume that owns its own memory (not part of the pool)
    public class DynamicVoxelVolume : MonoBehaviour, IVoxelStorage
    {
        public GraphicsBuffer NodeBuffer { get; private set; }
        public GraphicsBuffer PayloadBuffer { get; private set; }
        public GraphicsBuffer BrickDataBuffer { get; private set; }
        public GraphicsBuffer CounterBuffer { get; private set; }

        public int Resolution { get; private set; }
        public int MaxNodes => 4681; // Depth 3 full tree
        public int MaxBricks => 4096; // 16^3 bricks
        public bool IsReady => NodeBuffer != null;

        public void Initialize(int resolution, float worldSize)
        {
            Resolution = resolution;
            
            // Allocate private buffers
            NodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxNodes, Marshal.SizeOf<SVONode>());
            PayloadBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxNodes, Marshal.SizeOf<VoxelPayload>());
            BrickDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxBricks * 216, sizeof(uint));
            CounterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, sizeof(uint));
            
            // Register with Registry so Raytracer can find it (assuming Raytracer updated to support transforms)
            VoxelVolumeRegistry.RegisterVolumeLocal(this); 
        }

        public void BuildFromGrid(ComputeShader builder, ComputeBuffer sourceGrid, Vector3Int gridRes)
        {
            if (builder == null) return;
            
            // Reset Counters
            CounterBuffer.SetData(new uint[] { 0, 0, 0 });
            
            int kernelInit = builder.FindKernel("InitDenseStructure");
            builder.SetBuffer(kernelInit, "_NodeBuffer", NodeBuffer);
            builder.SetBuffer(kernelInit, "_CounterBuffer", CounterBuffer);
            builder.SetInt("_NodeOffset", 0);
            builder.Dispatch(kernelInit, 74, 1, 1);

            int kernelBuild = builder.FindKernel("BuildBricksFromGrid");
            builder.SetBuffer(kernelBuild, "_NodeBuffer", NodeBuffer);
            builder.SetBuffer(kernelBuild, "_PayloadBuffer", PayloadBuffer);
            builder.SetBuffer(kernelBuild, "_BrickDataBuffer", BrickDataBuffer);
            builder.SetBuffer(kernelBuild, "_CounterBuffer", CounterBuffer);
            builder.SetBuffer(kernelBuild, "_SourceGrid", sourceGrid);
            
            builder.SetInts("_GridResolution", new int[] { gridRes.x, gridRes.y, gridRes.z });
            builder.SetInt("_NodeOffset", 0);
            builder.SetInt("_PayloadOffset", 0);
            builder.SetInt("_BrickOffset", 0);
            builder.SetInt("_GridSize", Resolution);

            int groups = Mathf.CeilToInt(Resolution / 16.0f); // 4x4x4 threads per group handling bricks? No, 4x4x4 threads = 64 threads. 
            // Kernel is [4,4,4].
            int br = Resolution / 4;
            builder.Dispatch(kernelBuild, Mathf.CeilToInt(br/4.0f), Mathf.CeilToInt(br/4.0f), Mathf.CeilToInt(br/4.0f));
        }

        private void OnDestroy()
        {
            NodeBuffer?.Release();
            PayloadBuffer?.Release();
            BrickDataBuffer?.Release();
            CounterBuffer?.Release();
            VoxelVolumeRegistry.UnregisterVolumeLocal(this);
        }
    }
}