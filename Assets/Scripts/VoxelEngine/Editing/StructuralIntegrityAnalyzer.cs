using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Core;

namespace VoxelEngine.Core.Editing
{
    public class StructuralIntegrityAnalyzer : MonoBehaviour
    {
        public struct DebrisVoxel
        {
            public float3 position;
            public uint label;
        }

        public ComputeShader analysisShader;

        public event System.Action<VoxelVolume, List<Vector3>> OnAnalysisCompleted;
        
        [Header("Safety Settings")]
        [Tooltip("If the number of floating voxels exceeds this count for a static world chunk, the deletion will be aborted.")]
        public int safetyVoxelCountLimit = 100000;
        
        private List<Vector3> _floatingVoxelPositions = new List<Vector3>();
        private float _debugVoxelSize = 1.0f;
        
        private Queue<VoxelVolume> _analysisQueue = new Queue<VoxelVolume>();
        private bool _isAnalyzing = false;
        private int _currentPropagationIterations = 0;
        private const int MAX_PROPAGATION_ITERATIONS = 4096;
        private const float GROUND_THRESHOLD = 10.0f;

        private ComputeBuffer _topologyBuffer;
        private ComputeBuffer _activeBrickBuffer;
        private ComputeBuffer _activeBrickCountBuffer;
        private ComputeBuffer _labelBuffer;
        private ComputeBuffer _changeFlagBuffer;
        private ComputeBuffer _debrisVoxelOutput;
        private ComputeBuffer _debrisCountBuffer;

        // Definition for cardinal directions to check
        private struct NeighborCheck
        {
            public Vector3Int direction; // Normal (-1,0,0 etc)
            public Vector3 checkOffset;  // Offset from bounds center
        }

        private NeighborCheck[] _cardinalChecks;

        private void Awake()
        {
            // Initialize neighbor check directions
            _cardinalChecks = new NeighborCheck[]
            {
                new NeighborCheck { direction = new Vector3Int(0, -1, 0), checkOffset = new Vector3(0, -1, 0) }, // Down
                new NeighborCheck { direction = new Vector3Int(0, 1, 0),  checkOffset = new Vector3(0, 1, 0) },  // Up
                new NeighborCheck { direction = new Vector3Int(-1, 0, 0), checkOffset = new Vector3(-1, 0, 0) }, // Left
                new NeighborCheck { direction = new Vector3Int(1, 0, 0),  checkOffset = new Vector3(1, 0, 0) },  // Right
                new NeighborCheck { direction = new Vector3Int(0, 0, -1), checkOffset = new Vector3(0, 0, -1) }, // Back
                new NeighborCheck { direction = new Vector3Int(0, 0, 1),  checkOffset = new Vector3(0, 0, 1) }   // Forward
            };
        }

        public void AnalyzeWorld(Bounds? queryBounds = null)
        {
            if (analysisShader == null) return;
            // Note: We don't return if _isAnalyzing is true, we just add to queue. 
            // The queue processing handles the continuation.
            
            _floatingVoxelPositions.Clear();
            // Don't clear the queue here, or we lose pending propagations!
            // _analysisQueue.Clear(); 

            var volumes = VoxelVolumeRegistry.Volumes;
            foreach (var vol in volumes)
            {
                if (vol.gameObject.activeInHierarchy && vol.IsReady && !vol.IsTransient)
                {
                    if (queryBounds.HasValue && !queryBounds.Value.Intersects(vol.WorldBounds))
                        continue;
                    
                    if (!_analysisQueue.Contains(vol))
                        _analysisQueue.Enqueue(vol);
                }
            }

            if (!_isAnalyzing && _analysisQueue.Count > 0)
            {
                _isAnalyzing = true;
                ProcessNextVolume();
            }
        }

        public void AnalyzeVolume(VoxelVolume targetVolume, Bounds? queryBounds = null)
        {
            if (analysisShader == null || targetVolume == null) return;

            if (targetVolume.gameObject.activeInHierarchy && targetVolume.IsReady)
            {
                if (!_analysisQueue.Contains(targetVolume))
                    _analysisQueue.Enqueue(targetVolume);
            }

            if (!_isAnalyzing && _analysisQueue.Count > 0)
            {
                _isAnalyzing = true;
                _floatingVoxelPositions.Clear();
                ProcessNextVolume();
            }
        }

