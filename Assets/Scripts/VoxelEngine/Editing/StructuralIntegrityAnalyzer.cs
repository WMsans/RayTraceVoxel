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
        public ComputeShader analysisShader;

        public event System.Action<VoxelVolume, List<Vector3>> OnAnalysisCompleted;
        
        private List<Vector3> _floatingVoxelPositions = new List<Vector3>();
        private float _debugVoxelSize = 1.0f;
        
        // Queue for processing volumes sequentially to save VRAM
        private Queue<VoxelVolume> _analysisQueue = new Queue<VoxelVolume>();
        private bool _isAnalyzing = false;
        private int _currentPropagationIterations = 0;
        private const int MAX_PROPAGATION_ITERATIONS = 2048; // Safety limit
        private const float GROUND_THRESHOLD = 10.0f;

        // Active Buffers for current volume
        private ComputeBuffer _topologyBuffer;
        private ComputeBuffer _activeBrickBuffer;
        private ComputeBuffer _stabilityBuffer;
        private ComputeBuffer _changeFlagBuffer;
        private ComputeBuffer _floatingVoxelOutput;
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

            // 4. Setup Stability Buffers
            int res = vol.Resolution;
            int totalVoxels = res * res * res;
            
            _stabilityBuffer = new ComputeBuffer(totalVoxels, 4); 
            _changeFlagBuffer = new ComputeBuffer(1, 4);
            _floatingVoxelOutput = new ComputeBuffer(totalVoxels, 12, ComputeBufferType.Append); // Max reasonable floating (Full size to be safe)
            _floatingVoxelOutput.SetCounterValue(0);

            // Calculate Threshold
            float voxelSize = vol.WorldSize / res;
            float localThreshold = GROUND_THRESHOLD - vol.WorldOrigin.y;
            float voxelThresholdY = localThreshold / voxelSize;

            analysisShader.SetFloat("_GroundThresholdY", voxelThresholdY);

            // 5. Init Stability
            int initKernel = analysisShader.FindKernel("InitStability");
            analysisShader.SetBuffer(initKernel, "_ActiveBricksInput", _activeBrickBuffer);
            analysisShader.SetBuffer(initKernel, "_TopologyBuffer", _topologyBuffer);
            analysisShader.SetBuffer(initKernel, "_StabilityBuffer", _stabilityBuffer);
            analysisShader.SetInt("_Resolution", res);

            // Find the volume directly below the current one
            Vector3 targetOrigin = vol.WorldOrigin - new Vector3(0, vol.WorldSize, 0);
            VoxelVolume bottomNeighbor = null;
            
            // Simple linear search (can be optimized with a spatial hash if needed)
            foreach (var v in VoxelVolumeRegistry.Volumes)
            {
                if (v == vol) continue;
                // Check if origin matches (allowing small epsilon for float errors)
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
                
                // Bind Neighbor Buffers
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
                // Bind dummy buffers (current vol) to prevent API validation errors
                analysisShader.SetBuffer(initKernel, "_NeighborNodeBuffer", vol.NodeBuffer);
                analysisShader.SetBuffer(initKernel, "_NeighborPayloadBuffer", vol.PayloadBuffer);
                analysisShader.SetBuffer(initKernel, "_NeighborBrickDataBuffer", vol.BrickDataBuffer);
                analysisShader.SetBuffer(initKernel, "_NeighborPageTableBuffer", vol.BufferManager.PageTableBuffer);
            }

            // Group count = brickCount. Each group handles 1 brick (64 threads).
            analysisShader.Dispatch(initKernel, brickCount, 1, 1);

            // Start Propagation
            _currentPropagationIterations = 0;
            RunPropagationPass(vol, brickCount);
        }

        private const int PROPAGATION_BATCH_SIZE = 64;

        private void RunPropagationPass(VoxelVolume vol, int brickCount)
        {
            _changeFlagBuffer.SetData(new uint[] { 0 });

            int propKernel = analysisShader.FindKernel("PropagateStability");
            analysisShader.SetBuffer(propKernel, "_ActiveBricksInput", _activeBrickBuffer);
            analysisShader.SetBuffer(propKernel, "_TopologyBuffer", _topologyBuffer);
            analysisShader.SetBuffer(propKernel, "_StabilityBuffer", _stabilityBuffer);
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
            int collectKernel = analysisShader.FindKernel("CollectFloating");
            analysisShader.SetBuffer(collectKernel, "_ActiveBricksInput", _activeBrickBuffer);
            analysisShader.SetBuffer(collectKernel, "_TopologyBuffer", _topologyBuffer);
            analysisShader.SetBuffer(collectKernel, "_StabilityBuffer", _stabilityBuffer);
            analysisShader.SetBuffer(collectKernel, "_FloatingVoxelOutput", _floatingVoxelOutput);
            analysisShader.SetInt("_Resolution", vol.Resolution);

            analysisShader.Dispatch(collectKernel, brickCount, 1, 1);

            // Read count
            ComputeBuffer countBuf = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.IndirectArguments);
            ComputeBuffer.CopyCount(_floatingVoxelOutput, countBuf, 0);

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
                AsyncGPUReadback.Request(_floatingVoxelOutput, (req) => OnFinalDataReadback(req, count, vol));
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
                var data = request.GetData<float3>();
                float scale = vol.WorldSize / vol.Resolution;
                
                int readCount = Mathf.Min(count, data.Length);
                List<Vector3> volumeFloatingVoxels = new List<Vector3>();
                
                for (int i = 0; i < readCount; i++)
                {
                    float3 voxelPos = data[i];
                    Vector3 local = new Vector3(voxelPos.x + 0.5f, voxelPos.y + 0.5f, voxelPos.z + 0.5f);
                    Vector3 worldPos = vol.WorldOrigin + (local * scale);
                    volumeFloatingVoxels.Add(worldPos);
                    _floatingVoxelPositions.Add(worldPos);
                }

                if (volumeFloatingVoxels.Count > 0)
                {
                    OnAnalysisCompleted?.Invoke(vol, volumeFloatingVoxels);
                }
            }

            CleanupCurrentBuffers();
            ProcessNextVolume();
        }

        private void CleanupCurrentBuffers()
        {
            if (_topologyBuffer != null) _topologyBuffer.Release();
            if (_activeBrickBuffer != null) _activeBrickBuffer.Release();
            if (_stabilityBuffer != null) _stabilityBuffer.Release();
            if (_changeFlagBuffer != null) _changeFlagBuffer.Release();
            if (_floatingVoxelOutput != null) _floatingVoxelOutput.Release();
            
            _topologyBuffer = null;
            _activeBrickBuffer = null;
            _stabilityBuffer = null;
            _changeFlagBuffer = null;
            _floatingVoxelOutput = null;
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