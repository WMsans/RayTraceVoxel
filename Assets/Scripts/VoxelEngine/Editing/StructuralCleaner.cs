using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine.Core;
using System.Collections.Generic;
using Unity.Collections;
using System.Linq;
using Unity.Mathematics;

namespace VoxelEngine.Core.Editing
{
    public class StructuralCleaner : MonoBehaviour
    {
        public ComputeShader voxelModifierShader;
        public StructuralIntegrityAnalyzer analyzer;

        private void Start()
        {
            if (analyzer != null)
                analyzer.OnAnalysisCompleted += HandleAnalysisCompleted;
        }

        private void OnDestroy()
        {
            if (analyzer != null)
                analyzer.OnAnalysisCompleted -= HandleAnalysisCompleted;
        }

        private void HandleAnalysisCompleted(VoxelVolume vol, List<Vector3> floatingVoxels)
        {
            if (floatingVoxels == null || floatingVoxels.Count == 0) return;
            if (voxelModifierShader == null || !vol.IsReady) return;

            // 1. Prepare Data
            float worldToVoxelScale = vol.Resolution / vol.WorldSize;
            
            List<Vector3> localVoxelPositions = new List<Vector3>(floatingVoxels.Count);
            HashSet<Vector3Int> uniqueBricks = new HashSet<Vector3Int>();

            foreach (var worldPos in floatingVoxels)
            {
                // Convert World -> Local Voxel Space
                Vector3 localPos = (worldPos - vol.WorldOrigin) * worldToVoxelScale;
                localVoxelPositions.Add(localPos);

                // Identify Bricks
                Vector3Int voxelIdx = Vector3Int.FloorToInt(localPos);
                Vector3Int brickIdx = new Vector3Int(voxelIdx.x / 4, voxelIdx.y / 4, voxelIdx.z / 4);
                uniqueBricks.Add(brickIdx);
            }

            int voxelCount = localVoxelPositions.Count;
            int brickCount = uniqueBricks.Count;

            if (voxelCount == 0) return;

            // 2. Setup Buffers
            ComputeBuffer positionsBuffer = new ComputeBuffer(voxelCount, 12); // float3
            positionsBuffer.SetData(localVoxelPositions.ToArray());

            ComputeBuffer bricksBuffer = new ComputeBuffer(brickCount, 12); // int3
            int3[] brickArray = uniqueBricks.Select(b => new int3(b.x, b.y, b.z)).ToArray();
            bricksBuffer.SetData(brickArray);

            // 3. Dispatch Allocation (Ensure bricks exist)
            int kernelAlloc = voxelModifierShader.FindKernel("AllocateNodesList");
            SetCommonBuffers(kernelAlloc, vol);
            voxelModifierShader.SetBuffer(kernelAlloc, "_TargetBricks", bricksBuffer);
            voxelModifierShader.SetInt("_TargetBrickCount", brickCount);
            
            int resBricks = vol.Resolution / 4;
            voxelModifierShader.SetInts("_MaxBrickIndex", new int[] {resBricks-1, resBricks-1, resBricks-1});
            voxelModifierShader.SetInts("_MinBrickIndex", new int[] {0, 0, 0});
             
            int groupsAlloc = Mathf.CeilToInt(brickCount / 64.0f);
            voxelModifierShader.Dispatch(kernelAlloc, groupsAlloc, 1, 1);

            // 4. Dispatch Removal
            int kernelRemove = voxelModifierShader.FindKernel("RemoveVoxelList");
            SetCommonBuffers(kernelRemove, vol);
            voxelModifierShader.SetBuffer(kernelRemove, "_TargetPositions", positionsBuffer);
            voxelModifierShader.SetInt("_TargetCount", voxelCount);
             
            int groupsRemove = Mathf.CeilToInt(voxelCount / 64.0f);
            voxelModifierShader.Dispatch(kernelRemove, groupsRemove, 1, 1);

            // 5. Dispatch Extraction (Persistence)
            int kernelExtract = voxelModifierShader.FindKernel("ExtractBricksList");
            SetCommonBuffers(kernelExtract, vol);
            voxelModifierShader.SetBuffer(kernelExtract, "_TargetBricks", bricksBuffer);
            voxelModifierShader.SetInt("_TargetBrickCount", brickCount);
            // Re-set MaxBrickIndex for safety in this kernel too
            voxelModifierShader.SetInts("_MaxBrickIndex", new int[] {resBricks-1, resBricks-1, resBricks-1});
            voxelModifierShader.SetInts("_MinBrickIndex", new int[] {0, 0, 0});

            int totalVoxelsToRead = brickCount * 216;
            GraphicsBuffer readbackBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalVoxelsToRead, 4);
            voxelModifierShader.SetBuffer(kernelExtract, "_ReadbackBuffer", readbackBuffer);

            voxelModifierShader.Dispatch(kernelExtract, groupsAlloc, 1, 1);

            // 6. Readback
            AsyncGPUReadback.Request(readbackBuffer, (req) => 
            {
                // Cleanup Buffers
                positionsBuffer.Release();
                bricksBuffer.Release();
                readbackBuffer.Release();

                if (req.hasError) 
                {
                    Debug.LogError("[StructuralCleaner] GPU Readback error");
                    return;
                }

                using (NativeArray<uint> data = req.GetData<uint>())
                {
                    ProcessReadbackData(data, vol, brickArray);
                }
            });
        }

        private void SetCommonBuffers(int kernel, VoxelVolume vol)
        {
            voxelModifierShader.SetBuffer(kernel, "_NodeBuffer", vol.NodeBuffer);
            voxelModifierShader.SetBuffer(kernel, "_PayloadBuffer", vol.PayloadBuffer);
            voxelModifierShader.SetBuffer(kernel, "_BrickDataBuffer", vol.BrickDataBuffer);
            voxelModifierShader.SetBuffer(kernel, "_CounterBuffer", vol.CounterBuffer);
            voxelModifierShader.SetBuffer(kernel, "_PageTableBuffer", vol.BufferManager.PageTableBuffer);
            voxelModifierShader.SetInt("_NodeOffset", vol.BufferManager.PageTableOffset);
            voxelModifierShader.SetInt("_PayloadOffset", vol.BufferManager.PageTableOffset);
            voxelModifierShader.SetInt("_BrickOffset", vol.BufferManager.BrickDataOffset);
            voxelModifierShader.SetInt("_MaxBricks", vol.MaxBricks);
        }

        private void ProcessReadbackData(NativeArray<uint> data, VoxelVolume vol, int3[] bricks)
        {
            if (VoxelEditManager.Instance == null) return;

            Vector3Int volOriginBrick = VoxelEditManager.Instance.GetBrickCoordinate(vol.WorldOrigin);

            int cursor = 0;
            for (int i = 0; i < bricks.Length; i++)
            {
                int3 b = bricks[i];
                Vector3Int localBrick = new Vector3Int(b.x, b.y, b.z);
                Vector3Int globalBrick = volOriginBrick + localBrick;

                if (cursor + 216 > data.Length) break;

                // Slice data for this brick
                uint[] brickData = data.GetSubArray(cursor, 216).ToArray();
                cursor += 216;

                VoxelEditManager.Instance.RegisterEdit(globalBrick, brickData);
            }
            
            Debug.Log($"[StructuralCleaner] Successfully removed {bricks.Length} floating bricks.");
        }
    }
}
