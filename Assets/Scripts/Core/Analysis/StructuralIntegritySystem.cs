using System.Collections;
using System.Collections.Generic; // Added for List<T>
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;
using VoxelEngine.Core.Streaming;
using VoxelEngine.Core.Editing;

namespace VoxelEngine.Core.Analysis
{
    public class StructuralIntegritySystem : MonoSingleton<StructuralIntegritySystem>
    {
        [Header("Configuration")]
        public ComputeShader integrityCompute;
        [Tooltip("Margin around the brush bounds to check for connectivity.")]
        public float analysisMargin = 2.0f; 
        [Header("Debug")]
        public bool drawDebugGizmos = true;

        // Buffers
        private ComputeBuffer _resultBuffer;
        private NativeArray<uint> _readbackArray;

        // Debug Data
        private List<Vector3> _debugFloatingPositions = new List<Vector3>();
        private Vector3 _lastBoundsMin;
        private float _lastVoxelSize;

        public void Analyze(Bounds bounds)
        {
            StartCoroutine(AnalyzeRoutine(bounds));
        }

        private IEnumerator AnalyzeRoutine(Bounds bounds)
        {
            // 1. Wait for EndOfFrame to ensure WorldManager has regenerated the chunks
            yield return new WaitForEndOfFrame();
            
            if (VoxelVolumePool.Instance == null || VoxelVolumePool.Instance.ActiveChunkCount == 0) yield break;

            // 2. Expand bounds
            bounds.Expand(analysisMargin);
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            float voxelSize = VoxelEditManager.Instance.voxelSize;

            // Store for Debugging
            _lastBoundsMin = min;
            _lastVoxelSize = voxelSize;

            // 3. Calculate Resolution
            int resX = Mathf.CeilToInt(bounds.size.x / voxelSize);
            int resY = Mathf.CeilToInt(bounds.size.y / voxelSize);
            int resZ = Mathf.CeilToInt(bounds.size.z / voxelSize);
            int totalVoxels = resX * resY * resZ;

            if (totalVoxels == 0) yield break;

            // 4. Setup Compute
            if (_resultBuffer != null) _resultBuffer.Release();
            _resultBuffer = new ComputeBuffer(totalVoxels, sizeof(uint));

            int kernel = integrityCompute.FindKernel("ExtractRegion");
            var pool = VoxelVolumePool.Instance;

            integrityCompute.SetBuffer(kernel, "_GlobalNodeBuffer", pool.GlobalNodeBuffer);
            integrityCompute.SetBuffer(kernel, "_GlobalPayloadBuffer", pool.GlobalPayloadBuffer);
            integrityCompute.SetBuffer(kernel, "_GlobalBrickDataBuffer", pool.GlobalBrickDataBuffer);
            integrityCompute.SetBuffer(kernel, "_ChunkBuffer", pool.ChunkBuffer);
            integrityCompute.SetBuffer(kernel, "_TLASGridBuffer", pool.TLASGridBuffer);
            integrityCompute.SetBuffer(kernel, "_TLASChunkIndexBuffer", pool.TLASChunkIndexBuffer);
            
            integrityCompute.SetVector("_TLASBoundsMin", pool.TLASBoundsMin);
            integrityCompute.SetVector("_TLASBoundsMax", pool.TLASBoundsMax);
            integrityCompute.SetInt("_TLASResolution", pool.TLASResolution);

            integrityCompute.SetVector("_BoundsMin", min);
            integrityCompute.SetVector("_BoundsMax", max);
            integrityCompute.SetInts("_Resolution", new int[] { resX, resY, resZ });
            integrityCompute.SetFloat("_VoxelSize", voxelSize);
            integrityCompute.SetBuffer(kernel, "_ResultBuffer", _resultBuffer);

            int groupsX = Mathf.CeilToInt(resX / 8.0f);
            int groupsY = Mathf.CeilToInt(resY / 8.0f);
            int groupsZ = Mathf.CeilToInt(resZ / 8.0f);
            integrityCompute.Dispatch(kernel, groupsX, groupsY, groupsZ);

            // Fix: Allocate the NativeArray before requesting data into it.
            // RequestIntoNativeArray writes directly to this memory, so it must exist.
            if (!_readbackArray.IsCreated || _readbackArray.Length != totalVoxels)
            {
                if (_readbackArray.IsCreated) _readbackArray.Dispose();
                _readbackArray = new NativeArray<uint>(totalVoxels, Allocator.Persistent);
            }

            AsyncGPUReadback.RequestIntoNativeArray(ref _readbackArray, _resultBuffer, (request) => OnReadbackComplete(request, resX, resY, resZ));
        }

