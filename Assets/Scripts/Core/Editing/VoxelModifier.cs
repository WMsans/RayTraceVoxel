using UnityEngine;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Interfaces;

namespace VoxelEngine.Core.Editing
{
    public class VoxelModifier
    {
        private ComputeShader _shader;
        private IVoxelStorage _storage;

        public VoxelModifier(ComputeShader shader, IVoxelStorage storage)
        {
            _shader = shader;
            _storage = storage;
        }

        public void Apply(VoxelBrush brush, float gridSize)
        {
            if (_shader == null || _storage == null || !_storage.IsReady) return;

            // [FIX] Get VoxelVolume specific data (Offsets & Scale)
            VoxelVolume vol = _storage as VoxelVolume;
            if (vol == null) return;

            // Calculate Scale: Voxel Units / World Units
            // If WorldSize=1024, Res=64 -> Scale = 0.0625. (1 Meter = 0.0625 Voxels)
            // Wait, standard SVO: Res=64, Size=1024. 
            // 1 Voxel = 16 Meters.
            // If Brush is at 16m, it is at Voxel 1.
            // So VoxelPos = WorldPos * (Res / Size).
            float worldToVoxelScale = (float)vol.Resolution / vol.WorldSize;

            // Convert Brush to Voxel Space
            Vector3 brushPosVoxel = brush.position * worldToVoxelScale;
            float brushRadiusVoxel = brush.radius * worldToVoxelScale;
            Vector3 brushBoundsVoxel = brush.bounds * worldToVoxelScale;

            // 1. Calculate AABB of Brush in Voxel Grid Space
            Bounds aabb = new Bounds(brushPosVoxel, Vector3.zero);
            if (brush.shape == (int)BrushShape.Sphere)
                aabb.extents = Vector3.one * brushRadiusVoxel;
            else
                aabb.extents = brushBoundsVoxel * 0.5f;

            // 2. Determine Brick Indices (Bricks are 4x4x4 Voxels)
            float brickSize = SVONode.BRICK_SIZE; // 4.0
            
            Vector3 min = aabb.min;
            Vector3 max = aabb.max;

            // Clamp to Grid limits
            min = Vector3.Max(min, Vector3.zero);
            max = Vector3.Min(max, new Vector3(vol.Resolution, vol.Resolution, vol.Resolution));

            // 1. Prevent invalid execution if brush is effectively outside bounds or inverted
            if (min.x >= max.x || min.y >= max.y || min.z >= max.z) return;

            Vector3Int minBrickId = Vector3Int.FloorToInt(min / brickSize);
            
            // 2. Fix: Subtract epsilon from max to treat it as an exclusive upper bound.
            // If max is 64.0, (64.0 - eps) / 4 = 15.99 -> Index 15.
            // This prevents Index 16, which wraps to 0 in the shader's Morton encoding.
            Vector3Int maxBrickId = Vector3Int.FloorToInt((max - Vector3.one * 0.001f) / brickSize);

            // Calculate Dispatch Range
            int rangeX = Mathf.Max(1, maxBrickId.x - minBrickId.x + 1);
            int rangeY = Mathf.Max(1, maxBrickId.y - minBrickId.y + 1);
            int rangeZ = Mathf.Max(1, maxBrickId.z - minBrickId.z + 1);

            // 3. Select Kernels
            int kernelAlloc;
            int kernelEdit;
            
            if (brush.shape == (int)BrushShape.Sphere)
            {
                kernelAlloc = _shader.FindKernel("AllocateNodesSphere");
                kernelEdit = _shader.FindKernel("EditVoxelsSphere");
            }
            else
            {
                kernelAlloc = _shader.FindKernel("AllocateNodesCube");
                kernelEdit = _shader.FindKernel("EditVoxelsCube");
            }

            // Uniforms
            _shader.SetInts("_MinBrickIndex", new int[] { minBrickId.x, minBrickId.y, minBrickId.z });
            _shader.SetInts("_MaxBrickIndex", new int[] { maxBrickId.x, maxBrickId.y, maxBrickId.z });
            _shader.SetFloat("_GridSize", (float)vol.Resolution);
            _shader.SetInt("_MaxBricks", vol.MaxBricks);

            // [FIX] Pass Offsets
            _shader.SetInt("_NodeOffset", vol.BufferManager.NodeOffset);
            _shader.SetInt("_PayloadOffset", vol.BufferManager.PayloadOffset);
            _shader.SetInt("_BrickOffset", vol.BufferManager.BrickDataOffset);

            // Brush Uniforms (Converted to Voxel Space)
            _shader.SetVector("_BrushPosition", brushPosVoxel);
            _shader.SetVector("_BrushBounds", brushBoundsVoxel);
            _shader.SetFloat("_BrushRadius", brushRadiusVoxel);
            _shader.SetInt("_BrushMaterialId", brush.materialId);
            _shader.SetInt("_BrushOp", brush.op);
            _shader.SetFloat("_Smoothness", 1.0f);

            // Buffers
            _shader.SetBuffer(kernelAlloc, "_NodeBuffer", vol.NodeBuffer);
            _shader.SetBuffer(kernelAlloc, "_CounterBuffer", vol.CounterBuffer);
            _shader.SetBuffer(kernelAlloc, "_PayloadBuffer", vol.PayloadBuffer);
            _shader.SetBuffer(kernelAlloc, "_BrickDataBuffer", vol.BrickDataBuffer);
            
            _shader.SetBuffer(kernelEdit, "_NodeBuffer", vol.NodeBuffer);
            _shader.SetBuffer(kernelEdit, "_PayloadBuffer", vol.PayloadBuffer);
            _shader.SetBuffer(kernelEdit, "_BrickDataBuffer", vol.BrickDataBuffer);

            // 4. Dispatch
            // Allocate: 1 thread per brick (8x8x8 group size -> 512 threads)
            // We need enough groups to cover [rangeX * rangeY * rangeZ] bricks? 
            // Actually the kernel likely expects 3D dispatch logic.
            // AllocateNodesSphere is currently empty, but for safety dispatch matching layout:
            _shader.Dispatch(kernelAlloc, Mathf.CeilToInt(rangeX / 8.0f), Mathf.CeilToInt(rangeY / 8.0f), Mathf.CeilToInt(rangeZ / 8.0f));

            // Edit: 1 group per brick? Or logic inside handles it?
            // Kernel is [numthreads(4, 4, 4)]. This is 64 threads.
            // The logic inside uses `_MinBrickIndex + id`.
            // So we dispatch the number of bricks we want to cover directly.
            _shader.Dispatch(kernelEdit, rangeX, rangeY, rangeZ);
        }
    }
}