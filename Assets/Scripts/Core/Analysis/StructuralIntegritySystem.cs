using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;
using VoxelEngine.Core.Streaming;
using VoxelEngine.Core.Editing;
using VoxelEngine.Core.Generators;
using VoxelEngine.Core.Data;

namespace VoxelEngine.Core.Analysis
{
    public class StructuralIntegritySystem : MonoSingleton<StructuralIntegritySystem>
    {
        [Header("Configuration")]
        public ComputeShader integrityCompute;
        public ComputeShader extractorCompute;
        public GameObject dynamicBodyPrefab;
        public float analysisMargin = 2.0f; 
        
        [Header("Debug")]
        public bool drawDebugGizmos = true;

        private ComputeBuffer _resultBuffer;
        private NativeArray<uint> _readbackArray;
        private List<Vector3> _debugFloatingPositions = new List<Vector3>();
        private Vector3 _lastBoundsMin;
        private float _lastVoxelSize;

        public void Analyze(Bounds bounds)
        {
            StartCoroutine(AnalyzeRoutine(bounds));
        }

        private IEnumerator AnalyzeRoutine(Bounds bounds)
        {
            yield return new WaitForEndOfFrame();
            
            if (VoxelVolumePool.Instance == null || VoxelVolumePool.Instance.ActiveChunkCount == 0) yield break;

            bounds.Expand(analysisMargin);
            Vector3 min = bounds.min;
            float voxelSize = VoxelEditManager.Instance.voxelSize;
            _lastBoundsMin = min;
            _lastVoxelSize = voxelSize;

            int resX = Mathf.CeilToInt(bounds.size.x / voxelSize);
            int resY = Mathf.CeilToInt(bounds.size.y / voxelSize);
            int resZ = Mathf.CeilToInt(bounds.size.z / voxelSize);
            int totalVoxels = resX * resY * resZ;

            if (totalVoxels == 0) yield break;

            // --- Step 1: Integrity Check (0/1) ---
            if (_resultBuffer != null) _resultBuffer.Release();
            _resultBuffer = new ComputeBuffer(totalVoxels, sizeof(uint));

            int kernel = integrityCompute.FindKernel("ExtractRegion");
            var pool = VoxelVolumePool.Instance;
            
            // Bind buffers
            BindIntegrityBuffers(kernel, pool, min, resX, resY, resZ, voxelSize);
            
            int gx = Mathf.CeilToInt(resX/8f), gy = Mathf.CeilToInt(resY/8f), gz = Mathf.CeilToInt(resZ/8f);
            integrityCompute.Dispatch(kernel, gx, gy, gz);

            if (!_readbackArray.IsCreated || _readbackArray.Length != totalVoxels)
            {
                if (_readbackArray.IsCreated) _readbackArray.Dispose();
                _readbackArray = new NativeArray<uint>(totalVoxels, Allocator.Persistent);
            }

            // --- Step 2: Readback and Identify Floating Cluster ---
            bool requestDone = false;
            AsyncGPUReadback.RequestIntoNativeArray(ref _readbackArray, _resultBuffer, (req) => requestDone = true);
            while (!requestDone) yield return null;

            var resultFloatingCount = new NativeArray<int>(1, Allocator.TempJob);
            var resultFloatingIndices = new NativeList<int>(Allocator.TempJob);
            
            var job = new IntegrityAnalysisJob
            {
                voxels = _readbackArray,
                dims = new int3(resX, resY, resZ),
                floatingCount = resultFloatingCount,
                floatingIndices = resultFloatingIndices
            };
            job.Schedule().Complete();

            int floatingCount = resultFloatingCount[0];
            
            // --- Step 3: TEARDOWN PHASE ---
            if (floatingCount > 0)
            {
                Debug.Log($"[Integrity] Found {floatingCount} floating voxels. Initiating Teardown.");
                
                // 3a. Calculate Bounds of floating voxels
                Bounds floatingBounds = CalculateFloatingBounds(resultFloatingIndices, resX, resY, min, voxelSize);
                
                // 3b. Extract Data
                ComputeBuffer gridData = ExtractFloatingData(floatingBounds);

                // 3c. Erase from World (Subtract)
                // [FIX] Expand the subtraction box slightly. Exact bounds can leave artifacts at the 0.0 SDF surface.
                Vector3 subtractSize = floatingBounds.size + Vector3.one * 0.25f;
                Bounds subtractBounds = new Bounds(floatingBounds.center, subtractSize);

                SDFObject subtractor = new SDFObject
                {
                    type = 1, // Cube
                    operation = 1, // Subtract
                    boundsMin = subtractBounds.min,
                    boundsMax = subtractBounds.max,
                    position = subtractBounds.center,
                    rotation = Quaternion.identity,
                    scale = subtractSize,
                    blendFactor = 0.5f,
                    materialId = 1
                };

                if (DynamicSDFManager.Instance != null)
                {
                    DynamicSDFManager.Instance.RegisterObject(subtractor);
                    
                    // [FIX] Force a buffer rebuild immediately.
                    // If WorldManager updates before DynamicSDFManager in the next frame loop,
                    // it would see the dirty region but use old GPU buffers, failing to delete the chunks.
                    DynamicSDFManager.Instance.RebuildBVH();
                }

                // 3d. Instantiate Dynamic Body
                if (dynamicBodyPrefab != null && gridData != null)
                {
                    GameObject go = Instantiate(dynamicBodyPrefab, floatingBounds.center, Quaternion.identity);
                    var body = go.GetComponent<DynamicVoxelBody>();
                    if (body != null)
                    {
                        // Use original accurate size for the mesh generation
                        body.Initialize(gridData, floatingBounds.size);
                    }
                }
                
                // Clean up temp buffer (The Body makes its own copy or uses it)
                 if (gridData != null) gridData.Release();
            }

            resultFloatingCount.Dispose();
            resultFloatingIndices.Dispose();
        }