        private void ProcessNextVolume()
        {
            if (_analysisQueue.Count == 0)
            {
                _isAnalyzing = false;
                // Debug.Log($"[Structural Analysis] Analysis Batch Complete.");
                return;
            }

            VoxelVolume vol = _analysisQueue.Dequeue();
            // Verify volume is still valid before processing
            if (vol == null || !vol.gameObject.activeInHierarchy || !vol.IsReady)
            {
                ProcessNextVolume();
                return;
            }

            DispatchVolumeAnalysis(vol);
        }

        private void DispatchVolumeAnalysis(VoxelVolume volume)
        {
            int res = volume.Resolution;
            int totalVoxels = res * res * res;
            int bitmaskSize = Mathf.CeilToInt(totalVoxels / 32.0f);

            _topologyBuffer = new ComputeBuffer(bitmaskSize, 4);
            _topologyBuffer.SetData(new uint[bitmaskSize]);

            int bricksPerDim = res / 4;
            int maxBricks = bricksPerDim * bricksPerDim * bricksPerDim;
            _activeBrickBuffer = new ComputeBuffer(maxBricks, sizeof(uint));
            _activeBrickCountBuffer = new ComputeBuffer(1, sizeof(uint));
            _activeBrickCountBuffer.SetData(new uint[] { 0 });

            int kernel = analysisShader.FindKernel("AnalyzeBricks");
            analysisShader.SetBuffer(kernel, "_GlobalNodeBuffer", volume.NodeBuffer);
            analysisShader.SetBuffer(kernel, "_GlobalPayloadBuffer", volume.PayloadBuffer);
            analysisShader.SetBuffer(kernel, "_GlobalBrickDataBuffer", volume.BrickDataBuffer);
            analysisShader.SetBuffer(kernel, "_PageTableBuffer", volume.BufferManager.PageTableBuffer);
            analysisShader.SetBuffer(kernel, "_TopologyBuffer", _topologyBuffer);
            analysisShader.SetBuffer(kernel, "_ActiveBrickBuffer", _activeBrickBuffer);
            analysisShader.SetBuffer(kernel, "_ActiveBrickCountBuffer", _activeBrickCountBuffer);

            analysisShader.SetInt("_Resolution", res);
            analysisShader.SetInt("_PageTableOffset", volume.BufferManager.PageTableOffset);
            analysisShader.SetInt("_BrickOffset", volume.BufferManager.BrickDataOffset);

            int groups = Mathf.CeilToInt(bricksPerDim / 4.0f);
            analysisShader.Dispatch(kernel, groups, groups, groups);

            AsyncGPUReadback.Request(_activeBrickCountBuffer, (req) => OnBrickCountReadback(req, volume));
        }

