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
        
        // Global Analysis State
        private Dictionary<VoxelVolume, NativeArray<uint>> _volumeData = new Dictionary<VoxelVolume, NativeArray<uint>>();
        private int _pendingReadbacks = 0;
        private Dictionary<Vector3Int, VoxelVolume> _chunkMap = new Dictionary<Vector3Int, VoxelVolume>();

        private Bounds? _currentQueryBounds;

        public void AnalyzeWorld(Bounds? queryBounds = null)
        {
            if (analysisShader == null) return;
            
            _currentQueryBounds = queryBounds;
            
            // 1. Reset State
            foreach(var kvp in _volumeData) { if(kvp.Value.IsCreated) kvp.Value.Dispose(); }
            _volumeData.Clear();
            _chunkMap.Clear();
            
            var volumes = VoxelVolumeRegistry.Volumes;
            if (volumes.Count == 0) return;

            _pendingReadbacks = 0;
            
            // 2. Dispatch for all volumes
            foreach (var vol in volumes)
            {
                if (!vol.gameObject.activeInHierarchy || !vol.IsReady) continue;

                // Map Chunk Coordinate for neighbor lookup
                // Assuming volume is axis aligned and integer coordinates match typical chunking
                // We use WorldOrigin rounded to nearest integer as key logic, or better:
                // If VoxelEditManager.Instance exists, use its helper, otherwise manual.
                // Assuming standard grid snapping:
                Vector3Int coord = Vector3Int.RoundToInt(vol.WorldOrigin); 
                if (!_chunkMap.ContainsKey(coord)) _chunkMap.Add(coord, vol);

                DispatchVolume(vol);
            }
        }
        
        private void DispatchVolume(VoxelVolume volume)
        {
            int res = volume.Resolution;
            int totalVoxels = res * res * res;
            int bufferSize = Mathf.CeilToInt(totalVoxels / 32.0f);

            ComputeBuffer topologyBuffer = new ComputeBuffer(bufferSize, 4);
            // Clear buffer (0 = Air) -> relying on driver/API default or explict clear if needed?
            // Safer to clear:
            uint[] clearData = new uint[bufferSize];
            topologyBuffer.SetData(clearData); 

            int kernel = analysisShader.FindKernel("ExtractTopology");

            analysisShader.SetBuffer(kernel, "_GlobalNodeBuffer", volume.NodeBuffer);
            analysisShader.SetBuffer(kernel, "_GlobalPayloadBuffer", volume.PayloadBuffer);
            analysisShader.SetBuffer(kernel, "_GlobalBrickDataBuffer", volume.BrickDataBuffer);
            analysisShader.SetBuffer(kernel, "_PageTableBuffer", volume.BufferManager.PageTableBuffer);
            analysisShader.SetBuffer(kernel, "_TopologyBuffer", topologyBuffer);

            analysisShader.SetInt("_Resolution", res);
            analysisShader.SetInt("_PageTableOffset", volume.BufferManager.PageTableOffset);
            analysisShader.SetInt("_BrickOffset", volume.BufferManager.BrickDataOffset);

            int threadGroups = Mathf.CeilToInt(res / 8.0f);
            analysisShader.Dispatch(kernel, threadGroups, threadGroups, threadGroups);

            _pendingReadbacks++;
            AsyncGPUReadback.Request(topologyBuffer, (request) => OnReadback(request, topologyBuffer, volume));
        }

        private void OnReadback(AsyncGPUReadbackRequest request, ComputeBuffer bufferToRelease, VoxelVolume vol)
        {
            bufferToRelease.Release();
            _pendingReadbacks--;

            if (request.hasError)
            {
                Debug.LogError($"Analysis Readback Failed for {vol.name}");
            }
            else
            {
                // Store data (persistent allocator needed since we wait for others)
                NativeArray<uint> raw = request.GetData<uint>();
                NativeArray<uint> persistentCopy = new NativeArray<uint>(raw, Allocator.Persistent);
                
                lock(_volumeData)
                {
                    _volumeData[vol] = persistentCopy;
                }
            }
            
            if (_pendingReadbacks <= 0)
            {
                PerformGlobalDFS();
            }
        }

        private void PerformGlobalDFS()
        {
            _floatingVoxelPositions.Clear();
            
            // Global visited set for floating clusters to avoid duplicates
            // We use a string key "VolName_Index" or similar, but Dictionary<Vol, BitArray> is better.
            Dictionary<VoxelVolume, System.Collections.BitArray> globalFloatingVisited = new Dictionary<VoxelVolume, System.Collections.BitArray>();
            
            foreach(var kvp in _volumeData)
            {
                int total = kvp.Key.Resolution * kvp.Key.Resolution * kvp.Key.Resolution;
                globalFloatingVisited[kvp.Key] = new System.Collections.BitArray(total);
            }

            int floatingCount = 0;
            
            // Define Seeds
            // If Bounds are provided, we only seed from Solid voxels within the Bounds.
            // Otherwise, we iterate the world (Full Scan).
            
            List<(VoxelVolume, int)> seeds = new List<(VoxelVolume, int)>();

            if (_currentQueryBounds.HasValue)
            {
                Bounds query = _currentQueryBounds.Value;
                // Dilate query slightly to capture surface
                query.Expand(2.0f); 

                foreach (var kvp in _volumeData)
                {
                    VoxelVolume vol = kvp.Key;
                    if (!vol.WorldBounds.Intersects(query)) continue;
                    
                    NativeArray<uint> data = kvp.Value;
                    int res = vol.Resolution;
                    float voxelSize = vol.WorldSize / res;
                    
                    // Convert World Bounds to Local Voxel Range
                    Vector3 localMin = vol.transform.InverseTransformPoint(query.min);
                    Vector3 localMax = vol.transform.InverseTransformPoint(query.max);
                    
                    // Handle rotation/scale implications on AABB (simplified)
                    Vector3 min = Vector3.Min(localMin, localMax);
                    Vector3 max = Vector3.Max(localMin, localMax);
                    
                    int3 minIdx = (int3)math.floor(min / voxelSize);
                    int3 maxIdx = (int3)math.ceil(max / voxelSize);
                    
                    minIdx = math.clamp(minIdx, 0, res - 1);
                    maxIdx = math.clamp(maxIdx, 0, res - 1);
                    
                    for (int z = minIdx.z; z <= maxIdx.z; z++)
                    for (int y = minIdx.y; y <= maxIdx.y; y++)
                    for (int x = minIdx.x; x <= maxIdx.x; x++)
                    {
                        int idx = z * (res * res) + y * res + x;
                        if (IsSolid(data, idx))
                        {
                            seeds.Add((vol, idx));
                        }
                    }
                }
            }
            else
            {
                // Full Scan
                foreach (var kvp in _volumeData)
                {
                    VoxelVolume vol = kvp.Key;
                    NativeArray<uint> data = kvp.Value;
                    int total = vol.Resolution * vol.Resolution * vol.Resolution;
                    for (int i = 0; i < total; i++)
                    {
                        if (IsSolid(data, i)) seeds.Add((vol, i));
                    }
                }
            }

            // Local Visited Set for the current search (to handle Pruning correctly)
            // If we prune, we don't mark global visited. 
            // If we find floating, we mark global visited.
            
            // Optimization: If full scan, we use global visited strictly.
            // If partial scan, we need local visited for pruned branches.
            
            foreach (var seed in seeds)
            {
                VoxelVolume seedVol = seed.Item1;
                int seedIdx = seed.Item2;
                
                // Already processed as part of a Floating cluster?
                if (globalFloatingVisited[seedVol][seedIdx]) continue;
                
                // Start DFS
                List<(VoxelVolume, int)> currentComponent = new List<(VoxelVolume, int)>();
                
                // We need a way to track visited for THIS traversal.
                // Reusing global visited for grounded paths is dangerous if we prune.
                // So we use a temporary HashSet for the current path.
                HashSet<(VoxelVolume, int)> pathVisited = new HashSet<(VoxelVolume, int)>();
                
                bool isGrounded = false;
                
                Stack<(VoxelVolume, int)> stack = new Stack<(VoxelVolume, int)>();
                stack.Push((seedVol, seedIdx));
                pathVisited.Add((seedVol, seedIdx));
                
                while(stack.Count > 0)
                {
                    var current = stack.Pop();
                    VoxelVolume cVol = current.Item1;
                    int cIdx = current.Item2;
                    
                    currentComponent.Add(current);
                    
                    // Check Grounded
                    Vector3 worldPos = GetWorldPos(cVol, cIdx);
                    if (worldPos.y <= 10.0f) 
                    {
                        isGrounded = true;
                        break; // PRUNE: Stop searching this component immediately.
                    }
                    
                    // Neighbors
                    // Bias: We want to go DOWN first. Stack is LIFO.
                    // So we push UP/SIDES first, and DOWN last.
                    CheckNeighborsBias(cVol, cIdx, stack, pathVisited, globalFloatingVisited);
                }
                
                if (!isGrounded)
                {
                    // It's Floating!
                    floatingCount += currentComponent.Count;
                    foreach(var item in currentComponent)
                    {
                        _floatingVoxelPositions.Add(GetWorldPos(item.Item1, item.Item2));
                        // Mark as visited globally so we don't re-scan this island
                        globalFloatingVisited[item.Item1][item.Item2] = true;
                    }
                }
                else
                {
                    // It's Grounded (and Pruned).
                    // We discard the 'pathVisited' implicitly.
                    // Next seed might re-traverse part of this, but will also prune quickly.
                }
            }
            
            Debug.Log($"[Structural Analysis] Scan Complete. Seeds: {seeds.Count}. Floating Voxels: {floatingCount}");

            // Cleanup NativeArrays
            foreach(var kvp in _volumeData)
            {
                if(kvp.Value.IsCreated) kvp.Value.Dispose();
            }
            _volumeData.Clear();
        }
        
        private void CheckNeighborsBias(VoxelVolume vol, int idx, Stack<(VoxelVolume, int)> stack, HashSet<(VoxelVolume, int)> pathVisited, Dictionary<VoxelVolume, System.Collections.BitArray> globalVisited)
        {
            int res = vol.Resolution;
            int z = idx / (res * res);
            int rem = idx % (res * res);
            int y = rem / res;
            int x = rem % res;
            
            // LIFO Order: Push least preferred first.
            // Preferred: Down (Y-1).
            // Order: Up, Sides, Down.
            
            CheckDir(x, y + 1, z); // Up
            
            CheckDir(x + 1, y, z);
            CheckDir(x - 1, y, z);
            CheckDir(x, y, z + 1);
            CheckDir(x, y, z - 1);
            
            CheckDir(x, y - 1, z); // Down (Popped first)
            
            void CheckDir(int nx, int ny, int nz)
            {
                VoxelVolume targetVol = vol;
                int tx = nx, ty = ny, tz = nz;
                
                // Cross-chunk Logic
                bool crossed = false;
                Vector3Int neighborOffset = Vector3Int.zero;

                if (nx >= res) { neighborOffset.x = 1; tx = 0; crossed = true; }
                else if (nx < 0) { neighborOffset.x = -1; tx = res - 1; crossed = true; }
                
                if (ny >= res) { neighborOffset.y = 1; ty = 0; crossed = true; }
                else if (ny < 0) { neighborOffset.y = -1; ty = res - 1; crossed = true; }
                
                if (nz >= res) { neighborOffset.z = 1; tz = 0; crossed = true; }
                else if (nz < 0) { neighborOffset.z = -1; tz = res - 1; crossed = true; }
                
                if (crossed)
                {
                    Vector3Int currentCoord = Vector3Int.RoundToInt(vol.WorldOrigin);
                    int step = Mathf.RoundToInt(vol.WorldSize); 
                    Vector3Int targetCoord = currentCoord + (neighborOffset * step);
                    
                    if (!_chunkMap.TryGetValue(targetCoord, out targetVol)) return;
                }
                
                // Check Bounds
                if (tx >= 0 && tx < targetVol.Resolution && 
                    ty >= 0 && ty < targetVol.Resolution && 
                    tz >= 0 && tz < targetVol.Resolution)
                {
                    int tIdx = tz * (res * res) + ty * res + tx;
                    var key = (targetVol, tIdx);
                    
                    // Check if already visited in this path OR globally visited as floating
                    if (pathVisited.Contains(key)) return;
                    if (globalVisited.ContainsKey(targetVol) && globalVisited[targetVol][tIdx]) return;
                    
                    // Check Solidity
                    if (_volumeData.ContainsKey(targetVol) && IsSolid(_volumeData[targetVol], tIdx))
                    {
                        pathVisited.Add(key);
                        stack.Push(key);
                    }
                }
            }
        }

        private bool IsSolid(NativeArray<uint> data, int idx)
        {
            if (!data.IsCreated) return false;
            return (data[idx / 32] & (1u << (idx % 32))) != 0;
        }

        private Vector3 GetWorldPos(VoxelVolume vol, int idx)
        {
            int res = vol.Resolution;
            int z = idx / (res * res);
            int rem = idx % (res * res);
            int y = rem / res;
            int x = rem % res;
            
            Vector3 local = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            // Local to World requires scale factor
            float scale = vol.WorldSize / res;
            return vol.WorldOrigin + (local * scale);
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