        private void OnReadbackComplete(AsyncGPUReadbackRequest request, int w, int h, int d)
        {
            if (request.hasError) return;

            var resultFloatingIndices = new NativeList<int>(Allocator.TempJob);
            var resultFloatingCount = new NativeArray<int>(1, Allocator.TempJob);
            
            var job = new IntegrityAnalysisJob
            {
                voxels = _readbackArray,
                dims = new int3(w, h, d),
                floatingCount = resultFloatingCount,
                floatingIndices = resultFloatingIndices
            };

            job.Schedule().Complete();

            int floating = resultFloatingCount[0];
            
            // --- Update Debug Gizmos ---
            _debugFloatingPositions.Clear();
            if (floating > 0 && drawDebugGizmos)
            {
                Debug.LogWarning($"[Structural Integrity] Detected {floating} floating voxels!");
                for (int i = 0; i < resultFloatingIndices.Length; i++)
                {
                    int idx = resultFloatingIndices[i];
                    int z = idx / (w * h);
                    int rem = idx % (w * h);
                    int y = rem / w;
                    int x = rem % w;

                    Vector3 localPos = new Vector3(x, y, z) * _lastVoxelSize + (_lastVoxelSize * 0.5f) * Vector3.one;
                    _debugFloatingPositions.Add(_lastBoundsMin + localPos);
                }
            }
            // ---------------------------

            resultFloatingCount.Dispose();
            resultFloatingIndices.Dispose();
        }

        private void OnDrawGizmos()
        {
            if (!drawDebugGizmos || _debugFloatingPositions == null) return;

            Gizmos.color = Color.red;
            float size = _lastVoxelSize * 0.9f;
            foreach (var pos in _debugFloatingPositions)
            {
                Gizmos.DrawWireCube(pos, Vector3.one * size);
            }
        }

        private void OnDestroy()
        {
            if (_resultBuffer != null) _resultBuffer.Release();
            if (_readbackArray.IsCreated) _readbackArray.Dispose();
        }
    }

    [BurstCompile]
    public struct IntegrityAnalysisJob : IJob
    {
        [ReadOnly] public NativeArray<uint> voxels;
        public int3 dims;
        [WriteOnly] public NativeArray<int> floatingCount;
        [WriteOnly] public NativeList<int> floatingIndices; // Added to store specific floating voxels

        public void Execute()
        {
            int w = dims.x;
            int h = dims.y;
            int d = dims.z;
            int total = w * h * d;

            var visited = new NativeArray<bool>(total, Allocator.Temp);
            var queue = new NativeQueue<int>(Allocator.Temp);

            // 1. Find Anchors (Voxels on the boundary)
            for (int z = 0; z < d; z++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        bool isBoundary = (x == 0 || x == w - 1 || y == 0 || y == h - 1 || z == 0 || z == d - 1);
                        if (isBoundary)
                        {
                            int idx = z * (w * h) + y * w + x;
                            if (voxels[idx] == 1) 
                            {
                                visited[idx] = true;
                                queue.Enqueue(idx);
                            }
                        }
                    }
                }
            }

            // 2. Flood Fill
            var neighbors = new NativeArray<int3>(6, Allocator.Temp);
            neighbors[0] = new int3(1, 0, 0); neighbors[1] = new int3(-1, 0, 0);
            neighbors[2] = new int3(0, 1, 0); neighbors[3] = new int3(0, -1, 0);
            neighbors[4] = new int3(0, 0, 1); neighbors[5] = new int3(0, 0, -1);

            while (!queue.IsEmpty())
            {
                int currIdx = queue.Dequeue();
                
                int cz = currIdx / (w * h);
                int rem = currIdx % (w * h);
                int cy = rem / w;
                int cx = rem % w;
                int3 currPos = new int3(cx, cy, cz);

                for (int i = 0; i < 6; i++)
                {
                    int3 nPos = currPos + neighbors[i];
                    if (nPos.x >= 0 && nPos.x < w && nPos.y >= 0 && nPos.y < h && nPos.z >= 0 && nPos.z < d)
                    {
                        int nIdx = nPos.z * (w * h) + nPos.y * w + nPos.x;
                        if (!visited[nIdx] && voxels[nIdx] == 1)
                        {
                            visited[nIdx] = true;
                            queue.Enqueue(nIdx);
                        }
                    }
                }
            }

            // 3. Count Floating
            int floating = 0;
            for (int i = 0; i < total; i++)
            {
                if (voxels[i] == 1 && !visited[i])
                {
                    floating++;
                    floatingIndices.Add(i);
                }
            }
            floatingCount[0] = floating;

            neighbors.Dispose();
            visited.Dispose();
            queue.Dispose();
        }
    }
}