        private void OnBrickCountReadback(AsyncGPUReadbackRequest request, VoxelVolume vol)
        {
            int brickCount = 0;
            if (!request.hasError)
            {
                brickCount = (int)request.GetData<uint>()[0];
                Debug.Log($"[Structural Analysis] Phase 1 for {vol.name}: {brickCount} active bricks (API: {SystemInfo.graphicsDeviceType})");
            }
            else
            {
                Debug.LogError($"[Structural Analysis] GPU readback error for {vol.name}");
            }

            if (brickCount == 0)
            {
                CleanupCurrentBuffers();
                ProcessNextVolume();
                return;
            }

            // Setup buffers
            int res = vol.Resolution;
            int totalVoxels = res * res * res;
            
            _labelBuffer = new ComputeBuffer(totalVoxels, 4);
            // Critical: Initialize all labels to AIR (~0u). Without this, uninitialized voxels
            // default to 0 (GROUNDED), causing PropagateLabels to treat empty space outside
            // active bricks as stable anchors - which grounds everything and produces zero debris.
            var initLabels = new uint[totalVoxels];
            for (int i = 0; i < totalVoxels; i++) initLabels[i] = 0xFFFFFFFF;
            _labelBuffer.SetData(initLabels);
            _changeFlagBuffer = new ComputeBuffer(1, 4);
            _debrisVoxelOutput = new ComputeBuffer(totalVoxels, 16);
            _debrisCountBuffer = new ComputeBuffer(1, sizeof(uint));
            _debrisCountBuffer.SetData(new uint[] { 0 });

            float voxelSize = vol.WorldSize / res;
            float localThreshold = GROUND_THRESHOLD - vol.WorldOrigin.y;
            float voxelThresholdY = localThreshold / voxelSize;
            analysisShader.SetFloat("_GroundThresholdY", voxelThresholdY);

            // 1. Initial Pass: Internal Stability (Ground)
            int initKernel = analysisShader.FindKernel("InitLabels");
            analysisShader.SetBuffer(initKernel, "_ActiveBricksInput", _activeBrickBuffer);
            analysisShader.SetBuffer(initKernel, "_TopologyBuffer", _topologyBuffer);
            analysisShader.SetBuffer(initKernel, "_LabelBuffer", _labelBuffer);
            analysisShader.SetInt("_Resolution", res);
            
            analysisShader.Dispatch(initKernel, brickCount, 1, 1);

            // 2. Neighbor Passes: Check all 6 directions
            int checkKernel = analysisShader.FindKernel("CheckNeighborBoundary");
            
            Bounds worldBounds = vol.WorldBounds;
            Vector3 worldSize = worldBounds.size;

            foreach (var check in _cardinalChecks)
            {
                // Calculate position to probe for neighbor
                // We check slightly outside the bounds in the given direction
                Vector3 probePos = worldBounds.center + Vector3.Scale(worldSize * 0.55f, (Vector3)check.checkOffset);
                
                VoxelVolume neighbor = FindNeighbor(vol, probePos);

                if (neighbor != null)
                {
                    // Bind neighbor buffers
                    analysisShader.SetInt("_NeighborResolution", neighbor.Resolution);
                    analysisShader.SetBuffer(checkKernel, "_NeighborNodeBuffer", neighbor.NodeBuffer);
                    analysisShader.SetBuffer(checkKernel, "_NeighborPayloadBuffer", neighbor.PayloadBuffer);
                    analysisShader.SetBuffer(checkKernel, "_NeighborBrickDataBuffer", neighbor.BrickDataBuffer);
                    analysisShader.SetBuffer(checkKernel, "_NeighborPageTableBuffer", neighbor.BufferManager.PageTableBuffer);
                    analysisShader.SetInt("_NeighborPageTableOffset", neighbor.BufferManager.PageTableOffset);
                    analysisShader.SetInt("_NeighborBrickOffset", neighbor.BufferManager.BrickDataOffset);
                    
                    // Set Direction parameters
                    analysisShader.SetInts("_FaceNormal", new int[] { check.direction.x, check.direction.y, check.direction.z });
                    
                    // Bind operational buffers
                    analysisShader.SetBuffer(checkKernel, "_ActiveBricksInput", _activeBrickBuffer);
                    analysisShader.SetBuffer(checkKernel, "_LabelBuffer", _labelBuffer);
                    analysisShader.SetInt("_Resolution", res);

                    analysisShader.Dispatch(checkKernel, brickCount, 1, 1);
                }
            }

            // Start Propagation
            _currentPropagationIterations = 0;
            RunPropagationPass(vol, brickCount);
        }

        private VoxelVolume FindNeighbor(VoxelVolume source, Vector3 probePoint)
        {
            foreach (var v in VoxelVolumeRegistry.Volumes)
            {
                if (v == source) continue;
                if (!v.IsReady || !v.gameObject.activeInHierarchy) continue;
                if (v.IsTransient) continue; // Skip debris, only check world chunks

                if (v.WorldBounds.Contains(probePoint))
                {
                    return v;
                }
            }
            return null;
        }

        private void RunPropagationPass(VoxelVolume vol, int brickCount)
        {
            _changeFlagBuffer.SetData(new uint[] { 0 });

            int propKernel = analysisShader.FindKernel("PropagateLabels");
            analysisShader.SetBuffer(propKernel, "_ActiveBricksInput", _activeBrickBuffer);
            analysisShader.SetBuffer(propKernel, "_TopologyBuffer", _topologyBuffer);
            analysisShader.SetBuffer(propKernel, "_LabelBuffer", _labelBuffer);
            analysisShader.SetBuffer(propKernel, "_ChangeFlagBuffer", _changeFlagBuffer);
            analysisShader.SetInt("_Resolution", vol.Resolution);

            // Batch multiple iterations to reduce CPU overhead
            int batchSize = 64;
            for (int i = 0; i < batchSize; i++)
            {
                analysisShader.Dispatch(propKernel, brickCount, 1, 1);
            }

            AsyncGPUReadback.Request(_changeFlagBuffer, (req) => OnPropagationReadback(req, vol, brickCount));
        }

