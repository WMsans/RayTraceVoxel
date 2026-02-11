using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine.Core;
using VoxelEngine.Core.Streaming;
using System.Collections.Generic;
using Unity.Collections;
using System.Linq;
using Unity.Mathematics;
using VoxelEngine.Physics;

namespace VoxelEngine.Core.Editing
{
    public class StructuralCleaner : MonoBehaviour
    {
        public ComputeShader voxelModifierShader;
        public StructuralIntegrityAnalyzer analyzer;

        [Header("Settings")]
        [Tooltip("If true, removes neighbors of floating voxels to ensure clean breaks and remove diagonal artifacts.")]
        public bool erodeFloatingVoxels = true;

        [Tooltip("Density multiplier for debris mass calculation (Mass = Volume * Density).")]
        public float debrisDensity = 10.0f;

        [Tooltip("If debris has fewer voxels than this, a BoxCollider will be generated instead of a MeshCollider.")]
        public int smallDebrisVoxelLimit = 64;

        [Tooltip("Minimum number of voxels required to generate debris. If less, the floating voxels are ignored (remain in source).")]
        public int minimumDebrisVoxelCount = 10;

        [Header("Stitching")]
        [Tooltip("If true, attempts to physically link debris chunks that span across chunk boundaries.")]
        public bool stitchDebris = true;
        
        [Tooltip("The time window (in seconds) to consider separate debris pieces as part of the same structural failure.")]
        public float stitchingTimeWindow = 0.5f;

        [Tooltip("The overlap/touch tolerance distance for stitching detection.")]
        public float stitchingTolerance = 0.1f;

        // Internal Stitching Tracking
        private struct DebrisFragment
        {
            public VoxelVolume Debris;
            public VoxelVolume Source;
            public Bounds WorldBounds;
            public float Timestamp;
        }
        
        // Tracks recently created debris to find matches
        private List<DebrisFragment> _recentFragments = new List<DebrisFragment>();

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

        public void RecalculateDebrisMass(VoxelVolume vol, int removedVoxelCount)
        {
            if (!vol.IsTransient) return;

            Rigidbody rb = vol.GetComponent<Rigidbody>();
            if (rb == null) return;

            float voxelSize = vol.WorldSize / vol.Resolution;
            float singleVoxelVol = Mathf.Pow(voxelSize, 3.0f);
            
            float removedMass = removedVoxelCount * singleVoxelVol * debrisDensity;
            rb.mass = Mathf.Max(0.1f, rb.mass - removedMass);
            
            Debug.Log($"[StructuralCleaner] Updated Mass for {vol.name}: {rb.mass} (Removed {removedVoxelCount} voxels)");
        }

