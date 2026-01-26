using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
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

        public void AnalyzeWorld()
        {
            if (analysisShader == null) return;
            
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
            
            // Tracking visited voxels globally: Dictionary<(Volume, Index), bool> is too slow.
            // Using BitArray per volume in a dictionary.
            Dictionary<VoxelVolume, System.Collections.BitArray> visitedMap = new Dictionary<VoxelVolume, System.Collections.BitArray>();
            
            foreach(var kvp in _volumeData)
            {
                int total = kvp.Key.Resolution * kvp.Key.Resolution * kvp.Key.Resolution;
                visitedMap[kvp.Key] = new System.Collections.BitArray(total);
            }

            int floatingCount = 0;
            
            // iterate all volumes, all voxels
            foreach (var kvp in _volumeData)
            {
                VoxelVolume vol = kvp.Key;
                NativeArray<uint> data = kvp.Value;
                int res = vol.Resolution;
                int totalVoxels = res * res * res;
                var visited = visitedMap[vol];
                
                _debugVoxelSize = vol.WorldSize / res; // update debug size

                for (int i = 0; i < totalVoxels; i++)
                {
                    if (visited[i]) continue;
                    
                    if (IsSolid(data, i))
                    {
                        // Start DFS
                        List<(VoxelVolume, int)> component = new List<(VoxelVolume, int)>();
                        bool isGrounded = false;
                        
                        Stack<(VoxelVolume, int)> stack = new Stack<(VoxelVolume, int)>();
                        stack.Push((vol, i));
                        visited[i] = true;
                        
                        while(stack.Count > 0)
                        {
                            var current = stack.Pop();
                            VoxelVolume cVol = current.Item1;
                            int cIdx = current.Item2;
                            
                            component.Add(current);
                            
                            // Check Grounded
                            Vector3 worldPos = GetWorldPos(cVol, cIdx);
                            if (worldPos.y < 10.0f) isGrounded = true;
                            
                            // Neighbors
                            CheckNeighbors(cVol, cIdx, stack, visitedMap);
                        }
                        
                        if (!isGrounded)
                        {
                            // Mark all as floating
                            floatingCount += component.Count;
                            foreach(var item in component)
                            {
                                _floatingVoxelPositions.Add(GetWorldPos(item.Item1, item.Item2));
                            }
                        }
                    }
                }
            }
            
            Debug.Log($"[Structural Analysis] Global Scan Complete. Floating Voxels: {floatingCount}");

            // Cleanup NativeArrays
            foreach(var kvp in _volumeData)
            {
                if(kvp.Value.IsCreated) kvp.Value.Dispose();
            }
            _volumeData.Clear();
        }
        
        private void CheckNeighbors(VoxelVolume vol, int idx, Stack<(VoxelVolume, int)> stack, Dictionary<VoxelVolume, System.Collections.BitArray> visitedMap)
        {
            int res = vol.Resolution;
            int z = idx / (res * res);
            int rem = idx % (res * res);
            int y = rem / res;
            int x = rem % res;
            
            // 6 Directions
            CheckDir(x + 1, y, z);
            CheckDir(x - 1, y, z);
            CheckDir(x, y + 1, z);
            CheckDir(x, y - 1, z);
            CheckDir(x, y, z + 1);
            CheckDir(x, y, z - 1);
            
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
                    // Find neighbor volume
                    Vector3Int currentCoord = Vector3Int.RoundToInt(vol.WorldOrigin);
                    // Assuming volume size is what dictates the grid step. 
                    // WorldSize should be same as 'size' in map.
                    // For simplicity, assuming Resolution * VoxelSize matches the grid step implicitly
                    // or that WorldOrigin jumps by WorldSize.
                    // The map key was created with RoundToInt(WorldOrigin).
                    // So we need to add (WorldSize * offset).
                    
                    int step = Mathf.RoundToInt(vol.WorldSize); // Assuming uniform size
                    Vector3Int targetCoord = currentCoord + (neighborOffset * step);
                    
                    if (!_chunkMap.TryGetValue(targetCoord, out targetVol)) return; // No neighbor
                }
                
                // Check Bounds (Safety, though wrapped coords should be valid)
                if (tx >= 0 && tx < targetVol.Resolution && 
                    ty >= 0 && ty < targetVol.Resolution && 
                    tz >= 0 && tz < targetVol.Resolution)
                {
                    int tIdx = tz * (res * res) + ty * res + tx;
                    
                    if (visitedMap.ContainsKey(targetVol) && !visitedMap[targetVol][tIdx])
                    {
                        // Check Solidity
                        if (_volumeData.ContainsKey(targetVol) && IsSolid(_volumeData[targetVol], tIdx))
                        {
                            visitedMap[targetVol][tIdx] = true;
                            stack.Push((targetVol, tIdx));
                        }
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
