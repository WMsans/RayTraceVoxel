using UnityEngine;
using VoxelEngine.Core.Generators;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Streaming;

public class DebugVoxelGridSpawner : MonoBehaviour
{
    [Header("Settings")]
    public int resolution = 32;
    public float worldSize = 5.0f;
    public Vector3 spawnPosition = new Vector3(0, 10, 0);

    [Header("Debug")]
    public bool spawnOnStart = false;

    private void Start()
    {
        if (spawnOnStart) SpawnTestGrid();
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
        // We will create a 3D "Cross" shape to prove we are reading a grid, not just a sphere/cube.
        int totalVoxels = resolution * resolution * resolution;
        uint[] packedData = new uint[totalVoxels];
        
        float center = resolution / 2.0f;
        float thickness = resolution / 4.0f;

        for (int z = 0; z < resolution; z++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int index = z * resolution * resolution + y * resolution + x;

                    // Logic for a 3D Cross
                    bool inX = Mathf.Abs(x - center) < thickness && Mathf.Abs(y - center) < thickness;
                    bool inY = Mathf.Abs(x - center) < thickness && Mathf.Abs(z - center) < thickness;
                    bool inZ = Mathf.Abs(y - center) < thickness && Mathf.Abs(z - center) < thickness;
                    
                    bool isSolid = inX || inY || inZ;

                    // SDF Values: Surface = 0.0, Solid < 0.0, Air > 0.0
                    // We simply set Solid to -1.0 and Air to 1.0 for testing.
                    float sdf = isSolid ? -1.0f : 1.0f;
                    
                    // Pack data (matches your VoxelStructures.hlsl packing)
                    // We assume Material ID 2 (Red/Stone) for visibility
                    packedData[index] = PackData(sdf, Vector3.up, 2); 
                }
            }
        }

        // 2. Register Grid with Manager (Uploads to GPU Texture3D)
        int textureIndex = DynamicSDFManager.Instance.RegisterVoxelGrid(packedData, resolution);

        if (textureIndex == -1)
        {
            Debug.LogError("Failed to register voxel grid. Atlas might be full.");
            return;
        }

        Debug.Log($"Voxel Grid Registered at Texture Atlas Index: {textureIndex}");

        // 3. Create the SDF Object
        SDFObject obj = new SDFObject
        {
            type = 2, // VoxelGrid
            operation = 0, // Union
            position = spawnPosition,
            rotation = Quaternion.identity,
            scale = Vector3.one * worldSize,
            boundsMin = spawnPosition - Vector3.one * (worldSize * 0.5f),
            boundsMax = spawnPosition + Vector3.one * (worldSize * 0.5f),
            blendFactor = 0.5f,
            materialId = 2,
            textureIndex = textureIndex
        };

        // 4. Register Object to World
        DynamicSDFManager.Instance.RegisterObject(obj);
    }

    // Helper to match your HLSL packing logic logic
    private uint PackData(float sdf, Vector3 normal, uint matId)
    {
        // Clamp SDF to range [-4, 4] (MAX_SDF_RANGE)
        float maxRange = 4.0f;
        float normalizedSDF = Mathf.Clamp(sdf / maxRange, -1.0f, 1.0f);
        uint sdfInt = (uint)((normalizedSDF * 0.5f + 0.5f) * 255.0f);
        
        // Simplified normal packing (just for test, passing 0 is often fine for simple SDFs)
        uint norm = 0; // Keeping it simple, usually requires Octahedral packing
        
        return (matId & 0xFF) | (sdfInt << 8) | (norm << 16);
    }
}