using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine.Core;
using VoxelEngine.Core.Streaming; // Added for VoxelVolumePool
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
            
            // Find the maximum dimension to ensure the volume is cubic (common for voxel grids)
            float maxDimension = Mathf.Max(rawSize.x, Mathf.Max(rawSize.y, rawSize.z));

            // Get the source voxel size to maintain consistent density
            float voxelSize = vol.WorldSize / vol.Resolution;

            // Calculate how many voxels are needed to cover the object
            // Add padding (e.g., 2 voxels) to prevent clipping at the edges
            int requiredResolution = Mathf.CeilToInt(maxDimension / voxelSize) + 2;

            // Snap the resolution to the nearest power of two (min 16 for safety)
            int debrisResolution = Mathf.NextPowerOfTwo(Mathf.Max(requiredResolution, 16));

            // Calculate the final World Size for the new Debris Volume
            float debrisWorldSize = debrisResolution * voxelSize;

            // Calculate the Origin (Bottom-Left-Back corner) for the new volume.
            // Note: VoxelVolume origin is typically the min corner, not the center.
            // We center the debris volume around the bounds center.
            Vector3 debrisOrigin = boundsCenter - (Vector3.one * debrisWorldSize * 0.5f);

            // 3. Coordinate Conversion Strategy
            // We need to map Source Local Coord -> Debris Local Coord
            // Formula: DebrisLocalPos = SourceLocalPos + (SourceOrigin - DebrisOrigin) / VoxelSize
            Vector3 worldOriginDiff = vol.WorldOrigin - debrisOrigin;
            
            // This offset vector can be added to source local indices to get debris local indices
            // (Assuming grids are aligned; if not, interpolation would be needed, but we assume alignment for now)
            Vector3 debrisGridOffset = worldOriginDiff / voxelSize;

            Debug.Log($"[StructuralCleaner] Analysis Complete: Center={boundsCenter}, Size={debrisWorldSize}, Res={debrisResolution}, Origin={debrisOrigin}");

            // --- Phase 2: Volume Allocation ---
            // Request an empty volume from the pool with the specific resolution to match density.
            // We pass 'true' for generateEmpty to ensure no terrain is generated.
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
            
            // Use a HashSet to store unique voxel indices (prevents duplicates from neighbor expansion)
            HashSet<Vector3Int> voxelsToRemove = new HashSet<Vector3Int>();
            HashSet<Vector3Int> uniqueBricks = new HashSet<Vector3Int>();

            int resBricks = vol.Resolution / 4;
            Vector3Int maxBrickIdx = new Vector3Int(resBricks - 1, resBricks - 1, resBricks - 1);

            // Define neighbors for erosion (6-way)
            Vector3Int[] neighborOffsets = new Vector3Int[]
            {
                Vector3Int.zero, // Include self
                Vector3Int.up, Vector3Int.down, 
                Vector3Int.left, Vector3Int.right,
                new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1)
            };

            foreach (var worldPos in floatingVoxels)
            {
                // Convert World -> Local Voxel Space
                Vector3 localPos = (worldPos - vol.WorldOrigin) * worldToVoxelScale;
                Vector3Int centerIdx = Vector3Int.FloorToInt(localPos);

                // Expand selection if erosion is enabled
                int iterations = erodeFloatingVoxels ? 7 : 1; 

                for (int i = 0; i < iterations; i++)
                {
                    Vector3Int targetIdx = centerIdx + neighborOffsets[i];
                    
                    // Bounds Check
                    if (targetIdx.x >= 0 && targetIdx.y >= 0 && targetIdx.z >= 0 &&
                        targetIdx.x < vol.Resolution && targetIdx.y < vol.Resolution && targetIdx.z < vol.Resolution)
                    {
                        voxelsToRemove.Add(targetIdx);
                    }
                }
            }

            // Convert unique indices back to centered local positions and calculate bricks
            List<Vector3> localVoxelPositions = new List<Vector3>(voxelsToRemove.Count);

            foreach (var vIdx in voxelsToRemove)
            {
                // Add 0.5 to center the float position in the voxel
                localVoxelPositions.Add(new Vector3(vIdx.x + 0.5f, vIdx.y + 0.5f, vIdx.z + 0.5f));

                // Identify Brick
                // Calculate brick range covering this voxel (accounting for 1-voxel padding)
                // Brick B covers [B*4-1, B*4+4]. 
                int minX = Mathf.CeilToInt((vIdx.x - 4) / 4.0f);
                int maxX = Mathf.FloorToInt((vIdx.x + 1) / 4.0f);
                int minY = Mathf.CeilToInt((vIdx.y - 4) / 4.0f);
                int maxY = Mathf.FloorToInt((vIdx.y + 1) / 4.0f);
                int minZ = Mathf.CeilToInt((vIdx.z - 4) / 4.0f);
                int maxZ = Mathf.FloorToInt((vIdx.z + 1) / 4.0f);

                // Clamp to volume bounds
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
            
            Debug.Log($"[StructuralCleaner] Successfully removed {bricks.Length} floating bricks (including erosion).");
        }
    }
}