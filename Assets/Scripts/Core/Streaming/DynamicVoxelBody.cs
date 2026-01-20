using UnityEngine;
using VoxelEngine.Core.Generators;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Editing; 
using System.Collections.Generic; // Added for Dictionary

namespace VoxelEngine.Core.Streaming
{
    public class DynamicVoxelBody : MonoBehaviour
    {
        // Track the object in the DynamicSDFManager
        private int _sdfObjectIndex = -1;
        private int _textureAtlasIndex = -1;
        private bool _isInitialized = false;

        public void Initialize(ComputeBuffer sourceGrid, Vector3 worldSize)
        {
            if (DynamicSDFManager.Instance == null)
            {
                Debug.LogError("DynamicSDFManager not found.");
                Destroy(gameObject);
                return;
            }

            // 1. Synchronous Readback of Voxel Data
            // We must read immediately because the caller (StructuralIntegritySystem) releases the buffer after this call.
            uint[] rawData = new uint[sourceGrid.count];
            sourceGrid.GetData(rawData);

            // [FIX] Determine Dominant Material from the raw data
            int dominantMat = 1; // Default
            Dictionary<int, int> matCounts = new Dictionary<int, int>();
            
            for (int i = 0; i < rawData.Length; i++)
            {
                uint val = rawData[i];
                // Unpack SDF (Byte 2)
                uint sdfInt = (val >> 8) & 0xFF;
                
                // Check if Solid (SDF < 0). 
                // In packed format: 0 = -1.0, 255 = 1.0. 
                // So any value < 127 is solid (negative distance).
                if (sdfInt < 127)
                {
                    int mat = (int)(val & 0xFF); // Byte 1 is Material ID
                    if (mat != 0)
                    {
                        if (!matCounts.ContainsKey(mat)) matCounts[mat] = 0;
                        matCounts[mat]++;
                    }
                }
            }
            
            int maxCount = -1;
            foreach(var kvp in matCounts)
            {
                if (kvp.Value > maxCount)
                {
                    maxCount = kvp.Value;
                    dominantMat = kvp.Key;
                }
            }

            // 2. Prepare Data for Atlas
            // We need to fit the extracted data into the Atlas chunk size (default 32x32x32).
            int atlasRes = DynamicSDFManager.Instance.atlasResolution;
            uint[] paddedData = new uint[atlasRes * atlasRes * atlasRes];

            // Calculate dimensions of the source data
            float voxelSize = VoxelEditManager.Instance != null ? VoxelEditManager.Instance.voxelSize : 1.0f;
            int srcX = Mathf.CeilToInt(worldSize.x / voxelSize);
            int srcY = Mathf.CeilToInt(worldSize.y / voxelSize);
            int srcZ = Mathf.CeilToInt(worldSize.z / voxelSize);

            // Copy rawData into paddedData (Centering is optional, here we map 0,0,0 to 0,0,0 for simplicity)
            // Ensure we don't exceed atlas bounds
            int limitX = Mathf.Min(srcX, atlasRes);
            int limitY = Mathf.Min(srcY, atlasRes);
            int limitZ = Mathf.Min(srcZ, atlasRes);

            for (int z = 0; z < limitZ; z++)
            {
                for (int y = 0; y < limitY; y++)
                {
                    for (int x = 0; x < limitX; x++)
                    {
                        int srcIdx = z * (srcX * srcY) + y * srcX + x;
                        int dstIdx = z * (atlasRes * atlasRes) + y * atlasRes + x;

                        if (srcIdx < rawData.Length)
                        {
                            paddedData[dstIdx] = rawData[srcIdx];
                        }
                    }
                }
            }

            // 3. Register Voxel Grid to Texture Atlas
            _textureAtlasIndex = DynamicSDFManager.Instance.RegisterVoxelGrid(paddedData, atlasRes);

            if (_textureAtlasIndex == -1)
            {
                Debug.LogWarning("DynamicVoxelBody: Atlas is full, cannot create debris.");
                Destroy(gameObject);
                return;
            }

            // 4. Create and Register SDF Object
            // The SDF Object tells the generator where to place this voxel grid in the world.
            SDFObject obj = new SDFObject
            {
                type = 2, // VoxelGrid
                operation = 0, // Union
                position = transform.position,
                rotation = transform.rotation,
                scale = worldSize, // This scales the 0..1 UV space of the texture to World Units
                boundsMin = transform.position - worldSize * 0.5f,
                boundsMax = transform.position + worldSize * 0.5f,
                blendFactor = 0.5f, // Smooth blend with terrain
                materialId = dominantMat, // [FIX] Use detected material
                textureIndex = _textureAtlasIndex
            };

            DynamicSDFManager.Instance.RegisterObject(obj);
            
            // Store the index to update transform later
            _sdfObjectIndex = DynamicSDFManager.Instance.ObjectCount - 1;
            _isInitialized = true;
        }

        private void Update()
        {
            // Sync the SDF Object with the Rigidbody's movement
            if (_isInitialized && _sdfObjectIndex != -1 && transform.hasChanged)
            {
                SDFObject obj = DynamicSDFManager.Instance.GetObject(_sdfObjectIndex);
                
                obj.position = transform.position;
                obj.rotation = transform.rotation;
                
                // Recalculate bounds for Dirty Region logic (updates chunks)
                // Note: Frequent updates here will cause frequent chunk regeneration.
                Vector3 halfSize = obj.scale * 0.5f;
                // AABB rotation logic is simplified here; for exact bounds, rotate the corners.
                // Using a safe margin for rotation:
                float maxExtent = halfSize.magnitude;
                obj.boundsMin = transform.position - Vector3.one * maxExtent;
                obj.boundsMax = transform.position + Vector3.one * maxExtent;

                DynamicSDFManager.Instance.UpdateObject(_sdfObjectIndex, obj);
                transform.hasChanged = false;
            }
        }

        private void OnDisable()
        {
            // Remove from world when destroyed
            if (_sdfObjectIndex != -1 && DynamicSDFManager.Instance != null)
            {
                DynamicSDFManager.Instance.RemoveObjectAt(_sdfObjectIndex);
                _sdfObjectIndex = -1;
            }
        }
    }
}