using UnityEngine;
using VoxelEngine.Core;
using VoxelEngine.Core.Buffers;

namespace VoxelEngine.Physics
{
    [RequireComponent(typeof(VoxelVolume))]
    public class VoxelPhysicsBaker : MonoBehaviour
    {
        [Header("Settings")]
        public ComputeShader physicsShader;
        [Tooltip("Sampling stride. Higher means lower resolution mesh. e.g. 4 for 1/4th resolution.")]
        public int stride = 4;
        [Tooltip("Max vertices to allocate buffer for. 65536 is usually enough for coarse physics.")]
        public int maxVertices = 65536;

        [Header("References")]
        [Tooltip("Optional: Child object name to hold the collider.")]
        public string colliderChildName = "PhysicsCollider";

        private VoxelVolume _volume;

        private void Awake()
        {
            _volume = GetComponent<VoxelVolume>();
        }

        [ContextMenu("Bake Physics Mesh")]
        public void Bake()
        {
            if (_volume == null) _volume = GetComponent<VoxelVolume>();
            if (physicsShader == null)
            {
                Debug.LogError("Physics Shader is missing!");
                return;
            }
            if (!_volume.IsReady)
            {
                Debug.LogError("Voxel Volume is not ready (buffers not initialized).");
                return;
            }

            GenerateMesh();
        }

        private void GenerateMesh()
        {
            // 1. Setup Buffers
            // Output buffer for vertices (Float3 -> Vector3 stride is 12 bytes)
            ComputeBuffer vertexOutput = new ComputeBuffer(maxVertices, 12, ComputeBufferType.Append);
            vertexOutput.SetCounterValue(0);

            // Indirect args buffer to capture the count (4 ints)
            ComputeBuffer countBuffer = new ComputeBuffer(4, sizeof(int), ComputeBufferType.IndirectArguments);
            int[] args = new int[] { 0, 1, 0, 0 };
            countBuffer.SetData(args);

            try
            {
                // 2. Dispatch Compute Shader
                // Note: We use the volume's resolution, but the stride determines the physics mesh density.
                PhysicsGenerator.Generate(
                    physicsShader, 
                    _volume.BufferManager, 
                    vertexOutput, 
                    countBuffer, 
                    _volume.Resolution, 
                    stride, 
                    _volume.WorldOrigin, 
                    _volume.WorldSize
                );

                // 3. Read Back Count
                // We use ComputeBuffer.CopyCount inside Generate, so countBuffer contains the vertex count at index 0 (if using Append buffer logic usually CopyCount puts it in the first int).
                // Actually CopyCount copies the structure count to the destination buffer at dstOffsetBytes.
                // If we treat countBuffer as an int array, the first int will be the count.
                countBuffer.GetData(args);
                int vertexCount = args[0];

                if (vertexCount == 0)
                {
                    Debug.LogWarning("Physics generation resulted in 0 vertices.");
                    return;
                }
                
                if (vertexCount > maxVertices)
                {
                    Debug.LogWarning($"Vertex count {vertexCount} exceeded max {maxVertices}. Truncating.");
                    vertexCount = maxVertices;
                }

                // 4. Read Back Vertices
                Vector3[] vertices = new Vector3[vertexCount];
                vertexOutput.GetData(vertices, 0, 0, vertexCount);

                // 5. Create Unity Mesh
                Mesh mesh = new Mesh();
                mesh.name = "VoxelPhysicsMesh";
                
                // Index format 32 might be needed if > 65k vertices, though usually low res physics is smaller.
                if (vertexCount > 65535)
                    mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

                mesh.SetVertices(vertices);

                // Simple indices 0..N-1
                int[] indices = new int[vertexCount];
                for (int i = 0; i < vertexCount; i++) indices[i] = i;
                
                mesh.SetIndices(indices, MeshTopology.Triangles, 0);

                // 6. Optimization
                // "Crucial Optimization: Call Mesh.Optimize() or set Mesh.UploadMeshData(markNoLongerReadable: true)"
                mesh.Optimize();
                mesh.UploadMeshData(true);

                // 7. Assign to Collider
                AssignMeshToCollider(mesh);
                
                Debug.Log($"Baked Physics Mesh: {vertexCount} vertices.");
            }
            finally
            {
                // Cleanup
                vertexOutput?.Release();
                countBuffer?.Release();
            }
        }

        private void AssignMeshToCollider(Mesh mesh)
        {
            Transform child = transform.Find(colliderChildName);
            if (child == null)
            {
                GameObject obj = new GameObject(colliderChildName);
                obj.transform.SetParent(transform, false);
                child = obj.transform;
            }

            // Ensure MeshFilter exists (useful for debug drawing or if we want to render it later)
            MeshFilter filter = child.GetComponent<MeshFilter>();
            if (filter == null) filter = child.gameObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;

            MeshCollider collider = child.GetComponent<MeshCollider>();
            if (collider == null) collider = child.gameObject.AddComponent<MeshCollider>();
            
            // Unity implies a "Bake" cost when assigning.
            collider.sharedMesh = mesh;
        }
    }
}