        private void OnPropagationReadback(AsyncGPUReadbackRequest request, VoxelVolume vol, int brickCount)
        {
            if (request.hasError)
            {
                CleanupCurrentBuffers();
                ProcessNextVolume();
                return;
            }

            uint changed = request.GetData<uint>()[0];
            _currentPropagationIterations += 64;

            if (changed > 0 && _currentPropagationIterations < MAX_PROPAGATION_ITERATIONS)
            {
                RunPropagationPass(vol, brickCount);
            }
            else
            {
                Debug.Log($"[Structural Analysis] Propagation converged for {vol.name} after {_currentPropagationIterations} iterations (changed={changed})");
                if (_currentPropagationIterations >= MAX_PROPAGATION_ITERATIONS)
                    Debug.LogWarning($"[Structural Analysis] Max propagation iterations ({MAX_PROPAGATION_ITERATIONS}) reached for {vol.name}.");
                
                CollectResults(vol, brickCount);
            }
        }

        private void CollectResults(VoxelVolume vol, int brickCount)
        {
            int collectKernel = analysisShader.FindKernel("CollectDebris");
            analysisShader.SetBuffer(collectKernel, "_ActiveBricksInput", _activeBrickBuffer);
            analysisShader.SetBuffer(collectKernel, "_TopologyBuffer", _topologyBuffer);
            analysisShader.SetBuffer(collectKernel, "_LabelBuffer", _labelBuffer);
            analysisShader.SetBuffer(collectKernel, "_DebrisVoxelOutput", _debrisVoxelOutput);
            analysisShader.SetBuffer(collectKernel, "_DebrisCountBuffer", _debrisCountBuffer);
            analysisShader.SetInt("_Resolution", vol.Resolution);

            analysisShader.Dispatch(collectKernel, brickCount, 1, 1);

            AsyncGPUReadback.Request(_debrisCountBuffer, (req) => OnFinalCountReadback(req, vol));
        }

        private void OnFinalCountReadback(AsyncGPUReadbackRequest request, VoxelVolume vol)
        {
            int count = 0;
            if (!request.hasError) count = (int)request.GetData<uint>()[0];

            Debug.Log($"[Structural Analysis] Debris collection for {vol.name}: {count} floating voxels found");

            if (count > 0)
                AsyncGPUReadback.Request(_debrisVoxelOutput, (req) => OnFinalDataReadback(req, count, vol));
            else
            {
                CleanupCurrentBuffers();
                ProcessNextVolume();
            }
        }

        private void OnFinalDataReadback(AsyncGPUReadbackRequest request, int count, VoxelVolume vol)
        {
            if (!request.hasError)
            {
                var data = request.GetData<DebrisVoxel>();
                float voxelSize = vol.WorldSize / vol.Resolution;
                int readCount = Mathf.Min(count, data.Length);

                if (!vol.IsTransient && readCount > safetyVoxelCountLimit)
                {
                    Debug.LogWarning($"[Structural Analysis] Safety Stop: {readCount} voxels floating in {vol.name}. Aborting.");
                    CleanupCurrentBuffers();
                    ProcessNextVolume();
                    return;
                }

                // Phase 3: The Chain (Domino Effect Logic)
                // If a persistent world chunk is losing voxels, we must check if any of those
                // voxels were supporting neighbors. If so, those neighbors become dirty.
                if (!vol.IsTransient)
                {
                    CheckForNeighborPropagation(vol, data, readCount);
                }
                
                Dictionary<uint, List<Vector3>> debrisIslands = new Dictionary<uint, List<Vector3>>();

                for (int i = 0; i < readCount; i++)
                {
                    DebrisVoxel voxel = data[i];
                    Vector3 localPos = new Vector3(voxel.position.x + 0.5f, voxel.position.y + 0.5f, voxel.position.z + 0.5f) * voxelSize;
                    Vector3 worldPos = vol.transform.TransformPoint(localPos);

                    if (!debrisIslands.ContainsKey(voxel.label))
                        debrisIslands[voxel.label] = new List<Vector3>();
                    
                    debrisIslands[voxel.label].Add(worldPos);
                }

                if (vol.IsTransient)
                {
                    // For debris splitting (recursive fracture)
                    if (debrisIslands.Count > 1)
                    {
                        var sortedIslands = debrisIslands.Values.OrderByDescending(island => island.Count).ToList();
                        for (int i = 1; i < sortedIslands.Count; i++)
                        {
                            _floatingVoxelPositions.AddRange(sortedIslands[i]);
                            OnAnalysisCompleted?.Invoke(vol, sortedIslands[i]);
                        }
                    }
                }
                else
                {
                    // For World Terrain, everything identified as floating is debris
                    foreach (var island in debrisIslands.Values)
                    {
                        _floatingVoxelPositions.AddRange(island);
                        OnAnalysisCompleted?.Invoke(vol, island);
                    }
                }
            }

            CleanupCurrentBuffers();
            ProcessNextVolume();
        }

