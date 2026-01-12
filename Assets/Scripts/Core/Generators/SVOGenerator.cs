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
            
            // 1. Init Structure
            int kernelInit = shader.FindKernel("InitDenseStructure");
            shader.SetBuffer(kernelInit, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kernelInit, "_CounterBuffer", buffers.CounterBuffer);
            shader.SetInt("_NodeOffset", buffers.NodeOffset);
            
            shader.Dispatch(kernelInit, 74, 1, 1);

            // 2. Build Bricks
            int kernelBuild = shader.FindKernel("BuildBricks");
            
            // --- NEW: Bind Dynamic SDF Buffers ---
            var sdfManager = DynamicSDFManager.Instance;
            
            // Fix: Explicitly handle the count. If manager isn't ready, pass 0 to disable the loop in shader.
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
            
            // --- NEW: Bind SDF Atlas ---
            var shapeManager = SDFShapeManager.Instance;
            if (shapeManager != null && shapeManager.sdfAtlas != null)
            {
                shader.SetTexture(kernelBuild, "_SDFAtlas", shapeManager.sdfAtlas);
                // Params: x=Res, y=TotalDepth, z=ShapeCount
                shader.SetVector("_SDFAtlasParams", new Vector4(
                    shapeManager.targetResolution, 
                    shapeManager.sdfAtlas.depth, 
                    shapeManager.shapes.Count, 
                    0));
            }

            // Standard Bindings
            shader.SetBuffer(kernelBuild, "_NodeBuffer", buffers.NodeBuffer);
            shader.SetBuffer(kernelBuild, "_PayloadBuffer", buffers.PayloadBuffer);
            shader.SetBuffer(kernelBuild, "_BrickBuffer", buffers.BrickBuffer);
            shader.SetBuffer(kernelBuild, "_BrickMaterialBuffer", buffers.BrickMaterialBuffer);
            shader.SetBuffer(kernelBuild, "_BrickNormalBuffer", buffers.BrickNormalBuffer);
            shader.SetBuffer(kernelBuild, "_CounterBuffer", buffers.CounterBuffer);
            
            shader.SetInt("_NodeOffset", buffers.NodeOffset);
            shader.SetInt("_PayloadOffset", buffers.PayloadOffset);
            shader.SetInt("_BrickOffset", buffers.BrickOffset);

            shader.SetInt("_GridSize", resolution); 
            shader.SetVector("_ChunkWorldOrigin", chunkOrigin);
            shader.SetFloat("_ChunkWorldSize", chunkSize);

            int numBricksPerAxis = Mathf.CeilToInt(resolution / 4.0f);
            int threadGroups = Mathf.CeilToInt(numBricksPerAxis / 4.0f);
            
            shader.Dispatch(kernelBuild, threadGroups, threadGroups, threadGroups);

            // 3. Propagate LOD
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