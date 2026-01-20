using UnityEngine;
using VoxelEngine.Core.Buffers;

namespace VoxelEngine.Core.Streaming
{
    public class DynamicVoxelBody : MonoBehaviour
    {
        public ComputeShader gridSVOBuilder;
        private DynamicVoxelVolume _volume;

        public void Initialize(ComputeBuffer sourceGrid, Vector3 size)
        {
            if (_volume == null) _volume = gameObject.AddComponent<DynamicVoxelVolume>();
            
            // Setup Volume
            int resolution = 32; // Fixed resolution for dynamic debris for now
            _volume.Initialize(resolution, size.x); // Assuming uniform or handling scale inside

            // Build SVO from the extracted grid
            _volume.BuildFromGrid(gridSVOBuilder, sourceGrid, new Vector3Int((int)size.x, (int)size.y, (int)size.z));
            
            // Adjust Mass
            // var rb = GetComponent<Rigidbody>();
            // rb.mass = size.x * size.y * size.z * 0.1f; // Simple density approximation
        }
    }
}