        private void BindIntegrityBuffers(int kernel, VoxelVolumePool pool, Vector3 min, int rx, int ry, int rz, float size)
        {
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
            integrityCompute.SetInts("_Resolution", new int[] { rx, ry, rz });
            integrityCompute.SetFloat("_VoxelSize", size);
            integrityCompute.SetBuffer(kernel, "_ResultBuffer", _resultBuffer);
        }

        private Bounds CalculateFloatingBounds(NativeList<int> indices, int w, int h, Vector3 regionMin, float voxelSize)
        {
            Vector3 min = Vector3.one * float.MaxValue;
            Vector3 max = Vector3.one * float.MinValue;

            for (int i = 0; i < indices.Length; i++)
            {
                int idx = indices[i];
                int z = idx / (w * h);
                int rem = idx % (w * h);
                int y = rem / w;
                int x = rem % w;

                Vector3 pos = regionMin + new Vector3(x, y, z) * voxelSize + Vector3.one * (voxelSize * 0.5f);
                min = Vector3.Min(min, pos);
                max = Vector3.Max(max, pos);
            }
            return new Bounds((min + max) * 0.5f, max - min + Vector3.one * voxelSize);
        }

        private ComputeBuffer ExtractFloatingData(Bounds bounds)
        {
            if (extractorCompute == null) return null;
            
            float voxelSize = VoxelEditManager.Instance.voxelSize;
            int rx = Mathf.CeilToInt(bounds.size.x / voxelSize);
            int ry = Mathf.CeilToInt(bounds.size.y / voxelSize);
            int rz = Mathf.CeilToInt(bounds.size.z / voxelSize);
            int count = rx * ry * rz;

            ComputeBuffer outputGrid = new ComputeBuffer(count, sizeof(uint));
            int kernel = extractorCompute.FindKernel("ExtractGrid");
            var pool = VoxelVolumePool.Instance;

            extractorCompute.SetBuffer(kernel, "_GlobalNodeBuffer", pool.GlobalNodeBuffer);
            extractorCompute.SetBuffer(kernel, "_GlobalPayloadBuffer", pool.GlobalPayloadBuffer);
            extractorCompute.SetBuffer(kernel, "_GlobalBrickDataBuffer", pool.GlobalBrickDataBuffer);
            extractorCompute.SetBuffer(kernel, "_ChunkBuffer", pool.ChunkBuffer);
            extractorCompute.SetBuffer(kernel, "_TLASGridBuffer", pool.TLASGridBuffer);
            extractorCompute.SetBuffer(kernel, "_TLASChunkIndexBuffer", pool.TLASChunkIndexBuffer);
            extractorCompute.SetVector("_TLASBoundsMin", pool.TLASBoundsMin);
            extractorCompute.SetVector("_TLASBoundsMax", pool.TLASBoundsMax);
            extractorCompute.SetInt("_TLASResolution", pool.TLASResolution);

            extractorCompute.SetVector("_BoundsMin", bounds.min);
            extractorCompute.SetInts("_Resolution", new int[] { rx, ry, rz });
            extractorCompute.SetFloat("_VoxelSize", voxelSize);
            extractorCompute.SetBuffer(kernel, "_OutputGrid", outputGrid);

            extractorCompute.Dispatch(kernel, Mathf.CeilToInt(rx/8f), Mathf.CeilToInt(ry/8f), Mathf.CeilToInt(rz/8f));
            
            return outputGrid;
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
        [WriteOnly] public NativeList<int> floatingIndices; 

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