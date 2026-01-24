using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine.Core.Editing;
using VoxelEngine.Core.Data;
using Unity.Collections;

namespace VoxelEngine.Core.Analysis
{
    public class VoxelConnectivityManager : MonoBehaviour
    {
        public static VoxelConnectivityManager Instance { get; private set; }

        [Header("Settings")]
        public ComputeShader connectivityCompute;
        public int maxIterations = 64; // Approx propagation distance
        
        [Header("Debug")]
        public bool visualizeIslands = true;
        
        // internal buffers
        private ComputeBuffer _connectivityMap;
        private ComputeBuffer _islandVoxels;
        private ComputeBuffer _islandCounter;
        private ComputeBuffer _inputVoxelBuffer;

        private void Awake()
        {
            if (Instance != null && Instance != this) Destroy(this);
            Instance = this;
        }

        /// <summary>
        /// Analyzes a specific region of voxels (e.g. a chunk or brick) for floating islands.
        /// </summary>
        /// <param name="voxelData">The packed voxel data (uints) of the region.</param>
        /// <param name="size">Dimensions of the region (e.g., 6x6x6 for a brick).</param>
        /// <param name="callback">Action to receive the list of disconnected voxel indices.</param>
        public void AnalyzeConnectivity(uint[] voxelData, Vector3Int size, Action<int[]> callback)
        {
            if (connectivityCompute == null || voxelData == null || voxelData.Length == 0) return;

            int totalVoxels = size.x * size.y * size.z;
            if (voxelData.Length != totalVoxels)
            {
                // Just warn, don't crash, handle mismatch if possible or abort
                // Debug.LogWarning("Voxel data length mismatch for connectivity analysis.");
                // For now, assume brick size if mismatch
                if (totalVoxels != SVONode.BRICK_VOXEL_COUNT) return; 
            }

            // 1. Setup Buffers
            if (_connectivityMap == null || _connectivityMap.count < totalVoxels)
            {
                ReleaseBuffers();
                _connectivityMap = new ComputeBuffer(totalVoxels, 4);
                _islandVoxels = new ComputeBuffer(totalVoxels, 4);
                _islandCounter = new ComputeBuffer(1, 4);
                _inputVoxelBuffer = new ComputeBuffer(totalVoxels, 4);
            }

            _inputVoxelBuffer.SetData(voxelData);
            _islandCounter.SetData(new uint[] { 0 });

            // 2. Initialize
            int kInit = connectivityCompute.FindKernel("InitializeAnalysis");
            connectivityCompute.SetBuffer(kInit, "_InputVoxelBuffer", _inputVoxelBuffer);
            connectivityCompute.SetBuffer(kInit, "_ConnectivityMap", _connectivityMap);
            
            // Set Region Params
            connectivityCompute.SetInts("_RegionSize", new int[] { size.x, size.y, size.z });
            
            int groupsX = Mathf.CeilToInt(size.x / 8.0f);
            int groupsY = Mathf.CeilToInt(size.y / 8.0f);
            int groupsZ = Mathf.CeilToInt(size.z / 8.0f);
            
            connectivityCompute.Dispatch(kInit, groupsX, groupsY, groupsZ);

            // 3. Flood Fill Propagation
            int kFlood = connectivityCompute.FindKernel("FloodFill");
            connectivityCompute.SetBuffer(kFlood, "_ConnectivityMap", _connectivityMap);
            connectivityCompute.SetInts("_RegionSize", new int[] { size.x, size.y, size.z });

            // Dispatch loop for propagation
            // Number of threads = 3D Dispatch
            // Using same groups as Initialize/Identify
            int iterations = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * 2;
            
            for (int i = 0; i < iterations; i++)
            {
                connectivityCompute.Dispatch(kFlood, groupsX, groupsY, groupsZ);
            }

            // 4. Identify Islands
            int kIdent = connectivityCompute.FindKernel("IdentifyIslands");
            connectivityCompute.SetBuffer(kIdent, "_ConnectivityMap", _connectivityMap);
            connectivityCompute.SetBuffer(kIdent, "_IslandVoxels", _islandVoxels);
            connectivityCompute.SetBuffer(kIdent, "_IslandCounter", _islandCounter);
            connectivityCompute.SetInts("_RegionSize", new int[] { size.x, size.y, size.z });

            connectivityCompute.Dispatch(kIdent, groupsX, groupsY, groupsZ);

            // 5. Async Readback
            // Capture size for gizmo callback
            Vector3Int regionSize = size;
            
            AsyncGPUReadback.Request(_islandCounter, (request) =>
            {
                if (request.hasError) return;
                
                uint count = request.GetData<uint>()[0];
                if (count > 0)
                {
                    // Read back the indices
                    AsyncGPUReadback.Request(_islandVoxels, 0, (int)count * 4, (req) => 
                    {
                        if (req.hasError) return;
                        var indices = req.GetData<int>().ToArray();
                        
                        // Debug Visualization
                        if (visualizeIslands)
                        {
                            UpdateDebugGizmos(indices, regionSize);
                        }
                        
                        callback?.Invoke(indices);
                    });
                }
                else
                {
                    callback?.Invoke(new int[0]);
                }
            });
        }

        // --- Debug Gizmos ---
        private List<Vector3> _debugPoints = new List<Vector3>();
        private void UpdateDebugGizmos(int[] indices, Vector3Int size)
        {
            _debugPoints.Clear();
            // Just assume local origin for now, ideally pass world origin to AnalyzeConnectivity
            // But since this is a global singleton, we don't know the exact world pos here easily 
            // without passing it through. For now, we'll just show them at specific debug coords or 
            // require the caller to handle visualization. 
            
            // Actually, let's just log for now to confirm readback, 
            // or better: store relative coords and draw at (0,0,0) so the user can see *something* 
            // if they look at the origin, OR ask caller to visualize.
            
            // Let's implement a simple World Space Debugger separate from this manager, 
            // OR store the last analyzed WorldOrigin.
            
            // For now: Just store local points relative to "Last Analyzed Brick".
            foreach (var idx in indices)
            {
                int x = idx % size.x;
                int y = (idx / size.x) % size.y;
                int z = idx / (size.x * size.y);
                _debugPoints.Add(new Vector3(x, y, z));
            }
        }
        
        // Caller needs to set this if we want accurate world gizmos
        public Vector3 LastAnalyzedWorldOrigin { get; set; }

        private void OnDrawGizmos()
        {
            if (!visualizeIslands || _debugPoints.Count == 0) return;

            Gizmos.color = Color.red;
            Vector3 origin = LastAnalyzedWorldOrigin;
            // If origin is zero, it might be inside the ground, but let's draw anyway
            
            float scale = VoxelEditManager.Instance != null ? VoxelEditManager.Instance.voxelSize : 1.0f;
            Vector3 size = Vector3.one * scale * 0.9f;

            foreach (var p in _debugPoints)
            {
                Vector3 worldPos = origin + p * scale + Vector3.one * (scale * 0.5f);
                Gizmos.DrawWireCube(worldPos, size);
            }
        }

        private void ReleaseBuffers()
        {
            _connectivityMap?.Release();
            _islandVoxels?.Release();
            _islandCounter?.Release();
            _inputVoxelBuffer?.Release();
        }

        private void OnDestroy()
        {
            ReleaseBuffers();
        }
    }
}