        private void HandleAnalysisCompleted(VoxelVolume vol, List<Vector3> floatingVoxels)
        {
            if (floatingVoxels == null || floatingVoxels.Count == 0) return;
            
            // MODIFICATION: Decide if we create debris or just clean
            bool createDebris = floatingVoxels.Count >= minimumDebrisVoxelCount;

            if (voxelModifierShader == null || !vol.IsReady) return;

            // Phase 4: Mass Recalculation for Source
            if (vol.IsTransient)
            {
                RecalculateDebrisMass(vol, floatingVoxels.Count);
            }

            float voxelSize = vol.WorldSize / vol.Resolution;
            float brickSizeWorld = voxelSize * 4.0f;

            // Variables needed for Debris Creation
            VoxelVolume debrisVolume = null;

            if (createDebris)
            {
                // 1. Calculate Bounds in LOCAL Space
                // We must operate in the Source Volume's local space to maintain orientation.
                Vector3 minLocal = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 maxLocal = new Vector3(float.MinValue, float.MinValue, float.MinValue);

                foreach (var worldPos in floatingVoxels)
                {
                    Vector3 localPos = vol.transform.InverseTransformPoint(worldPos);
                    minLocal = Vector3.Min(minLocal, localPos);
                    maxLocal = Vector3.Max(maxLocal, localPos);
                }

                // 2. Determine Debris Volume Layout (Local)
                Vector3 localSize = maxLocal - minLocal;
                float maxDimension = Mathf.Max(localSize.x, Mathf.Max(localSize.y, localSize.z));
                
                int requiredResolution = Mathf.CeilToInt(maxDimension / voxelSize) + 2;
                int debrisResolution = Mathf.NextPowerOfTwo(Mathf.Max(requiredResolution, 16));
                float debrisWorldSize = debrisResolution * voxelSize;

                // 3. Determine Origin
                // Calculate ideal origin (corner) in local space
                Vector3 centerLocal = (minLocal + maxLocal) * 0.5f;
                Vector3 idealOriginLocal = centerLocal - (Vector3.one * debrisWorldSize * 0.5f);

                // Snap Local Origin to the Source's Local Brick Grid
                // This ensures voxels align 1:1 without resampling.
                Vector3 debrisOriginLocal = new Vector3(
                    Mathf.Round(idealOriginLocal.x / brickSizeWorld) * brickSizeWorld,
                    Mathf.Round(idealOriginLocal.y / brickSizeWorld) * brickSizeWorld,
                    Mathf.Round(idealOriginLocal.z / brickSizeWorld) * brickSizeWorld
                );

                // Transform back to World Space for instantiation
                Vector3 debrisOriginWorld = vol.transform.TransformPoint(debrisOriginLocal);

                Debug.Log($"[StructuralCleaner] Analysis Complete: WorldOrigin={debrisOriginWorld}, Res={debrisResolution}");

                // --- Phase 2: Volume Allocation ---
                debrisVolume = VoxelVolumePool.Instance.GetVolume(debrisOriginWorld, debrisWorldSize, -1, -1, debrisResolution, true);
                
                if (debrisVolume == null)
                {
                    Debug.LogError("[StructuralCleaner] Failed to allocate debris volume. Pool exhausted?");
                    createDebris = false;
                }
                else
                {
                    debrisVolume.gameObject.name = $"Debris_{System.DateTime.Now.Ticks}";
                    debrisVolume.IsTransient = true;
                    
                    // CRITICAL: Match rotation of the source volume!
                    // This volume's local grid is now aligned with the source's local grid.
                    debrisVolume.transform.rotation = vol.transform.rotation;
                    
                    // Ensure debris doesn't have a collider yet
                    if (VoxelPhysicsManager.Instance != null)
                    {
                        VoxelPhysicsManager.Instance.Remove(debrisVolume);
                        VoxelPhysicsManager.Instance.ClearCollider(debrisVolume);
                    }
                }
            }
            // ----------------------------------

            // 1. Prepare Data
            HashSet<Vector3Int> voxelsToRemove = new HashSet<Vector3Int>();
            HashSet<Vector3Int> uniqueBricks = new HashSet<Vector3Int>();

            int resBricks = vol.Resolution / 4;
            Vector3Int maxBrickIdx = new Vector3Int(resBricks - 1, resBricks - 1, resBricks - 1);

            List<Vector3Int> neighborOffsets = new List<Vector3Int>();
            for (int z = -1; z <= 1; z++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        neighborOffsets.Add(new Vector3Int(x, y, z));
                    }
                }
            }

            float inverseVoxelSize = 1.0f / voxelSize;

