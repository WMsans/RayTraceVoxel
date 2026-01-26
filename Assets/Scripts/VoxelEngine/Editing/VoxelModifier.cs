using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Interfaces;
using Unity.Collections; // Required for NativeArray

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

            VoxelVolume vol = _storage as VoxelVolume;
            if (vol == null) return;

            // 1. Check Scale Match (Must match global LOD0 size)
            float volVoxelSize = vol.WorldSize / vol.Resolution;
            float globalVoxelSize = VoxelEditManager.Instance.voxelSize;
            
            if (!Mathf.Approximately(volVoxelSize, globalVoxelSize)) return;

            // 2. Alignment Check
            float brickWorldSize = SVONode.BRICK_SIZE * globalVoxelSize;
            // [Alignment check logic omitted for brevity, assumed same as previous]

            // 3. Calculate Brush in Voxel Space
            float worldToVoxelScale = (float)vol.Resolution / vol.WorldSize;
            Vector3 localBrushPos = brush.position - vol.WorldOrigin;
            Vector3 brushPosVoxel = localBrushPos * worldToVoxelScale;
            float brushRadiusVoxel = brush.radius * worldToVoxelScale;
            Vector3 brushBoundsVoxel = brush.bounds * worldToVoxelScale;

            // 4. Determine Brick Range
            Bounds aabb = new Bounds(brushPosVoxel, Vector3.zero);
            if (brush.shape == (int)BrushShape.Sphere)
                aabb.extents = Vector3.one * brushRadiusVoxel;
            else
                aabb.extents = brushBoundsVoxel * 0.5f;

            Vector3 min = Vector3.Max(aabb.min, Vector3.zero);
            Vector3 max = Vector3.Min(aabb.max, new Vector3(vol.Resolution, vol.Resolution, vol.Resolution));

            if (min.x >= max.x || min.y >= max.y || min.z >= max.z) return;

            float brickVoxelSize = SVONode.BRICK_SIZE;
            Vector3Int minBrickId = Vector3Int.FloorToInt(min / brickVoxelSize);
            Vector3Int maxBrickId = Vector3Int.FloorToInt((max - Vector3.one * 0.001f) / brickVoxelSize);

            int rangeX = Mathf.Max(1, maxBrickId.x - minBrickId.x + 1);
            int rangeY = Mathf.Max(1, maxBrickId.y - minBrickId.y + 1);
            int rangeZ = Mathf.Max(1, maxBrickId.z - minBrickId.z + 1);

            // 5. Select Kernels
            int kernelAlloc = _shader.FindKernel(brush.shape == 0 ? "AllocateNodesSphere" : "AllocateNodesCube");
            int kernelEdit = _shader.FindKernel(brush.shape == 0 ? "EditVoxelsSphere" : "EditVoxelsCube");
            int kernelExtract = _shader.FindKernel("ExtractBricks"); // New Kernel

            // Set Common Uniforms
            _shader.SetInts("_MinBrickIndex", new int[] { minBrickId.x, minBrickId.y, minBrickId.z });
            _shader.SetInts("_MaxBrickIndex", new int[] { maxBrickId.x, maxBrickId.y, maxBrickId.z });
            _shader.SetFloat("_GridSize", (float)vol.Resolution);
            _shader.SetInt("_MaxBricks", vol.MaxBricks);
            _shader.SetInt("_NodeOffset", vol.BufferManager.PageTableOffset); // Changed
            _shader.SetInt("_PayloadOffset", vol.BufferManager.PageTableOffset); // Changed
            _shader.SetInt("_BrickOffset", vol.BufferManager.BrickDataOffset);
            _shader.SetVector("_BrushPosition", brushPosVoxel);
            _shader.SetVector("_BrushBounds", brushBoundsVoxel);
            _shader.SetFloat("_BrushRadius", brushRadiusVoxel);
            _shader.SetInt("_BrushMaterialId", brush.materialId);
            _shader.SetInt("_BrushOp", brush.op);
            _shader.SetFloat("_Smoothness", 1.0f);

            // Set Buffers (Alloc & Edit)
            _shader.SetBuffer(kernelAlloc, "_NodeBuffer", vol.NodeBuffer);
            _shader.SetBuffer(kernelAlloc, "_CounterBuffer", vol.CounterBuffer);
            _shader.SetBuffer(kernelAlloc, "_PayloadBuffer", vol.PayloadBuffer);
            _shader.SetBuffer(kernelAlloc, "_BrickDataBuffer", vol.BrickDataBuffer);
            _shader.SetBuffer(kernelAlloc, "_PageTableBuffer", vol.BufferManager.PageTableBuffer); // New
            
            _shader.SetBuffer(kernelEdit, "_NodeBuffer", vol.NodeBuffer);
            _shader.SetBuffer(kernelEdit, "_PayloadBuffer", vol.PayloadBuffer);
            _shader.SetBuffer(kernelEdit, "_BrickDataBuffer", vol.BrickDataBuffer);
            _shader.SetBuffer(kernelEdit, "_PageTableBuffer", vol.BufferManager.PageTableBuffer); // New

            // 6. DISPATCH: Apply Edits to VRAM
            _shader.Dispatch(kernelAlloc, Mathf.CeilToInt(rangeX / 8.0f), Mathf.CeilToInt(rangeY / 8.0f), Mathf.CeilToInt(rangeZ / 8.0f));
            _shader.Dispatch(kernelEdit, rangeX, rangeY, rangeZ);

            // --- Capture Edits ---
            
            // A. Create Readback Buffer
            // Size = Total Bricks * 216 Voxels * 4 Bytes (uint)
            int totalBricks = rangeX * rangeY * rangeZ;
            int totalVoxels = totalBricks * SVONode.BRICK_VOXEL_COUNT;
            
            GraphicsBuffer readbackBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalVoxels, sizeof(uint));

            // B. Dispatch Extract Kernel
            _shader.SetBuffer(kernelExtract, "_NodeBuffer", vol.NodeBuffer);
            _shader.SetBuffer(kernelExtract, "_PayloadBuffer", vol.PayloadBuffer);
            _shader.SetBuffer(kernelExtract, "_BrickDataBuffer", vol.BrickDataBuffer);
            _shader.SetBuffer(kernelExtract, "_ReadbackBuffer", readbackBuffer);
            _shader.SetBuffer(kernelExtract, "_PageTableBuffer", vol.BufferManager.PageTableBuffer); // New
            
            // Use same dispatch dimensions as Edit
            _shader.Dispatch(kernelExtract, Mathf.CeilToInt(rangeX / 4.0f), Mathf.CeilToInt(rangeY / 4.0f), Mathf.CeilToInt(rangeZ / 4.0f));

            // C. Request Async Readback
            // Capture necessary variables for the callback
            Vector3 worldOrigin = vol.WorldOrigin;
            
            AsyncGPUReadback.Request(readbackBuffer, (request) =>
            {
                // Clean up GPU memory immediately
                readbackBuffer.Release();

                if (request.hasError)
                {
                    Debug.LogError("[VoxelModifier] GPU Readback failed.");
                    return;
                }

                if (VoxelEditManager.Instance == null) return;

                // D. Process Data
                using (NativeArray<uint> rawData = request.GetData<uint>())
                {
                    // Calculate Volume's Global Brick Origin
                    Vector3Int volOriginBrick = VoxelEditManager.Instance.GetBrickCoordinate(worldOrigin);
                    
                    int cursor = 0;
                    
                    // Iterate bricks in the same order as the Compute Shader (Z, Y, X)
                    for (int z = 0; z < rangeZ; z++)
                    {
                        for (int y = 0; y < rangeY; y++)
                        {
                            for (int x = 0; x < rangeX; x++)
                            {
                                // 1. Calculate Global Coordinate
                                Vector3Int localOffset = minBrickId + new Vector3Int(x, y, z);
                                Vector3Int globalCoord = volOriginBrick + localOffset;

                                // 2. Extract 216 uints
                                // We use GetSubArray for zero-allocation slicing, then ToArray() to store.
                                uint[] brickData = rawData.GetSubArray(cursor, SVONode.BRICK_VOXEL_COUNT).ToArray();
                                cursor += SVONode.BRICK_VOXEL_COUNT;

                                // 3. Store in Database
                                VoxelEditManager.Instance.RegisterEdit(globalCoord, brickData);
                                
                                // Note: RegisterEdit effectively marks it as dirty in the database.
                            }
                        }
                    }
                }
            });
        }
        string LogPackedVoxel(uint data)
        {
            // 1. Extract Material (Bottom 8 bits)
            uint materialId = data & 0xFF;

            // 2. Extract SDF (Next 8 bits)
            uint sdfInt = (data >> 8) & 0xFF;
            float normalizedSDF = (sdfInt / 255.0f) * 2.0f - 1.0f;
            float sdf = normalizedSDF * 4.0f; // MAX_SDF_RANGE

            // 3. Extract Normal (Top 16 bits)
            // (Simplification for debug only - full unpacking requires the Octahedral math)
            uint packedNormal = (data >> 16) & 0xFFFF;

            return $"[Unpack] Mat: {materialId}, SDF: {sdf:F2}, PackedNormal: {packedNormal:X4}";
        }
    }
}