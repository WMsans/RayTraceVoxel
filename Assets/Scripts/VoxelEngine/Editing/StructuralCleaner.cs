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
            float brickSizeWorld = voxelSize * 4.0f;

            int requiredResolution = Mathf.CeilToInt(maxDimension / voxelSize) + 2;
            int debrisResolution = Mathf.NextPowerOfTwo(Mathf.Max(requiredResolution, 16));

            float debrisWorldSize = debrisResolution * voxelSize;
            
            // Calculate initial origin based on bounds
            Vector3 idealOrigin = boundsCenter - (Vector3.one * debrisWorldSize * 0.5f);
            
            // This ensures that when we copy a brick from Source to Debris, the internal voxels line up perfectly.
            Vector3 offsetFromSource = idealOrigin - vol.WorldOrigin;
            offsetFromSource.x = Mathf.Round(offsetFromSource.x / brickSizeWorld) * brickSizeWorld;
            offsetFromSource.y = Mathf.Round(offsetFromSource.y / brickSizeWorld) * brickSizeWorld;
            offsetFromSource.z = Mathf.Round(offsetFromSource.z / brickSizeWorld) * brickSizeWorld;
            
            Vector3 debrisOrigin = vol.WorldOrigin + offsetFromSource;

            Debug.Log($"[StructuralCleaner] Analysis Complete: Center={boundsCenter}, Size={debrisWorldSize}, Res={debrisResolution}, Origin={debrisOrigin} (Snapped)");

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

            // 3. Dispatch Allocation (Ensure bricks exist in source so we can extract them)
            int kernelAlloc = voxelModifierShader.FindKernel("AllocateNodesList");
            SetCommonBuffers(kernelAlloc, vol);
            voxelModifierShader.SetBuffer(kernelAlloc, "_TargetBricks", bricksBuffer);
            voxelModifierShader.SetInt("_TargetBrickCount", brickCount);
            
            voxelModifierShader.SetInts("_MaxBrickIndex", new int[] {resBricks-1, resBricks-1, resBricks-1});
            voxelModifierShader.SetInts("_MinBrickIndex", new int[] {0, 0, 0});
             
            int groupsAlloc = Mathf.CeilToInt(brickCount / 64.0f);
            voxelModifierShader.Dispatch(kernelAlloc, groupsAlloc, 1, 1);

            // We need to capture the floating data while it still exists.
            
            // 4. Dispatch Extraction
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

            // 5. Dispatch Removal (Now it's safe to delete from source)
            int kernelRemove = voxelModifierShader.FindKernel("RemoveVoxelList");
            SetCommonBuffers(kernelRemove, vol);
            voxelModifierShader.SetBuffer(kernelRemove, "_TargetPositions", positionsBuffer);
            voxelModifierShader.SetInt("_TargetCount", voxelCount);
             
            int groupsRemove = Mathf.CeilToInt(voxelCount / 64.0f);
            voxelModifierShader.Dispatch(kernelRemove, groupsRemove, 1, 1);

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
                    // Pass voxelsToRemove so we can filter out the ground voxels from the extracted bricks
                    ProcessReadbackData(data, vol, brickArray, debrisVolume, voxelsToRemove);
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

        private void ProcessReadbackData(NativeArray<uint> data, VoxelVolume sourceVol, int3[] sourceBricks, VoxelVolume debrisVol, HashSet<Vector3Int> voxelsToKeep)
        {
            if (VoxelEditManager.Instance == null) return;

            // --- Phase 3: Data Interception & Buffering ---
            List<(int3, uint[])> debrisTransferData = new List<(int3, uint[])>();

            float voxelSize = sourceVol.WorldSize / sourceVol.Resolution;
            float brickSizeWorld = voxelSize * 4.0f; 

            // Calculate offset for re-centering
            Vector3 sourceOrigin = sourceVol.WorldOrigin;
            Vector3 debrisOrigin = debrisVol.WorldOrigin;

            // Precompute packed air to fill discarded voxels
            uint packedAir = PackVoxelData(4.0f, Vector3.up, 0);

            int cursor = 0;
            for (int i = 0; i < sourceBricks.Length; i++)
            {
                if (cursor + 216 > data.Length) break;

                // 1. Extract Raw Data
                int3 srcBrickIdx = sourceBricks[i];
                uint[] brickData = new uint[216];
                
                // We only want to keep voxels that were marked as 'Floating' (in voxelsToKeep).
                // Everything else (like the ground attached to the brick) should be set to Air.
                bool hasContent = false;
                
                for (int z = 0; z < 6; z++)
                {
                    for (int y = 0; y < 6; y++)
                    {
                        for (int x = 0; x < 6; x++)
                        {
                            int flatIdx = (z * 36) + (y * 6) + x;
                            uint rawVal = data[cursor + flatIdx];

                            // Map storage coord (x,y,z) to Logical Coord in Source Volume
                            // Padding is 1. Brick Size is 4.
                            // Logical Pos = BrickIdx * 4 + (StoragePos - 1)
                            int3 logicalPos = srcBrickIdx * 4 + new int3(x - 1, y - 1, z - 1);
                            
                            if (voxelsToKeep.Contains(new Vector3Int(logicalPos.x, logicalPos.y, logicalPos.z)))
                            {
                                brickData[flatIdx] = rawVal;
                                hasContent = true;
                            }
                            else
                            {
                                brickData[flatIdx] = packedAir;
                            }
                        }
                    }
                }
                cursor += 216;

                if (!hasContent) continue; // Skip empty bricks

                // 3. Map to Debris Volume Space
                Vector3 srcBrickWorldPos = sourceOrigin + (new Vector3(srcBrickIdx.x, srcBrickIdx.y, srcBrickIdx.z) * brickSizeWorld);
                
                // Local Pos in Debris Volume
                Vector3 localPosInDebris = srcBrickWorldPos - debrisOrigin;

                int3 targetBrickIdx = new int3(
                    Mathf.RoundToInt(localPosInDebris.x / brickSizeWorld),
                    Mathf.RoundToInt(localPosInDebris.y / brickSizeWorld),
                    Mathf.RoundToInt(localPosInDebris.z / brickSizeWorld)
                );

                // Filter Out of Bounds
                int resBricks = debrisVol.Resolution / 4;
                if (targetBrickIdx.x >= 0 && targetBrickIdx.x < resBricks &&
                    targetBrickIdx.y >= 0 && targetBrickIdx.y < resBricks &&
                    targetBrickIdx.z >= 0 && targetBrickIdx.z < resBricks)
                {
                    debrisTransferData.Add((targetBrickIdx, brickData));
                }
            }

            // --- Phase 4: Data Injection ---
            int count = debrisTransferData.Count;
            if (count == 0) 
            {
                // If filtering removed everything, we can return or destroy the volume
                Debug.LogWarning("[StructuralCleaner] No valid debris bricks to transfer after filtering.");
                VoxelVolumePool.Instance.ReturnVolume(debrisVol);
                return;
            }

            Debug.Log($"[StructuralCleaner] Pasting {count} filtered bricks into Debris Volume...");

            // 1. Flatten Data
            int3[] targetBrickArray = new int3[count];
            uint[] flatVoxelData = new uint[count * 216];

            for (int i = 0; i < count; i++)
            {
                targetBrickArray[i] = debrisTransferData[i].Item1;
                System.Array.Copy(debrisTransferData[i].Item2, 0, flatVoxelData, i * 216, 216);
            }

            // 2. Prepare Buffers
            ComputeBuffer targetBricksBuffer = new ComputeBuffer(count, 12);
            targetBricksBuffer.SetData(targetBrickArray);

            ComputeBuffer sourceVoxelDataBuffer = new ComputeBuffer(flatVoxelData.Length, 4);
            sourceVoxelDataBuffer.SetData(flatVoxelData);

            // 3. Dispatch Allocation
            int kernelAlloc = voxelModifierShader.FindKernel("AllocateNodesList");
            SetCommonBuffers(kernelAlloc, debrisVol);
            voxelModifierShader.SetBuffer(kernelAlloc, "_TargetBricks", targetBricksBuffer);
            voxelModifierShader.SetInt("_TargetBrickCount", count);
            
            int debrisResBricks = debrisVol.Resolution / 4;
            voxelModifierShader.SetInts("_MaxBrickIndex", new int[] {debrisResBricks-1, debrisResBricks-1, debrisResBricks-1});
            voxelModifierShader.SetInts("_MinBrickIndex", new int[] {0, 0, 0});

            int groups = Mathf.CeilToInt(count / 64.0f);
            voxelModifierShader.Dispatch(kernelAlloc, groups, 1, 1);

            // 4. Dispatch Data Write
            int kernelPaste = voxelModifierShader.FindKernel("PasteBricksList");
            SetCommonBuffers(kernelPaste, debrisVol);
            voxelModifierShader.SetBuffer(kernelPaste, "_TargetBricks", targetBricksBuffer);
            voxelModifierShader.SetInt("_TargetBrickCount", count);
            voxelModifierShader.SetBuffer(kernelPaste, "_SourceVoxelData", sourceVoxelDataBuffer);
            voxelModifierShader.SetInts("_MaxBrickIndex", new int[] {debrisResBricks-1, debrisResBricks-1, debrisResBricks-1});
            voxelModifierShader.SetInts("_MinBrickIndex", new int[] {0, 0, 0});

            voxelModifierShader.Dispatch(kernelPaste, groups, 1, 1);

            // 5. Cleanup
            targetBricksBuffer.Release();
            sourceVoxelDataBuffer.Release();

            // 6. Finalize
            Debug.Log($"[StructuralCleaner] Debris created: {debrisVol.name}");
        }

        // Helper to match Shader/Manager packing
        private static uint PackVoxelData(float sdf, Vector3 normal, uint materialID)
        {
            float MAX_SDF_RANGE = 4.0f;
            uint mat = materialID & 0xFF;
            float normalizedSDF = Mathf.Clamp(sdf / MAX_SDF_RANGE, -1.0f, 1.0f);
            uint sdfInt = (uint)((normalizedSDF * 0.5f + 0.5f) * 255.0f);
            uint norm = PackNormalOct(normal);
            return mat | (sdfInt << 8) | (norm << 16);
        }

        private static uint PackNormalOct(Vector3 n)
        {
            float sum = Mathf.Abs(n.x) + Mathf.Abs(n.y) + Mathf.Abs(n.z);
            if (sum < 1e-5f) return 0;

            n /= sum;
            Vector2 oct = n.z >= 0 ? new Vector2(n.x, n.y) : (Vector2.one - new Vector2(Mathf.Abs(n.y), Mathf.Abs(n.x))) * new Vector2(n.x >= 0 ? 1 : -1, n.y >= 0 ? 1 : -1);
            
            uint x = (uint)(Mathf.Clamp01(oct.x * 0.5f + 0.5f) * 255.0f);
            uint y = (uint)(Mathf.Clamp01(oct.y * 0.5f + 0.5f) * 255.0f);
            
            return x | (y << 8);
        }
    }
}