            foreach (var worldPos in floatingVoxels)
            {
                // Convert World Pos -> Local Pos -> Voxel Index
                Vector3 localPos = vol.transform.InverseTransformPoint(worldPos);
                Vector3Int centerIdx = Vector3Int.FloorToInt(localPos * inverseVoxelSize);

                int iterations = erodeFloatingVoxels ? neighborOffsets.Count : 1; 

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

            if (voxelCount == 0)
            {
                if (debrisVolume != null) VoxelVolumePool.Instance.ReturnVolume(debrisVolume);
                return;
            }

            // 2. Setup Buffers
            ComputeBuffer positionsBuffer = new ComputeBuffer(voxelCount, 12);
            positionsBuffer.SetData(localVoxelPositions.ToArray());

            ComputeBuffer bricksBuffer = new ComputeBuffer(brickCount, 12);
            int3[] brickArray = uniqueBricks.Select(b => new int3(b.x, b.y, b.z)).ToArray();
            bricksBuffer.SetData(brickArray);

            // 3. Dispatch Allocation (Source)
            int kernelAlloc = voxelModifierShader.FindKernel("AllocateNodesList");
            SetCommonBuffers(kernelAlloc, vol);
            voxelModifierShader.SetBuffer(kernelAlloc, "_TargetBricks", bricksBuffer);
            voxelModifierShader.SetInt("_TargetBrickCount", brickCount);
            
            voxelModifierShader.SetInts("_MaxBrickIndex", new int[] {resBricks-1, resBricks-1, resBricks-1});
            voxelModifierShader.SetInts("_MinBrickIndex", new int[] {0, 0, 0});
            
            int groupsAlloc = Mathf.CeilToInt(brickCount / 64.0f);
            voxelModifierShader.Dispatch(kernelAlloc, groupsAlloc, 1, 1);
            
            GraphicsBuffer readbackBuffer = null;

            // 4. Dispatch Extraction (Only if generating Debris)
            if (createDebris)
            {
                int kernelExtract = voxelModifierShader.FindKernel("ExtractBricksList");
                SetCommonBuffers(kernelExtract, vol);
                voxelModifierShader.SetBuffer(kernelExtract, "_TargetBricks", bricksBuffer);
                voxelModifierShader.SetInt("_TargetBrickCount", brickCount);
                voxelModifierShader.SetInts("_MaxBrickIndex", new int[] {resBricks-1, resBricks-1, resBricks-1});
                voxelModifierShader.SetInts("_MinBrickIndex", new int[] {0, 0, 0});

                int totalVoxelsToRead = brickCount * 216;
                readbackBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalVoxelsToRead, 4);
                voxelModifierShader.SetBuffer(kernelExtract, "_ReadbackBuffer", readbackBuffer);

                voxelModifierShader.Dispatch(kernelExtract, groupsAlloc, 1, 1);
            }

            // 5. Dispatch Removal (Source) - Always Clean!
            int kernelRemove = voxelModifierShader.FindKernel("RemoveVoxelList");
            SetCommonBuffers(kernelRemove, vol);
            voxelModifierShader.SetBuffer(kernelRemove, "_TargetPositions", positionsBuffer);
            voxelModifierShader.SetInt("_TargetCount", voxelCount);
            
            int groupsRemove = Mathf.CeilToInt(voxelCount / 64.0f);
            voxelModifierShader.Dispatch(kernelRemove, groupsRemove, 1, 1);

            if (VoxelPhysicsManager.Instance != null)
            {
                VoxelPhysicsManager.Instance.Enqueue(vol);
            }

            // Update Source Vegetation
            if (vol.grassRenderer != null) vol.grassRenderer.Refresh();
            if (vol.leafRenderer != null) vol.leafRenderer.Refresh();