        /// <summary>
        /// Checks the positions of falling debris to see if they were located on the boundary of the volume.
        /// If so, the neighbor in that direction is queued for analysis, ensuring propagation of instability.
        /// </summary>
        private void CheckForNeighborPropagation(VoxelVolume sourceVol, NativeArray<DebrisVoxel> debris, int count)
        {
            int res = sourceVol.Resolution;
            bool[] facesTouched = new bool[6]; // -X, +X, -Y, +Y, -Z, +Z

            // 1. Identify touched faces (Optimization: don't find neighbors inside the loop)
            for (int i = 0; i < count; i++)
            {
                float3 p = debris[i].position;
                if (p.x < 0.5f) facesTouched[0] = true;
                else if (p.x >= res - 0.5f) facesTouched[1] = true;
                
                if (p.y < 0.5f) facesTouched[2] = true;
                else if (p.y >= res - 0.5f) facesTouched[3] = true;

                if (p.z < 0.5f) facesTouched[4] = true;
                else if (p.z >= res - 0.5f) facesTouched[5] = true;
            }

            // 2. Queue Neighbors for touched faces
            Bounds bounds = sourceVol.WorldBounds;
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents; 
            
            // Push probe slightly out of bounds to hit neighbor
            float probeDist = sourceVol.WorldSize * 0.05f; 

            if (facesTouched[0]) QueueNeighborAt(sourceVol, center - sourceVol.transform.right * (extents.x + probeDist));
            if (facesTouched[1]) QueueNeighborAt(sourceVol, center + sourceVol.transform.right * (extents.x + probeDist));
            
            if (facesTouched[2]) QueueNeighborAt(sourceVol, center - sourceVol.transform.up * (extents.y + probeDist));
            if (facesTouched[3]) QueueNeighborAt(sourceVol, center + sourceVol.transform.up * (extents.y + probeDist));
            
            if (facesTouched[4]) QueueNeighborAt(sourceVol, center - sourceVol.transform.forward * (extents.z + probeDist));
            if (facesTouched[5]) QueueNeighborAt(sourceVol, center + sourceVol.transform.forward * (extents.z + probeDist));
        }

        private void QueueNeighborAt(VoxelVolume source, Vector3 probePos)
        {
            VoxelVolume neighbor = FindNeighbor(source, probePos);
            if (neighbor != null && !_analysisQueue.Contains(neighbor))
            {
                // Debug.Log($"[Structural Analysis] Propagation: {source.name} -> {neighbor.name}");
                _analysisQueue.Enqueue(neighbor);
            }
        }

        private void CleanupCurrentBuffers()
        {
            _topologyBuffer?.Release(); _topologyBuffer = null;
            _activeBrickBuffer?.Release(); _activeBrickBuffer = null;
            _activeBrickCountBuffer?.Release(); _activeBrickCountBuffer = null;
            _labelBuffer?.Release(); _labelBuffer = null;
            _changeFlagBuffer?.Release(); _changeFlagBuffer = null;
            _debrisVoxelOutput?.Release(); _debrisVoxelOutput = null;
            _debrisCountBuffer?.Release(); _debrisCountBuffer = null;
        }

        private void OnDestroy() { CleanupCurrentBuffers(); }

        private void OnDrawGizmos()
        {
            if (_floatingVoxelPositions.Count > 0)
            {
                Gizmos.color = Color.red;
                Vector3 size = Vector3.one * _debugVoxelSize;
                foreach (var pos in _floatingVoxelPositions)
                    Gizmos.DrawWireCube(pos, size);
            }
        }
    }
}