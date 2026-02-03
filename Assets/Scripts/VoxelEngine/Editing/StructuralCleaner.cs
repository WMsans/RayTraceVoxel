using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine.Core;
using VoxelEngine.Core.Streaming;
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

        [Header("Settings")]
        [Tooltip("If true, removes neighbors of floating voxels to ensure clean breaks and remove diagonal artifacts.")]
        public bool erodeFloatingVoxels = true;

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

            // 1. Calculate Bounds
            Vector3 minBounds = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 maxBounds = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i < floatingVoxels.Count; i++)
            {
                minBounds = Vector3.Min(minBounds, floatingVoxels[i]);
                maxBounds = Vector3.Max(maxBounds, floatingVoxels[i]);
            }

            // 2. Determine Center & Size
            Vector3 boundsCenter = (minBounds + maxBounds) * 0.5f;
            Vector3 rawSize = maxBounds - minBounds;
            
            float maxDimension = Mathf.Max(rawSize.x, Mathf.Max(rawSize.y, rawSize.z));
            float voxelSize = vol.WorldSize / vol.Resolution;

            int requiredResolution = Mathf.CeilToInt(maxDimension / voxelSize) + 2;
            int debrisResolution = Mathf.NextPowerOfTwo(Mathf.Max(requiredResolution, 16));

            float debrisWorldSize = debrisResolution * voxelSize;
            Vector3 debrisOrigin = boundsCenter - (Vector3.one * debrisWorldSize * 0.5f);

            Vector3 worldOriginDiff = vol.WorldOrigin - debrisOrigin;
            Vector3 debrisGridOffset = worldOriginDiff / voxelSize;

            Debug.Log($"[StructuralCleaner] Analysis Complete: Center={boundsCenter}, Size={debrisWorldSize}, Res={debrisResolution}, Origin={debrisOrigin}");

            // --- Phase 2: Volume Allocation ---
            VoxelVolume debrisVolume = VoxelVolumePool.Instance.GetVolume(debrisOrigin, debrisWorldSize, -1, -1, debrisResolution, true);
            
            if (debrisVolume == null)
            {
                Debug.LogError("[StructuralCleaner] Failed to allocate debris volume. Pool exhausted?");
                return;
            }
            
            debrisVolume.gameObject.name = $"Debris_{System.DateTime.Now.Ticks}";
            // ----------------------------------

            // 1. Prepare Data
            float worldToVoxelScale = vol.Resolution / vol.WorldSize;
            
            HashSet<Vector3Int> voxelsToRemove = new HashSet<Vector3Int>();
            HashSet<Vector3Int> uniqueBricks = new HashSet<Vector3Int>();

            int resBricks = vol.Resolution / 4;
            Vector3Int maxBrickIdx = new Vector3Int(resBricks - 1, resBricks - 1, resBricks - 1);

            Vector3Int[] neighborOffsets = new Vector3Int[]
            {
                Vector3Int.zero,
                Vector3Int.up, Vector3Int.down, 
                Vector3Int.left, Vector3Int.right,
                new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1)
            };

            foreach (var worldPos in floatingVoxels)
            {
                Vector3 localPos = (worldPos - vol.WorldOrigin) * worldToVoxelScale;
                Vector3Int centerIdx = Vector3Int.FloorToInt(localPos);

                int iterations = erodeFloatingVoxels ? 7 : 1; 

                for (int i = 0; i < iterations; i++)
                {
                    Vector3Int targetIdx = centerIdx + neighborOffsets[i];
                    
                    if (targetIdx.x >= 0 && targetIdx.y >= 0 && targetIdx.z >= 0 &&
                        targetIdx.x < vol.Resolution && targetIdx.y < vol.Resolution && targetIdx.z < vol.Resolution)
                    {
                        voxelsToRemove.Add(targetIdx);
                    }
                }
            }

            List<Vector3> localVoxelPositions = new List<Vector3>(voxelsToRemove.Count);

            foreach (var vIdx in voxelsToRemove)
            {
                localVoxelPositions.Add(new Vector3(vIdx.x + 0.5f, vIdx.y + 0.5f, vIdx.z + 0.5f));

                int minX = Mathf.CeilToInt((vIdx.x - 4) / 4.0f);
                int maxX = Mathf.FloorToInt((vIdx.x + 1) / 4.0f);
                int minY = Mathf.CeilToInt((vIdx.y - 4) / 4.0f);
                int maxY = Mathf.FloorToInt((vIdx.y + 1) / 4.0f);
                int minZ = Mathf.CeilToInt((vIdx.z - 4) / 4.0f);
                int maxZ = Mathf.FloorToInt((vIdx.z + 1) / 4.0f);

                minX = Mathf.Max(minX, 0); maxX = Mathf.Min(maxX, maxBrickIdx.x);
                minY = Mathf.Max(minY, 0); maxY = Mathf.Min(maxY, maxBrickIdx.y);
                minZ = Mathf.Max(minZ, 0); maxZ = Mathf.Min(maxZ, maxBrickIdx.z);

                for (int x = minX; x <= maxX; x++)
                    for (int y = minY; y <= maxY; y++)
                        for (int z = minZ; z <= maxZ; z++)
                            uniqueBricks.Add(new Vector3Int(x, y, z));
            }

            int voxelCount = localVoxelPositions.Count;
            int brickCount = uniqueBricks.Count;

            if (voxelCount == 0) return;

            // 2. Setup Buffers
            ComputeBuffer positionsBuffer = new ComputeBuffer(voxelCount, 12);
            positionsBuffer.SetData(localVoxelPositions.ToArray());

            ComputeBuffer bricksBuffer = new ComputeBuffer(brickCount, 12);
            int3[] brickArray = uniqueBricks.Select(b => new int3(b.x, b.y, b.z)).ToArray();
            bricksBuffer.SetData(brickArray);

            // 3. Dispatch Allocation
            int kernelAlloc = voxelModifierShader.FindKernel("AllocateNodesList");
            SetCommonBuffers(kernelAlloc, vol);
            voxelModifierShader.SetBuffer(kernelAlloc, "_TargetBricks", bricksBuffer);
            voxelModifierShader.SetInt("_TargetBrickCount", brickCount);
            
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

            // 5. Dispatch Extraction
            int kernelExtract = voxelModifierShader.FindKernel("ExtractBricksList");
            SetCommonBuffers(kernelExtract, vol);
            voxelModifierShader.SetBuffer(kernelExtract, "_TargetBricks", bricksBuffer);
            voxelModifierShader.SetInt("_TargetBrickCount", brickCount);
            voxelModifierShader.SetInts("_MaxBrickIndex", new int[] {resBricks-1, resBricks-1, resBricks-1});
            voxelModifierShader.SetInts("_MinBrickIndex", new int[] {0, 0, 0});

            int totalVoxelsToRead = brickCount * 216;
            GraphicsBuffer readbackBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalVoxelsToRead, 4);
            voxelModifierShader.SetBuffer(kernelExtract, "_ReadbackBuffer", readbackBuffer);

            voxelModifierShader.Dispatch(kernelExtract, groupsAlloc, 1, 1);

            // 6. Readback with Data Interception (Phase 3)
            AsyncGPUReadback.Request(readbackBuffer, (req) => 
            {
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
                    // Pass the allocated debrisVolume to the processing function
                    ProcessReadbackData(data, vol, brickArray, debrisVolume);
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

        private void ProcessReadbackData(NativeArray<uint> data, VoxelVolume sourceVol, int3[] sourceBricks, VoxelVolume debrisVol)
        {
            if (VoxelEditManager.Instance == null) return;

            // --- Phase 3: Data Interception & Buffering ---
            List<(Vector3Int, uint[])> debrisTransferData = new List<(Vector3Int, uint[])>();

            float voxelSize = sourceVol.WorldSize / sourceVol.Resolution;
            float brickSizeWorld = voxelSize * 4.0f; // Bricks are 4^3 voxels

            Vector3Int volOriginBrick = VoxelEditManager.Instance.GetBrickCoordinate(sourceVol.WorldOrigin);

            int cursor = 0;
            for (int i = 0; i < sourceBricks.Length; i++)
            {
                int3 b = sourceBricks[i];
                Vector3Int localBrick = new Vector3Int(b.x, b.y, b.z);
                
                if (cursor + 216 > data.Length) break;

                // Slice data for this brick
                uint[] brickData = data.GetSubArray(cursor, 216).ToArray();
                cursor += 216;

                // A. Register Edit on Source Volume (To clear it)
                Vector3Int globalBrick = volOriginBrick + localBrick;
                VoxelEditManager.Instance.RegisterEdit(globalBrick, brickData);

                // B. Transform to Debris Volume Space (The "Cut")
                // 1. Calculate Source Brick World Position (Min Corner)
                Vector3 sourceBrickWorldPos = sourceVol.WorldOrigin + (new Vector3(localBrick.x, localBrick.y, localBrick.z) * brickSizeWorld);

                // 2. Calculate Debris Volume Local Position
                Vector3 debrisLocalPos = sourceBrickWorldPos - debrisVol.WorldOrigin;

                // 3. Calculate Target Brick Index in Debris Volume
                // Assuming grid alignment, we snap to the nearest brick index
                Vector3Int debrisTargetIndex = new Vector3Int(
                    Mathf.RoundToInt(debrisLocalPos.x / brickSizeWorld),
                    Mathf.RoundToInt(debrisLocalPos.y / brickSizeWorld),
                    Mathf.RoundToInt(debrisLocalPos.z / brickSizeWorld)
                );

                // 4. Buffer the Data
                debrisTransferData.Add((debrisTargetIndex, brickData));
            }
            
            Debug.Log($"[StructuralCleaner] Intercepted {debrisTransferData.Count} bricks. Ready for Phase 4 (Paste) into {debrisVol.gameObject.name}.");
            
            // Phase 4: Apply debrisTransferData to debrisVol would go here
        }
    }
}
