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
        private Dictionary<VoxelVolume, NativeArray<uint>> _volumeActiveBricks = new Dictionary<VoxelVolume, NativeArray<uint>>();
        private int _pendingReadbacks = 0;
        private Dictionary<Vector3Int, VoxelVolume> _chunkMap = new Dictionary<Vector3Int, VoxelVolume>();

        private Bounds? _currentQueryBounds;

        public void AnalyzeWorld(Bounds? queryBounds = null)
        {
            if (analysisShader == null) return;
            
            _currentQueryBounds = queryBounds;
            
            // 1. Reset State
            foreach(var kvp in _volumeData) { if(kvp.Value.IsCreated) kvp.Value.Dispose(); }
            foreach(var kvp in _volumeActiveBricks) { if(kvp.Value.IsCreated) kvp.Value.Dispose(); }
            _volumeData.Clear();
            _volumeActiveBricks.Clear();
            _chunkMap.Clear();
            
            var volumes = VoxelVolumeRegistry.Volumes;
            if (volumes.Count == 0) return;

            _pendingReadbacks = 0;
            
            // 2. Dispatch for all volumes
            foreach (var vol in volumes)
            {
                if (!vol.gameObject.activeInHierarchy || !vol.IsReady) continue;

                Vector3Int coord = Vector3Int.RoundToInt(vol.WorldOrigin); 
                if (!_chunkMap.ContainsKey(coord)) _chunkMap.Add(coord, vol);

                DispatchVolume(vol);
            }
        }
        
        private void DispatchVolume(VoxelVolume volume)
        {
            int res = volume.Resolution;
            int totalVoxels = res * res * res;
            int bitmaskSize = Mathf.CeilToInt(totalVoxels / 32.0f);

            ComputeBuffer topologyBuffer = new ComputeBuffer(bitmaskSize, 4);
            uint[] clearData = new uint[bitmaskSize];
            topologyBuffer.SetData(clearData); 
            
            // Active Bricks Buffer
            int bricksPerDim = res / 4;
            int maxBricks = bricksPerDim * bricksPerDim * bricksPerDim;
            ComputeBuffer activeBrickBuffer = new ComputeBuffer(maxBricks, sizeof(uint), ComputeBufferType.Append);
            activeBrickBuffer.SetCounterValue(0);

            int kernel = analysisShader.FindKernel("AnalyzeBricks");

            analysisShader.SetBuffer(kernel, "_GlobalNodeBuffer", volume.NodeBuffer);
            analysisShader.SetBuffer(kernel, "_GlobalPayloadBuffer", volume.PayloadBuffer);
            analysisShader.SetBuffer(kernel, "_GlobalBrickDataBuffer", volume.BrickDataBuffer);
            analysisShader.SetBuffer(kernel, "_PageTableBuffer", volume.BufferManager.PageTableBuffer);
            analysisShader.SetBuffer(kernel, "_TopologyBuffer", topologyBuffer);
            analysisShader.SetBuffer(kernel, "_ActiveBrickBuffer", activeBrickBuffer);

            analysisShader.SetInt("_Resolution", res);
            analysisShader.SetInt("_PageTableOffset", volume.BufferManager.PageTableOffset);
            analysisShader.SetInt("_BrickOffset", volume.BufferManager.BrickDataOffset);

            // Dispatch: Threads = Bricks. GroupSize = 4. 
            // We want one thread per brick.
            // BricksPerDim / 4
            int groups = Mathf.CeilToInt(bricksPerDim / 4.0f);
            analysisShader.Dispatch(kernel, groups, groups, groups);

            // Get Count
            ComputeBuffer countBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.IndirectArguments);
            ComputeBuffer.CopyCount(activeBrickBuffer, countBuffer, 0);
            
            _pendingReadbacks++;
            
            // Read Count
            AsyncGPUReadback.Request(countBuffer, (req) => OnCountReadback(req, countBuffer, activeBrickBuffer, topologyBuffer, volume));
        }

        private void OnCountReadback(AsyncGPUReadbackRequest request, ComputeBuffer countBuf, ComputeBuffer activeBuf, ComputeBuffer topoBuf, VoxelVolume vol)
        {
            int count = 0;
            if (!request.hasError)
            {
                count = (int)request.GetData<uint>()[0];
            }
            countBuf.Release();
            
            // Read Active Bricks (Limit to Count if possible, but AsyncGPUReadback size is fixed to buffer usually unless partial)
            // We'll read full and slice later, or just read full.
            AsyncGPUReadback.Request(activeBuf, (req) => OnDataReadback(req, activeBuf, topoBuf, vol, count));
        }

        private void OnDataReadback(AsyncGPUReadbackRequest brickReq, ComputeBuffer brickBuf, ComputeBuffer topoBuf, VoxelVolume vol, int brickCount)
        {
            brickBuf.Release(); // Done with GPU buffer
            
            NativeArray<uint> brickData = new NativeArray<uint>(0, Allocator.Persistent);
            if (!brickReq.hasError && brickCount > 0)
            {
                var allData = brickReq.GetData<uint>();
                // Copy only valid count
                brickData = new NativeArray<uint>(brickCount, Allocator.Persistent);
                NativeArray<uint>.Copy(allData, brickData, brickCount);
            }
            
            // Read Topology
            AsyncGPUReadback.Request(topoBuf, (topoReq) => OnFinalReadback(topoReq, topoBuf, vol, brickData));
        }

        private void OnFinalReadback(AsyncGPUReadbackRequest topoReq, ComputeBuffer topoBuf, VoxelVolume vol, NativeArray<uint> brickData)
        {
            topoBuf.Release();
            _pendingReadbacks--;
            
            if (!topoReq.hasError)
            {
                NativeArray<uint> topoRaw = topoReq.GetData<uint>();
                NativeArray<uint> persistentTopo = new NativeArray<uint>(topoRaw, Allocator.Persistent);
                
                lock(_volumeData)
                {
                    _volumeData[vol] = persistentTopo;
                    _volumeActiveBricks[vol] = brickData;
                }
            }
            else
            {
                if (brickData.IsCreated) brickData.Dispose();
            }
            
            if (_pendingReadbacks <= 0)
            {
                PerformGlobalDFS();
            }
        }

        private void PerformGlobalDFS()
        {
            _floatingVoxelPositions.Clear();
            
            Dictionary<VoxelVolume, System.Collections.BitArray> globalFloatingVisited = new Dictionary<VoxelVolume, System.Collections.BitArray>();
            
            foreach(var kvp in _volumeData)
            {
                int total = kvp.Key.Resolution * kvp.Key.Resolution * kvp.Key.Resolution;
                globalFloatingVisited[kvp.Key] = new System.Collections.BitArray(total);
            }

            int floatingCount = 0;
            List<(VoxelVolume, int)> seeds = new List<(VoxelVolume, int)>();

            if (_currentQueryBounds.HasValue)
            {
                // [Bounds Logic preserved: Iterates Volume Data + Bitmask within bounds]
                Bounds query = _currentQueryBounds.Value;
                query.Expand(2.0f); 

                foreach (var kvp in _volumeData)
                {
                    VoxelVolume vol = kvp.Key;
                    if (!vol.WorldBounds.Intersects(query)) continue;
                    
                    NativeArray<uint> data = kvp.Value;
                    int res = vol.Resolution;
                    float voxelSize = vol.WorldSize / res;
                    
                    Vector3 localMin = vol.transform.InverseTransformPoint(query.min);
                    Vector3 localMax = vol.transform.InverseTransformPoint(query.max);
                    
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
                // Full Scan Optimized with Active Bricks
                // We assume _volumeActiveBricks is populated
                foreach (var kvp in _volumeActiveBricks)
                {
                    VoxelVolume vol = kvp.Key;
                    NativeArray<uint> bricks = kvp.Value;
                    if (!bricks.IsCreated) continue;
                    
                    NativeArray<uint> topoData = _volumeData[vol];
                    int res = vol.Resolution;
                    int brickSize = 4; // Hardcoded matches shader

                    for (int i = 0; i < bricks.Length; i++)
                    {
                        uint packed = bricks[i];
                        int bx = (int)(packed & 0x3FF);
                        int by = (int)((packed >> 10) & 0x3FF);
                        int bz = (int)((packed >> 20) & 0x3FF);
                        
                        int startX = bx * brickSize;
                        int startY = by * brickSize;
                        int startZ = bz * brickSize;
                        
                        // Iterate voxels in this brick
                        for (int z = 0; z < brickSize; z++)
                        for (int y = 0; y < brickSize; y++)
                        for (int x = 0; x < brickSize; x++)
                        {
                            int vx = startX + x;
                            int vy = startY + y;
                            int vz = startZ + z;
                            
                            int idx = vz * (res * res) + vy * res + vx;
                            
                            // Double check solidity using bitmask (already populated)
                            if (IsSolid(topoData, idx))
                            {
                                seeds.Add((vol, idx));
                            }
                        }
                    }
                }
            }
            
            // ... [Rest of DFS Logic]
            
            foreach (var seed in seeds)
            {
                VoxelVolume seedVol = seed.Item1;
                int seedIdx = seed.Item2;
                
                if (globalFloatingVisited[seedVol][seedIdx]) continue;
                
                List<(VoxelVolume, int)> currentComponent = new List<(VoxelVolume, int)>();
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
                    
                    Vector3 worldPos = GetWorldPos(cVol, cIdx);
                    if (worldPos.y <= 10.0f) 
                    {
                        isGrounded = true;
                        break; 
                    }
                    
                    CheckNeighborsBias(cVol, cIdx, stack, pathVisited, globalFloatingVisited);
                }
                
                if (!isGrounded)
                {
                    floatingCount += currentComponent.Count;
                    foreach(var item in currentComponent)
                    {
                        _floatingVoxelPositions.Add(GetWorldPos(item.Item1, item.Item2));
                        globalFloatingVisited[item.Item1][item.Item2] = true;
                    }
                }
            }
            
            Debug.Log($"[Structural Analysis] Scan Complete. Seeds: {seeds.Count}. Floating Voxels: {floatingCount}");

            // Cleanup NativeArrays
            foreach(var kvp in _volumeData) { if(kvp.Value.IsCreated) kvp.Value.Dispose(); }
            foreach(var kvp in _volumeActiveBricks) { if(kvp.Value.IsCreated) kvp.Value.Dispose(); }
            _volumeData.Clear();
            _volumeActiveBricks.Clear();
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
