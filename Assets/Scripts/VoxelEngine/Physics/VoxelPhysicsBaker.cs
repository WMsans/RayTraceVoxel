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

        private struct PhysicsTriangle
        {
            public Vector3 v1;
            public Vector3 v2;
            public Vector3 v3;
        }

        private void GenerateMesh()
        {
            // 1. Setup Buffers
            // Output buffer for triangles (Struct size is 36 bytes: 3 * Vector3)
            // We interpret maxVertices as a rough limit on total vertices, so maxTriangles = maxVertices / 3
            int maxTriangles = maxVertices / 3;
            ComputeBuffer vertexOutput = new ComputeBuffer(maxTriangles, 36, ComputeBufferType.Append);
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
                // countBuffer contains the TRIANGLE count at index 0.
                countBuffer.GetData(args);
                int triangleCount = args[0];

                if (triangleCount == 0)
                {
                    Debug.LogWarning("Physics generation resulted in 0 triangles.");
                    return;
                }
                
                if (triangleCount > maxTriangles)
                {
                    Debug.LogWarning($"Triangle count {triangleCount} exceeded max {maxTriangles}. Truncating.");
                    triangleCount = maxTriangles;
                }

                // 4. Read Back Triangles
                PhysicsTriangle[] tris = new PhysicsTriangle[triangleCount];
                vertexOutput.GetData(tris, 0, 0, triangleCount);

                // Flatten to vertices
                int vertexCount = triangleCount * 3;
                Vector3[] vertices = new Vector3[vertexCount];
                for (int i = 0; i < triangleCount; i++)
                {
                    vertices[i * 3] = tris[i].v1;
                    vertices[i * 3 + 1] = tris[i].v2;
                    vertices[i * 3 + 2] = tris[i].v3;
                }

                // 5. Create Unity Mesh
                Mesh mesh = new Mesh();
                mesh.name = "VoxelPhysicsMesh";
                
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
                
                Debug.Log($"Baked Physics Mesh: {vertexCount} vertices ({triangleCount} triangles).");
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