            // 6. Readback with Data Interception (Phase 3) OR Cleanup
            if (createDebris && readbackBuffer != null)
            {
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
                        ProcessReadbackData(data, vol, brickArray, debrisVolume, voxelsToRemove);
                    }
                });
            }
            else
            {
                // Simple Cleanup if we are just cleaning source
                positionsBuffer.Release();
                bricksBuffer.Release();
                if (readbackBuffer != null) readbackBuffer.Release();
            }
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

            // Offset Calculation (Global Grid) is not safe if rotated.
            // Using Transform based offset calculation.
            
            Vector3 sourceOrigin = sourceVol.WorldOrigin;
            // Precompute packed air to fill discarded voxels
            uint packedAir = PackVoxelData(4.0f, Vector3.up, 0);

            int cursor = 0;
            for (int i = 0; i < sourceBricks.Length; i++)
            {
                if (cursor + 216 > data.Length) break;

                // 1. Extract Raw Data
                int3 srcBrickIdx = sourceBricks[i];
                uint[] brickData = new uint[216];
                uint[] sourceUpdateData = new uint[216];
                
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
                            int3 logicalPos = srcBrickIdx * 4 + new int3(x - 1, y - 1, z - 1);
                            
                            bool isFloating = voxelsToKeep.Contains(new Vector3Int(logicalPos.x, logicalPos.y, logicalPos.z));
                            
                            uint sdfEncoded = (rawVal >> 8) & 0xFF;
                            bool isSourceAir = sdfEncoded > 127;

                            if (isFloating)
                            {
                                brickData[flatIdx] = rawVal;
                                hasContent = true;
                                sourceUpdateData[flatIdx] = packedAir;
                            }
                            else if (isSourceAir)
                            {
                                brickData[flatIdx] = rawVal;
                                sourceUpdateData[flatIdx] = rawVal;
                            }
                            else
                            {
                                brickData[flatIdx] = packedAir;
                                sourceUpdateData[flatIdx] = rawVal;
                            }
                        }
                    }
                }

                // Update Source Database (Only for persistent terrain, which is axis aligned)
                if (!sourceVol.IsTransient)
                {
                    // For persistent terrain, simple addition works because it's axis aligned.
                    Vector3 srcBrickWorldPosCorner = sourceOrigin + (new Vector3(srcBrickIdx.x, srcBrickIdx.y, srcBrickIdx.z) * brickSizeWorld);
                    Vector3Int srcGlobalCoord = VoxelEditManager.Instance.GetBrickCoordinate(srcBrickWorldPosCorner + Vector3.one * 0.01f);
                    VoxelEditManager.Instance.RegisterEdit(srcGlobalCoord, sourceUpdateData);
                }

                cursor += 216;

                if (!hasContent) continue;

                // 3. Map to Debris Volume Space (Handling Rotation)
                // Get World Position of the Source Brick (considering rotation)
                Vector3 srcBrickLocalPos = new Vector3(srcBrickIdx.x, srcBrickIdx.y, srcBrickIdx.z) * brickSizeWorld;
                Vector3 srcBrickWorldPos = sourceVol.transform.TransformPoint(srcBrickLocalPos);

                // Get Local Position in Debris Volume
                Vector3 localPosInDebris = debrisVol.transform.InverseTransformPoint(srcBrickWorldPos);

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

            // --- Phase 5: Post-Process Readback & Database Hydration ---
            targetBricksBuffer.Release();
            sourceVoxelDataBuffer.Release();

            // 6. Finalize
            Debug.Log($"[StructuralCleaner] Debris created: {debrisVol.name}");

            if (VoxelPhysicsManager.Instance != null)
            {
                float singleVoxelVol = Mathf.Pow(voxelSize, 3.0f);
                float totalVolume = voxelsToKeep.Count * singleVoxelVol;

                Rigidbody rb = debrisVol.gameObject.GetComponent<Rigidbody>();
                if (rb == null)
                    rb = debrisVol.gameObject.AddComponent<Rigidbody>();

                rb.mass = Mathf.Max(0.1f, totalVolume * debrisDensity);

                // Small Debris Handling: Use BoxCollider if too small for MeshCollider
                if (voxelsToKeep.Count < smallDebrisVoxelLimit)
                {
                    Vector3Int min = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
                    Vector3Int max = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);

                    foreach (var v in voxelsToKeep)
                    {
                        min = Vector3Int.Min(min, v);
                        max = Vector3Int.Max(max, v);
                    }
                    
                    Vector3 sizeWorld = (Vector3)(max - min + Vector3Int.one) * voxelSize;
                    
                    // Center in Local Space (Grid) of the SOURCE volume
                    Vector3 centerGrid = (Vector3)min + (Vector3)(max - min) * 0.5f + Vector3.one * 0.5f;
                    Vector3 centerSourceLocal = centerGrid * voxelSize;

                    // Convert Source Local -> World -> Debris Local
                    Vector3 centerWorld = sourceVol.transform.TransformPoint(centerSourceLocal);
                    Vector3 centerDebrisLocal = debrisVol.transform.InverseTransformPoint(centerWorld);

                    // BoxCollider is local to the volume object
                    BoxCollider bc = debrisVol.GetComponent<BoxCollider>();
                    if (bc == null) bc = debrisVol.gameObject.AddComponent<BoxCollider>();
                    
                    bc.enabled = true;
                    bc.center = centerDebrisLocal;
                    bc.size = sizeWorld;

                    debrisVol.gameObject.SetActive(true);
                    if (debrisVol.meshCol != null) debrisVol.meshCol.enabled = false;
                }
                else
                {
                    if (debrisVol.meshCol != null)
                        debrisVol.meshCol.convex = true;

                    debrisVol.gameObject.SetActive(true);
                    VoxelPhysicsManager.Instance.Enqueue(debrisVol);
                }

                // Update Debris Vegetation
                if (debrisVol.grassRenderer != null) debrisVol.grassRenderer.Refresh();
                if (debrisVol.leafRenderer != null) debrisVol.leafRenderer.Refresh();

                // Phase 4: Register for Stitching
                RegisterDebrisFragment(debrisVol, sourceVol);
            }
        }

        private void RegisterDebrisFragment(VoxelVolume debrisVol, VoxelVolume sourceVol)
        {
            if (!stitchDebris || debrisVol == null) return;

            // 1. Cleanup old fragments to keep the list fresh
            float now = Time.time;
            _recentFragments.RemoveAll(x => now - x.Timestamp > stitchingTimeWindow || x.Debris == null);

            // 2. Get the bounds of the new debris for intersection checks
            Bounds bounds = new Bounds();
            Collider col = debrisVol.GetComponent<Collider>();
            
            // Handle both Box and Mesh colliders
            if (col != null) 
            {
                bounds = col.bounds;
            }
            else 
            {
                // Fallback if collider isn't ready immediately (though it should be setup above)
                return;
            }

            // Expand bounds slightly to catch adjacent chunks that are technically just touching
            bounds.Expand(stitchingTolerance);

            // 3. Scan recently created debris for neighbors
            foreach (var frag in _recentFragments)
            {
                // Rule 1: Must be from different source volumes (chunks)
                // If they are from the same chunk, they were likely already split by the analyzer 
                // because they were disjoint islands, so we shouldn't stitch them back.
                if (frag.Source == sourceVol) continue;
                
                // Rule 2: Debris must be active
                if (frag.Debris == null || !frag.Debris.gameObject.activeInHierarchy) continue;

                // Rule 3: Spatial intersection
                if (bounds.Intersects(frag.WorldBounds))
                {
                    Stitch(debrisVol, frag.Debris);
                }
            }

            // 4. Register this new piece
            _recentFragments.Add(new DebrisFragment
            {
                Debris = debrisVol,
                Source = sourceVol,
                WorldBounds = bounds,
                Timestamp = now
            });
        }

        private void Stitch(VoxelVolume a, VoxelVolume b)
        {
            Rigidbody rbA = a.GetComponent<Rigidbody>();
            Rigidbody rbB = b.GetComponent<Rigidbody>();
            
            if (rbA == null || rbB == null) return;

            // Check if a joint already exists between these two
            FixedJoint[] existingJoints = a.GetComponents<FixedJoint>();
            foreach(var j in existingJoints) 
            {
                if (j.connectedBody == rbB) return;
            }

            // Create the joint
            FixedJoint joint = a.gameObject.AddComponent<FixedJoint>();
            joint.connectedBody = rbB;
            
            // Disable collision between the stitched parts to prevent physics jitter/explosion
            joint.enableCollision = false; 
            
            // Heuristic for break force: based on mass of the connected parts
            float combinedMass = rbA.mass + rbB.mass;
            joint.breakForce = combinedMass * 50.0f; 
            joint.breakTorque = combinedMass * 50.0f;

            Debug.Log($"[StructuralCleaner] Stitched {a.name} to {b.name} (Source: {a.transform.parent?.name} & {b.transform.parent?.name})");
        }

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