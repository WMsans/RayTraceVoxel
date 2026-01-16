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

            // Calculate Scale: Voxel Units / World Units
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

            // Calculate Volume Global Origin in Brick Coordinates (for Global indexing)
            Vector3Int volOriginBrick = VoxelEditManager.Instance.GetGlobalBrickIndex(vol.WorldOrigin);

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
                                Vector3Int globalKey = volOriginBrick + info.brickIdx;
                                
                                // Issue Readback for the specific slice (216 uints)
                                int byteOffset = (int)info.brickPtr * 4;
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