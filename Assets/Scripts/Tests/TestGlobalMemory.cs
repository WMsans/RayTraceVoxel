using UnityEngine;
using VoxelEngine.Core.Streaming;
using VoxelEngine.Core;

public class TestGlobalMemory : MonoBehaviour
{
    [Header("Test Settings")]
    public int gridWidth = 2; // Creates a 2x2 grid
    public float voxelSize = 1.0f; // Size of a single voxel
    public int resolution = 64; // Voxels per chunk

    private void Start()
    {
        if (VoxelVolumePool.Instance == null)
        {
            Debug.LogError("TestGlobalMemory: No VoxelVolumePool found in scene!");
            return;
        }

        float chunkSize = resolution * voxelSize;

        Debug.Log($"<color=green>Test Start:</color> Spawning {gridWidth * gridWidth} chunks...");

        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridWidth; z++)
            {
                // 1. Calculate Position (Continuous Grid)
                Vector3 pos = new Vector3(x * chunkSize, 0, z * chunkSize);

                // 2. Request from Pool
                VoxelVolume vol = VoxelVolumePool.Instance.GetVolume(pos, chunkSize);

                if (vol != null)
                {
                    Debug.Log($"Spawned Chunk [{x},{z}] at {pos}. \n" +
                              $"Global Node Offset: {vol.BufferManager.NodeOffset} \n" +
                              $"Global Brick Offset: {vol.BufferManager.BrickOffset}");
                }
                else
                {
                    Debug.LogError($"Failed to spawn chunk at {pos}. Pool might be empty.");
                }
            }
        }
    }
}