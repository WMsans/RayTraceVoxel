using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Interfaces;

namespace VoxelEngine.Core.Editing
{
    public class VoxelModifier
    {
        private ComputeShader _shader;
        private IVoxelStorage _storage;

        // Structure matching the Compute Shader
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct ModifiedBrickInfo
        {
            public Vector3Int brickIdx;
            public uint brickPtr;
        }

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

            // 1. Check Scale Match
            // We can only edit volumes that match the global voxel size (LOD 0)
            float volVoxelSize = vol.WorldSize / vol.Resolution;
            float globalVoxelSize = VoxelEditManager.Instance.voxelSize;
            
            if (!Mathf.Approximately(volVoxelSize, globalVoxelSize))
            {
                // Debug.LogWarning($"[VoxelModifier] Skipped edit on LOD Chunk (Size: {volVoxelSize} vs Global: {globalVoxelSize})");
                return;
            }

            // 2. Check Alignment
            // The Volume must align to the Global Brick Grid for the keys to match
            float brickWorldSize = SVONode.BRICK_SIZE * globalVoxelSize;
            if (vol.WorldOrigin.x % brickWorldSize != 0 || 
                vol.WorldOrigin.y % brickWorldSize != 0 || 
                vol.WorldOrigin.z % brickWorldSize != 0)
            {
                // Allow for small floating point errors
                float epsilon = 0.01f;
                bool alignedX = Mathf.Abs(vol.WorldOrigin.x % brickWorldSize) < epsilon || Mathf.Abs(vol.WorldOrigin.x % brickWorldSize - brickWorldSize) < epsilon;
                bool alignedY = Mathf.Abs(vol.WorldOrigin.y % brickWorldSize) < epsilon || Mathf.Abs(vol.WorldOrigin.y % brickWorldSize - brickWorldSize) < epsilon;
                bool alignedZ = Mathf.Abs(vol.WorldOrigin.z % brickWorldSize) < epsilon || Mathf.Abs(vol.WorldOrigin.z % brickWorldSize - brickWorldSize) < epsilon;

                if (!alignedX || !alignedY || !alignedZ)
                {
                    // Debug.LogWarning($"[VoxelModifier] Skipped edit on Misaligned Chunk {vol.WorldOrigin}");
                    return;
                }
            }

            // Calculate Scale: Voxel Units / World Units
            float worldToVoxelScale = (float)vol.Resolution / vol.WorldSize;

            // This ensures minBrickId is Local, which the Shader and Readback logic expect.
            Vector3 localBrushPos = brush.position - vol.WorldOrigin;

            // Convert Brush to Voxel Space
            Vector3 brushPosVoxel = localBrushPos * worldToVoxelScale;
            float brushRadiusVoxel = brush.radius * worldToVoxelScale;
            Vector3 brushBoundsVoxel = brush.bounds * worldToVoxelScale;

            // 1. Calculate AABB of Brush in Voxel Grid Space
            Bounds aabb = new Bounds(brushPosVoxel, Vector3.zero);
            if (brush.shape == (int)BrushShape.Sphere)
                aabb.extents = Vector3.one * brushRadiusVoxel;
            else
                aabb.extents = brushBoundsVoxel * 0.5f;

            // 2. Determine Brick Indices (Bricks are 4x4x4 Voxels)
            float brickVoxelSize = SVONode.BRICK_SIZE; // 4.0
            
            Vector3 min = aabb.min;
            Vector3 max = aabb.max;

            // Clamp to Grid limits
            min = Vector3.Max(min, Vector3.zero);
            max = Vector3.Min(max, new Vector3(vol.Resolution, vol.Resolution, vol.Resolution));

            // 1. Prevent invalid execution if brush is effectively outside bounds or inverted
            if (min.x >= max.x || min.y >= max.y || min.z >= max.z) return;

            Vector3Int minBrickId = Vector3Int.FloorToInt(min / brickVoxelSize);
            Vector3Int maxBrickId = Vector3Int.FloorToInt((max - Vector3.one * 0.001f) / brickVoxelSize);

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

            // --- Phase 3: Bake Setup (Mod Log) ---
            int maxModBricks = rangeX * rangeY * rangeZ;
            ComputeBuffer appendBuffer = new ComputeBuffer(maxModBricks, 16, ComputeBufferType.Append);
            appendBuffer.SetCounterValue(0);
            _shader.SetBuffer(kernelEdit, "_OutModifiedBricks", appendBuffer);

            // 4. Dispatch
            _shader.Dispatch(kernelAlloc, Mathf.CeilToInt(rangeX / 8.0f), Mathf.CeilToInt(rangeY / 8.0f), Mathf.CeilToInt(rangeZ / 8.0f));
            _shader.Dispatch(kernelEdit, rangeX, rangeY, rangeZ);

            // --- Phase 3: Async Readback ---
            // We need a counter buffer to know how many bricks were modified
            ComputeBuffer countBuffer = new ComputeBuffer(1, 4, ComputeBufferType.IndirectArguments);
            ComputeBuffer.CopyCount(appendBuffer, countBuffer, 0);

            float eps = VoxelEditManager.Instance.voxelSize * 0.01f;
            Vector3Int volOriginBrick = VoxelEditManager.Instance.GetGlobalBrickIndex(vol.WorldOrigin + Vector3.one * eps);

            // 1. Request Count
            AsyncGPUReadback.Request(countBuffer, (reqCount) => 
            {
                if (reqCount.hasError) { countBuffer.Dispose(); appendBuffer.Dispose(); return; }
                
                int count = reqCount.GetData<int>()[0];

                if (count > 0)
                {
                    // 2. Request Modified Brick List
                    AsyncGPUReadback.Request(appendBuffer, count * 16, 0, (reqList) => 
                    {
                        if (!reqList.hasError)
                        {
                            var list = reqList.GetData<ModifiedBrickInfo>();
                            
                            // 3. Request Actual Brick Data
                            for (int i = 0; i < list.Length; i++)
                            {
                                ModifiedBrickInfo info = list[i];
                                
                                // Calculate Global Key: VolOrigin + LocalBrickIdx
                                // Since minBrickId was calculated from Local space, info.brickIdx is Local.
                                // Origin (Global) + Local = Global. This is now correct.
                                Vector3Int globalKey = volOriginBrick + info.brickIdx;
                                
                                // [FIX] Issue Readback for the specific slice (216 uints)
                                // We must OFFSET by the Volume's start in the Global Buffer
                                int globalBrickPtr = vol.BufferManager.BrickDataOffset + (int)info.brickPtr;
                                int byteOffset = globalBrickPtr * 4;
                                int byteSize = SVONode.BRICK_VOXEL_COUNT * 4;

                                // We must capture 'vol' but verify it's still valid
                                if (vol != null && vol.BrickDataBuffer != null)
                                {
                                    AsyncGPUReadback.Request(vol.BrickDataBuffer, byteSize, byteOffset, (reqData) => 
                                    {
                                        if (!reqData.hasError)
                                        {
                                            uint[] data = reqData.GetData<uint>().ToArray();
                                            VoxelEditManager.Instance.StoreBrick(globalKey, data);
                                        }
                                    });
                                }
                            }
                        }
                        
                        // Clean up
                        countBuffer.Dispose();
                        appendBuffer.Dispose();
                    });
                }
                else
                {
                    countBuffer.Dispose();
                    appendBuffer.Dispose();
                }
            });
        }
    }
}