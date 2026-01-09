using UnityEngine;
using VoxelEngine.Core.Streaming;
using VoxelEngine.Core.Rendering;

public class TestGlobalDispatch : MonoBehaviour
{
    public VoxelVolumePool pool;
    public int gridSize = 2; // Creates a 2x2 grid of chunks
    public float separation = 5.0f; // Gap between chunks to prove they are separate

    private ComputeBuffer _debugRaycastBuffer;
    private Vector4[] _raycastData = new Vector4[1];

    private void Start()
    {
        if (pool == null) pool = FindFirstObjectByType<VoxelVolumePool>();

        // Spawn a grid of chunks
        float size = 64.0f; // Assuming standard chunk size
        
        for (int x = 0; x < gridSize; x++)
        {
            for (int z = 0; z < gridSize; z++)
            {
                // Position chunks with a gap
                Vector3 pos = new Vector3(x * (size + separation), 0, z * (size + separation));
                
                // Get Volume (Auto-activates and Generates SDF)
                pool.GetVolume(pos, size);
            }
        }
        
        Debug.Log($"<color=green>Test Phase 2 Started:</color> Spawned {gridSize * gridSize} chunks.");
    }

    private void OnDrawGizmos()
    {
        // Visualize the GPU Raycast result
        // The VoxelRaytracerFeature writes the hit position of the center pixel to this buffer.
        if (VoxelRaytracerFeature.RaycastHitBuffer != null)
        {
            VoxelRaytracerFeature.RaycastHitBuffer.GetData(_raycastData);
            Vector3 hitPos = new Vector3(_raycastData[0].x, _raycastData[0].y, _raycastData[0].z);
            
            if (_raycastData[0].w > 0.0f) // w is usually 1.0 if hit, 0 if miss
            {
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(hitPos, 0.5f);
                Gizmos.DrawLine(Camera.main.transform.position, hitPos);
            }
        }
    }
}