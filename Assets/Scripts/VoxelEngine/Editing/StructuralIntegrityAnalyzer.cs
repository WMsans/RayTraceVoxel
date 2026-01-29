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
        
        private List<Vector3> _floatingVoxelPositions = new List<Vector3>();
        private float _debugVoxelSize = 1.0f;
        
        // Queue for processing volumes sequentially to save VRAM
        private Queue<VoxelVolume> _analysisQueue = new Queue<VoxelVolume>();
        private bool _isAnalyzing = false;
        private int _currentPropagationIterations = 0;
        private const int MAX_PROPAGATION_ITERATIONS = 4096; // Increased safety limit for CCL
        private const float GROUND_THRESHOLD = 10.0f;

        // Active Buffers for current volume
        private ComputeBuffer _topologyBuffer;
        private ComputeBuffer _activeBrickBuffer;
        private ComputeBuffer _labelBuffer; // Replaces _stabilityBuffer
        private ComputeBuffer _changeFlagBuffer;
        private ComputeBuffer _debrisVoxelOutput; // Replaces _floatingVoxelOutput
        private ComputeBuffer _argsBuffer; // For indirect dispatch or count readback

        public void AnalyzeWorld(Bounds? queryBounds = null)
        {
            if (analysisShader == null) return;
            if (_isAnalyzing) return; // Prevent concurrent runs

            _floatingVoxelPositions.Clear();
            _analysisQueue.Clear();

            var volumes = VoxelVolumeRegistry.Volumes;
            foreach (var vol in volumes)
            {
                if (vol.gameObject.activeInHierarchy && vol.IsReady)
                {
                    if (queryBounds.HasValue && !queryBounds.Value.Intersects(vol.WorldBounds))
                    {
                        continue;
                    }
                    _analysisQueue.Enqueue(vol);
                }
            }

            if (_analysisQueue.Count > 0)
            {
                _isAnalyzing = true;
                ProcessNextVolume();
            }
        }

        private void ProcessNextVolume()
        {
            if (_analysisQueue.Count == 0)
            {
                _isAnalyzing = false;
                Debug.Log($"[Structural Analysis] World Scan Complete. Floating Voxels: {_floatingVoxelPositions.Count}");
                return;
            }

            VoxelVolume vol = _analysisQueue.Dequeue();
            DispatchVolumeAnalysis(vol);
        }

        private void DispatchVolumeAnalysis(VoxelVolume volume)
        {
            int res = volume.Resolution;
            int totalVoxels = res * res * res;
            int bitmaskSize = Mathf.CeilToInt(totalVoxels / 32.0f);

            // 1. Setup Buffers
            _topologyBuffer = new ComputeBuffer(bitmaskSize, 4);
            _topologyBuffer.SetData(new uint[bitmaskSize]); // Clear

            int bricksPerDim = res / 4;
            int maxBricks = bricksPerDim * bricksPerDim * bricksPerDim;
            _activeBrickBuffer = new ComputeBuffer(maxBricks, sizeof(uint), ComputeBufferType.Append);
            _activeBrickBuffer.SetCounterValue(0);

            // 2. Dispatch AnalyzeBricks
            int kernel = analysisShader.FindKernel("AnalyzeBricks");
            analysisShader.SetBuffer(kernel, "_GlobalNodeBuffer", volume.NodeBuffer);
            analysisShader.SetBuffer(kernel, "_GlobalPayloadBuffer", volume.PayloadBuffer);
            analysisShader.SetBuffer(kernel, "_GlobalBrickDataBuffer", volume.BrickDataBuffer);
            analysisShader.SetBuffer(kernel, "_PageTableBuffer", volume.BufferManager.PageTableBuffer);
            analysisShader.SetBuffer(kernel, "_TopologyBuffer", _topologyBuffer);
            analysisShader.SetBuffer(kernel, "_ActiveBrickBuffer", _activeBrickBuffer);

            analysisShader.SetInt("_Resolution", res);
            analysisShader.SetInt("_PageTableOffset", volume.BufferManager.PageTableOffset);
            analysisShader.SetInt("_BrickOffset", volume.BufferManager.BrickDataOffset);

            int groups = Mathf.CeilToInt(bricksPerDim / 4.0f);
            analysisShader.Dispatch(kernel, groups, groups, groups);

            // 3. Read Brick Count
            ComputeBuffer countBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.IndirectArguments);
            ComputeBuffer.CopyCount(_activeBrickBuffer, countBuffer, 0);

            AsyncGPUReadback.Request(countBuffer, (req) => OnBrickCountReadback(req, countBuffer, volume));
        }

        private void OnBrickCountReadback(AsyncGPUReadbackRequest request, ComputeBuffer countBuf, VoxelVolume vol)
        {
            int brickCount = 0;
            if (!request.hasError)
            {
                brickCount = (int)request.GetData<uint>()[0];
            }
            countBuf.Release();

            if (brickCount == 0)
            {
                // Empty volume, cleanup and next
                CleanupCurrentBuffers();
                ProcessNextVolume();
                return;
            }

            // 4. Setup Label & Debris Buffers
            int res = vol.Resolution;
            int totalVoxels = res * res * res;
            
            _labelBuffer = new ComputeBuffer(totalVoxels, 4); // 1 uint per voxel
            _changeFlagBuffer = new ComputeBuffer(1, 4);
            
            // Output buffer for DebrisVoxel (float3 position + uint label) -> 12 + 4 bytes = 16 stride
            _debrisVoxelOutput = new ComputeBuffer(totalVoxels, 16, ComputeBufferType.Append); 
            _debrisVoxelOutput.SetCounterValue(0);

            // Calculate Threshold
            float voxelSize = vol.WorldSize / res;
            float localThreshold = GROUND_THRESHOLD - vol.WorldOrigin.y;
            float voxelThresholdY = localThreshold / voxelSize;

            analysisShader.SetFloat("_GroundThresholdY", voxelThresholdY);

            // 5. Init Labels
            int initKernel = analysisShader.FindKernel("InitLabels");
            analysisShader.SetBuffer(initKernel, "_ActiveBricksInput", _activeBrickBuffer);
            analysisShader.SetBuffer(initKernel, "_TopologyBuffer", _topologyBuffer);
            analysisShader.SetBuffer(initKernel, "_LabelBuffer", _labelBuffer);
            analysisShader.SetInt("_Resolution", res);

            // Find the volume directly below the current one
            Vector3 targetOrigin = vol.WorldOrigin - new Vector3(0, vol.WorldSize, 0);
            VoxelVolume bottomNeighbor = null;
            
            // Simple linear search
            foreach (var v in VoxelVolumeRegistry.Volumes)
            {
                if (v == vol) continue;
                if (Vector3.Distance(v.WorldOrigin, targetOrigin) < (voxelSize * 0.5f))
                {
                    if (v.IsReady)
                    {
                        bottomNeighbor = v;
                        break;
                    }
                }
            }

            if (bottomNeighbor != null)
            {
                analysisShader.SetInt("_HasNeighbor", 1);
                analysisShader.SetInt("_NeighborResolution", bottomNeighbor.Resolution);
                
                analysisShader.SetBuffer(initKernel, "_NeighborNodeBuffer", bottomNeighbor.NodeBuffer);
                analysisShader.SetBuffer(initKernel, "_NeighborPayloadBuffer", bottomNeighbor.PayloadBuffer);
                analysisShader.SetBuffer(initKernel, "_NeighborBrickDataBuffer", bottomNeighbor.BrickDataBuffer);
                analysisShader.SetBuffer(initKernel, "_NeighborPageTableBuffer", bottomNeighbor.BufferManager.PageTableBuffer);
                
                analysisShader.SetInt("_NeighborPageTableOffset", bottomNeighbor.BufferManager.PageTableOffset);
                analysisShader.SetInt("_NeighborBrickOffset", bottomNeighbor.BufferManager.BrickDataOffset);
            }
            else
            {
                analysisShader.SetInt("_HasNeighbor", 0);
                analysisShader.SetBuffer(initKernel, "_NeighborNodeBuffer", vol.NodeBuffer);
                analysisShader.SetBuffer(initKernel, "_NeighborPayloadBuffer", vol.PayloadBuffer);
                analysisShader.SetBuffer(initKernel, "_NeighborBrickDataBuffer", vol.BrickDataBuffer);
                analysisShader.SetBuffer(initKernel, "_NeighborPageTableBuffer", vol.BufferManager.PageTableBuffer);
            }

            analysisShader.Dispatch(initKernel, brickCount, 1, 1);

            // Start Propagation
            _currentPropagationIterations = 0;
            RunPropagationPass(vol, brickCount);
        }

        private const int PROPAGATION_BATCH_SIZE = 64;

        private void RunPropagationPass(VoxelVolume vol, int brickCount)
        {
            _changeFlagBuffer.SetData(new uint[] { 0 });

            int propKernel = analysisShader.FindKernel("PropagateLabels");
            analysisShader.SetBuffer(propKernel, "_ActiveBricksInput", _activeBrickBuffer);
            analysisShader.SetBuffer(propKernel, "_TopologyBuffer", _topologyBuffer);
            analysisShader.SetBuffer(propKernel, "_LabelBuffer", _labelBuffer);
            analysisShader.SetBuffer(propKernel, "_ChangeFlagBuffer", _changeFlagBuffer);
            analysisShader.SetInt("_Resolution", vol.Resolution);

            for (int i = 0; i < PROPAGATION_BATCH_SIZE; i++)
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
            _currentPropagationIterations += PROPAGATION_BATCH_SIZE;

            if (changed > 0 && _currentPropagationIterations < MAX_PROPAGATION_ITERATIONS)
            {
                RunPropagationPass(vol, brickCount);
            }
            else
            {
                if (_currentPropagationIterations >= MAX_PROPAGATION_ITERATIONS)
                {
                    Debug.LogWarning("[Structural Analysis] Max propagation iterations reached. Results may be incomplete.");
                }
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
            analysisShader.SetInt("_Resolution", vol.Resolution);

            analysisShader.Dispatch(collectKernel, brickCount, 1, 1);

            // Read count
            ComputeBuffer countBuf = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.IndirectArguments);
            ComputeBuffer.CopyCount(_debrisVoxelOutput, countBuf, 0);

            AsyncGPUReadback.Request(countBuf, (req) => OnFinalCountReadback(req, countBuf, vol));
        }

        private void OnFinalCountReadback(AsyncGPUReadbackRequest request, ComputeBuffer countBuf, VoxelVolume vol)
        {
            int count = 0;
            if (!request.hasError)
            {
                count = (int)request.GetData<uint>()[0];
            }
            countBuf.Release();

            if (count > 0)
            {
                AsyncGPUReadback.Request(_debrisVoxelOutput, (req) => OnFinalDataReadback(req, count, vol));
            }
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
                float scale = vol.WorldSize / vol.Resolution;
                
                int readCount = Mathf.Min(count, data.Length);
                
                // Grouping: Island ID -> List of World Positions
                Dictionary<uint, List<Vector3>> debrisIslands = new Dictionary<uint, List<Vector3>>();

                for (int i = 0; i < readCount; i++)
                {
                    DebrisVoxel voxel = data[i];
                    Vector3 local = new Vector3(voxel.position.x + 0.5f, voxel.position.y + 0.5f, voxel.position.z + 0.5f);
                    Vector3 worldPos = vol.WorldOrigin + (local * scale);

                    if (!debrisIslands.ContainsKey(voxel.label))
                    {
                        debrisIslands[voxel.label] = new List<Vector3>();
                    }
                    debrisIslands[voxel.label].Add(worldPos);
                }

                Debug.Log($"[Structural Analysis] Found {debrisIslands.Count} distinct floating islands.");

                // Flatten for StructuralCleaner (or future physics processing)
                List<Vector3> allFloatingVoxels = new List<Vector3>();
                foreach (var island in debrisIslands.Values)
                {
                    allFloatingVoxels.AddRange(island);
                    _floatingVoxelPositions.AddRange(island);
                }

                if (allFloatingVoxels.Count > 0)
                {
                    OnAnalysisCompleted?.Invoke(vol, allFloatingVoxels);
                }
            }

            CleanupCurrentBuffers();
            ProcessNextVolume();
        }

        private void CleanupCurrentBuffers()
        {
            if (_topologyBuffer != null) _topologyBuffer.Release();
            if (_activeBrickBuffer != null) _activeBrickBuffer.Release();
            if (_labelBuffer != null) _labelBuffer.Release();
            if (_changeFlagBuffer != null) _changeFlagBuffer.Release();
            if (_debrisVoxelOutput != null) _debrisVoxelOutput.Release();
            
            _topologyBuffer = null;
            _activeBrickBuffer = null;
            _labelBuffer = null;
            _changeFlagBuffer = null;
            _debrisVoxelOutput = null;
        }

        private void OnDestroy()
        {
            CleanupCurrentBuffers();
        }

        private void OnDrawGizmos()
        {
            if (_floatingVoxelPositions.Count > 0)
            {
                Gizmos.color = Color.red;
                Vector3 size = Vector3.one * _debugVoxelSize;
                foreach (var pos in _floatingVoxelPositions)
                {
                    Gizmos.DrawWireCube(pos, size);
                }
            }
        }
    }
}