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

            // 1. Calculate AABB of Brush in Grid Space
            Bounds aabb = GetBrushAABB(brush);

            // 2. Determine Grid Indices (Bricks are 4x4x4)
            int brickSize = SVONode.BRICK_SIZE; // 4
            
            Vector3 min = aabb.min;
            Vector3 max = aabb.max;

            // Clamp to Grid size
            min = Vector3.Max(min, Vector3.zero);
            max = Vector3.Min(max, new Vector3(gridSize, gridSize, gridSize));

            Vector3Int minBrickId = Vector3Int.FloorToInt(min / brickSize);
            Vector3Int maxBrickId = Vector3Int.FloorToInt(max / brickSize);

            // Calculate Range
            int rangeX = Mathf.Max(1, maxBrickId.x - minBrickId.x + 1);
            int rangeY = Mathf.Max(1, maxBrickId.y - minBrickId.y + 1);
            int rangeZ = Mathf.Max(1, maxBrickId.z - minBrickId.z + 1);

            // 3. Setup Compute Shader kernels
            int kernelAlloc = _shader.FindKernel("AllocateNodes");
            int kernelEdit = _shader.FindKernel("EditVoxels");

            // Uniforms
            _shader.SetInts("_MinBrickIndex", new int[] { minBrickId.x, minBrickId.y, minBrickId.z });
            _shader.SetInts("_MaxBrickIndex", new int[] { maxBrickId.x, maxBrickId.y, maxBrickId.z });
            _shader.SetFloat("_GridSize", gridSize);
            _shader.SetInt("_MaxBricks", _storage.MaxBricks);

            // Brush Uniforms
            _shader.SetVector("_BrushPosition", brush.position);
            _shader.SetVector("_BrushBounds", brush.bounds);
            _shader.SetFloat("_BrushRadius", brush.radius);
            _shader.SetInt("_BrushMaterialId", brush.materialId);
            _shader.SetInt("_BrushShape", brush.shape);
            _shader.SetInt("_BrushOp", brush.op);
            _shader.SetFloat("_Smoothness", 1.0f); // Could be part of brush

            // Buffers - Allocate
            _shader.SetBuffer(kernelAlloc, "_NodeBuffer", _storage.NodeBuffer);
            _shader.SetBuffer(kernelAlloc, "_CounterBuffer", _storage.CounterBuffer);
            _shader.SetBuffer(kernelAlloc, "_PayloadBuffer", _storage.PayloadBuffer);
            _shader.SetBuffer(kernelAlloc, "_BrickBuffer", _storage.BrickBuffer);
            _shader.SetBuffer(kernelAlloc, "_BrickMaterialBuffer", _storage.BrickMaterialBuffer);
            
            // Buffers - Edit
            _shader.SetBuffer(kernelEdit, "_NodeBuffer", _storage.NodeBuffer);
            _shader.SetBuffer(kernelEdit, "_PayloadBuffer", _storage.PayloadBuffer);
            _shader.SetBuffer(kernelEdit, "_BrickBuffer", _storage.BrickBuffer);
            _shader.SetBuffer(kernelEdit, "_BrickMaterialBuffer", _storage.BrickMaterialBuffer);

            // 4. Dispatch AllocateNodes (8x8x8 threads per group -> 1 brick per thread)
            // We want 1 thread per brick.
            // Threads per group = 512 (8*8*8).
            // If range is e.g. 10x10x10 = 1000 bricks. We need 2 groups.
            int totalBricksX = rangeX;
            int totalBricksY = rangeY;
            int totalBricksZ = rangeZ;
            
            int groupsAllocX = Mathf.CeilToInt(totalBricksX / 8.0f);
            int groupsAllocY = Mathf.CeilToInt(totalBricksY / 8.0f);
            int groupsAllocZ = Mathf.CeilToInt(totalBricksZ / 8.0f);
            
            _shader.Dispatch(kernelAlloc, groupsAllocX, groupsAllocY, groupsAllocZ);

            // 5. Dispatch EditVoxels (4x4x4 threads per group -> 1 brick per GROUP)
            _shader.Dispatch(kernelEdit, rangeX, rangeY, rangeZ);
            
            // Debug.Log($"Applied Edit. Range: {rangeX}x{rangeY}x{rangeZ}");
        }

        private Bounds GetBrushAABB(VoxelBrush brush)
        {
            Bounds b = new Bounds(brush.position, Vector3.zero);
            if (brush.shape == (int)BrushShape.Sphere)
            {
                b.extents = new Vector3(brush.radius, brush.radius, brush.radius);
            }
            else // Cube/Plane
            {
                b.extents = brush.bounds * 0.5f;
            }
            return b;
        }
    }
}
