using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using System.Collections.Generic;
using VoxelEngine.Core;

namespace VoxelEngine.Core.Editing
{
    public class StructuralIntegrityAnalyzer : MonoBehaviour
    {
        public ComputeShader analysisShader;
        
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
            
            // 4 bytes per voxel for stability (1 = Stable, 0 = Unstable)
            _stabilityBuffer = new ComputeBuffer(totalVoxels, 4); 
            // Initialize with 0? Or rely on Init kernel. 
            // It's safer to clear or rely on Init covering everything.
            // InitStability only runs on Active Bricks.
            // We need to clear it first because we sparsely write to it? 
            // Actually, if we only check IsSolid && Stability==0, and IsSolid implies ActiveBrick, then valid voxels are covered.
            // But to be safe against garbage data:
            // _stabilityBuffer.SetData(new uint[totalVoxels]); // Slow on CPU for large vol?
            // Let's assume InitStability and Logic covers it. 
            // Actually, if a voxel is NOT in an active brick, it is NOT solid, so it won't trigger floating check.
            
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
                if (count > data.Length)
                {
                     Debug.LogWarning($"[Structural Analysis] Floating voxel count ({count}) exceeded buffer size ({data.Length}). Truncating.");
                }

                // Only copy the valid elements
                // Note: GetData returns the whole buffer usually. We loop up to count.
                for (int i = 0; i < readCount; i++)
                {
                    float3 voxelPos = data[i];
                    Vector3 local = new Vector3(voxelPos.x + 0.5f, voxelPos.y + 0.5f, voxelPos.z + 0.5f);
                    Vector3 worldPos = vol.WorldOrigin + (local * scale);
                    _floatingVoxelPositions.Add(worldPos);
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
