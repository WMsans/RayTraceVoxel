using UnityEngine;
using VoxelEngine.Core.Generators;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Streaming;

public class DebugVoxelGridSpawner : MonoBehaviour
{
    [Header("Settings")]
    public int resolution = 32;
    public float worldSize = 5.0f;
    // Removed 'spawnPosition' to rely on the Transform's position instead

    [Header("Debug")]
    public bool spawnOnStart = false;

    // Track the object in the manager
    private int _sdfObjectIndex = -1;
    private int _textureAtlasIndex = -1;

    private void Start()
    {
        if (spawnOnStart) SpawnTestGrid();
    }

    private void Update()
    {
        // Only update if we have a registered object and the transform has moved
        if (_sdfObjectIndex != -1 && transform.hasChanged)
        {
            UpdateSDFTransform();
            transform.hasChanged = false;
        }
    }

    [ContextMenu("Spawn Test Grid")]
    public void SpawnTestGrid()
    {
        if (DynamicSDFManager.Instance == null)
        {
            Debug.LogError("DynamicSDFManager is missing from the scene!");
            return;
        }

        // 1. Generate Raw Voxel Data (CPU)
        int totalVoxels = resolution * resolution * resolution;
        uint[] packedData = new uint[totalVoxels];
        
        // [FIX] Center the sphere perfectly within the bounds 0..32
        float center = resolution / 2.0f;
        
        // Define Sphere Radius (e.g., 40% of the resolution to ensure it fits inside the bounds)
        float radius = resolution * 0.4f;

        for (int z = 0; z < resolution; z++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int index = z * resolution * resolution + y * resolution + x;

                    // [FIX] Use +0.5 to sample the CENTER of the voxel, aligning with Texture UVs
                    Vector3 voxelPos = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);

                    // Calculate distance from the current voxel to the center of the grid
                    float dist = Vector3.Distance(voxelPos, new Vector3(center, center, center));

                    // Calculate SDF: 
                    // dist - radius < 0 (Negative) means INSIDE the sphere
                    // dist - radius > 0 (Positive) means OUTSIDE the sphere
                    float sdf = dist - radius;
                    
                    packedData[index] = PackData(sdf, Vector3.up, 2); 
                }
            }
        }

        // 2. Register Grid with Manager (Uploads to GPU Texture3D)
        // We only need to do this ONCE. The texture data doesn't change when we move.
        _textureAtlasIndex = DynamicSDFManager.Instance.RegisterVoxelGrid(packedData, resolution);

        if (_textureAtlasIndex == -1)
        {
            Debug.LogError("Failed to register voxel grid. Atlas might be full.");
            return;
        }

        Debug.Log($"Voxel Grid Registered at Texture Atlas Index: {_textureAtlasIndex}");

        // 3. Register the SDF Object
        RegisterSDFObject();
    }

    private void RegisterSDFObject()
    {
        SDFObject obj = CreateSDFObjectFromTransform();

        DynamicSDFManager.Instance.RegisterObject(obj);
        
        // Store the index so we can update it later. 
        // Note: RegisterObject adds to the end of the list.
        _sdfObjectIndex = DynamicSDFManager.Instance.ObjectCount - 1;
    }

    private void UpdateSDFTransform()
    {
        if (DynamicSDFManager.Instance == null) return;

        SDFObject updatedObj = CreateSDFObjectFromTransform();
        DynamicSDFManager.Instance.UpdateObject(_sdfObjectIndex, updatedObj);
    }

    private SDFObject CreateSDFObjectFromTransform()
    {
        Vector3 pos = transform.position;
        // Calculate bounds based on current position
        Vector3 size = Vector3.one * worldSize;
        Vector3 min = pos - size * 0.5f;
        Vector3 max = pos + size * 0.5f;

        return new SDFObject
        {
            type = 2, // VoxelGrid
            operation = 0, // Union
            position = pos,
            rotation = transform.rotation, // Sync Rotation
            scale = size,
            boundsMin = min,
            boundsMax = max,
            blendFactor = 0.5f,
            materialId = 2,
            textureIndex = _textureAtlasIndex
        };
    }

    private uint PackData(float sdf, Vector3 normal, uint matId)
    {
        float maxRange = 4.0f;
        float normalizedSDF = Mathf.Clamp(sdf / maxRange, -1.0f, 1.0f);
        uint sdfInt = (uint)((normalizedSDF * 0.5f + 0.5f) * 255.0f);
        uint norm = 0; 
        return (matId & 0xFF) | (sdfInt << 8) | (norm << 16);
    }

    private void OnDisable()
    {
        // Cleanup: If this object is disabled/destroyed, remove the SDF from the world
        if (_sdfObjectIndex != -1 && DynamicSDFManager.Instance != null)
        {
            // Note: In a complex system, removing objects shifts indices of other objects.
            // For a debug tool, this is acceptable, but be aware if using multiple tools.
            DynamicSDFManager.Instance.RemoveObjectAt(_sdfObjectIndex);
            _sdfObjectIndex = -1;
        }
    }
}