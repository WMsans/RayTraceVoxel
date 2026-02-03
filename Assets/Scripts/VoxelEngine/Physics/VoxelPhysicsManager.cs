using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine.Core;
using VoxelEngine.Core.Streaming;

namespace VoxelEngine.Physics
{
    public class VoxelPhysicsManager : MonoBehaviour
    {
        public static VoxelPhysicsManager Instance { get; private set; }

        [Header("Settings")]
        public ComputeShader physicsShader;
        [Tooltip("Target updates per second.")]
        public float updateFrequency = 4.0f;
        [Tooltip("Max volumes to dispatch per update step.")]
        public int batchSize = 1;
        [Tooltip("Sampling stride. Higher means lower resolution mesh.")]
        public int stride = 4;
        [Tooltip("Max vertices limit for buffer.")]
        public int maxVertices = 65536;

        private HashSet<VoxelVolume> _dirtyQueue = new HashSet<VoxelVolume>();
        private float _timer;
        
        // Structure for readback context
        private struct PhysicsRequest
        {
            public VoxelVolume volume;
            public ComputeBuffer vertexBuffer;
            public ComputeBuffer countBuffer;
        }
        
        private struct PhysicsTriangle
        {
            public Vector3 v1;
            public Vector3 v2;
            public Vector3 v3;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        public void Enqueue(VoxelVolume volume)
        {
            if (volume == null) return;
            if (!_dirtyQueue.Contains(volume))
            {
                _dirtyQueue.Add(volume);
            }
        }

        public void Remove(VoxelVolume volume)
        {
            if (volume == null) return;
            if (_dirtyQueue.Contains(volume))
            {
                _dirtyQueue.Remove(volume);
            }
            
            // Also ensure we clean up existing collider if we are removing it (e.g. merged back)
            // But this method might be called just to cancel an update. 
            // The caller (WorldManager) should handle the collider disabling if strictly needed.
            // However, the prompt says: "Action: The child volumes are returned to the pool. You must clear their MeshCollider"
            // So we can provide a helper for that.
        }

        public void ClearCollider(VoxelVolume volume)
        {
                 MeshCollider mc = volume.meshCol;
                 if (mc != null) mc.sharedMesh = null;
                 mc.gameObject.SetActive(false);
        }

        private void Update()
        {
            if (_dirtyQueue.Count == 0) return;

            _timer += Time.deltaTime;
            if (_timer >= (1.0f / updateFrequency))
            {
                _timer = 0;
                ProcessBatch();
            }
        }

        private void ProcessBatch()
        {
            int processed = 0;
            List<VoxelVolume> toRemove = new List<VoxelVolume>();

            foreach (var vol in _dirtyQueue)
            {
                if (processed >= batchSize) break;
                
                // Validate
                if (vol == null || !vol.isActiveAndEnabled || !vol.IsReady)
                {
                    toRemove.Add(vol);
                    continue;
                }

                DispatchPhysics(vol);
                toRemove.Add(vol);
                processed++;
            }

            foreach (var vol in toRemove)
            {
                _dirtyQueue.Remove(vol);
            }
        }

        private void DispatchPhysics(VoxelVolume volume)
        {
            if (physicsShader == null) return;

            int maxTriangles = maxVertices / 3;
            ComputeBuffer vertexOutput = new ComputeBuffer(maxTriangles, 36, ComputeBufferType.Append);
            vertexOutput.SetCounterValue(0);

            ComputeBuffer countBuffer = new ComputeBuffer(4, sizeof(int), ComputeBufferType.IndirectArguments);
            countBuffer.SetData(new int[] { 0, 1, 0, 0 });

            PhysicsGenerator.Generate(
                physicsShader, 
                volume.BufferManager, 
                vertexOutput, 
                countBuffer, 
                volume.Resolution, 
                stride, 
                volume.WorldOrigin, 
                volume.WorldSize
            );

            // Request Readback for Count
            // We attach context to the callback
            var req = new PhysicsRequest { volume = volume, vertexBuffer = vertexOutput, countBuffer = countBuffer };
            
            // We need to CopyCount to the countBuffer (PhysicsGenerator might have done it, let's check. 
            // PhysicsGenerator usually dispatches the kernel. If it doesn't CopyCount, we need to do it.
            // Assuming PhysicsGenerator just runs the kernel. 
            // Wait, standard Append buffer usage requires CopyCount to get the counter into an args buffer.
            // Let's assume PhysicsGenerator does NOT do CopyCount unless I see it.
            // I'll check PhysicsGenerator.cs content in a moment, but it's safer to do it here if needed.
            // Actually, VoxelPhysicsBaker uses `countBuffer` as IndirectArguments and calls `countBuffer.GetData(args)`.
            // But it doesn't show `ComputeBuffer.CopyCount` in the snippet I saw. 
            // Implicitly, if the shader uses an AppendStructuredBuffer, the counter is internal. 
            // To get it into `countBuffer`, we need `ComputeBuffer.CopyCount(vertexOutput, countBuffer, 0);`
            // If VoxelPhysicsBaker worked, maybe PhysicsGenerator does it? 
            // Or maybe it's not an Append buffer in the shader? 
            // VoxelPhysicsBaker: `ComputeBuffer vertexOutput = new ComputeBuffer(..., ComputeBufferType.Append);`
            // So it IS Append. 
            // I will add CopyCount to be safe/correct.
            
            ComputeBuffer.CopyCount(vertexOutput, countBuffer, 0);

            AsyncGPUReadback.Request(countBuffer, (r) => OnCountReadback(r, req));
        }

        private void OnCountReadback(AsyncGPUReadbackRequest request, PhysicsRequest context)
        {
            if (request.hasError)
            {
                context.vertexBuffer.Release();
                context.countBuffer.Release();
                return;
            }

            var data = request.GetData<int>();
            int triangleCount = data[0];

            if (triangleCount <= 0)
            {
                context.vertexBuffer.Release();
                context.countBuffer.Release();
                // Clear collider if no mesh?
                // ClearCollider(context.volume); // Optional
                return;
            }

            int maxTriangles = maxVertices / 3;
            if (triangleCount > maxTriangles) triangleCount = maxTriangles;

            // Now request the vertex data
            // We can request the whole buffer or just the part we need?
            // AsyncGPUReadback.Request(ComputeBuffer src, int size, int offset, ...)
            // Size in bytes. 36 bytes per triangle struct (3 vectors * 3 floats * 4 bytes = 36? No. 
            // Vector3 = 12 bytes. 3 * 12 = 36 bytes. Correct.
            
            AsyncGPUReadback.Request(context.vertexBuffer, triangleCount * 36, 0, (r) => OnVertexReadback(r, context, triangleCount));
        }

        private void OnVertexReadback(AsyncGPUReadbackRequest request, PhysicsRequest context, int triangleCount)
        {
            // Always release buffers in the final callback
            context.vertexBuffer.Release();
            context.countBuffer.Release();

            if (request.hasError || context.volume == null) return;

            // Deserialize
            var tris = request.GetData<PhysicsTriangle>(); // This gets the whole array or slice? 
            // GetData<T> returns a NativeArray.
            
            if (tris.Length < triangleCount)
            {
                 // Should not happen if we requested correct size
                 return;
            }

            // Weld and Build Mesh
            // Note: This is on Main Thread (callback). 
            // For large meshes this could spike. 
            // But we throttled the dispatch (2-4Hz, 1 batch).
            
            BuildMesh(context.volume, tris, triangleCount);
        }

        private void BuildMesh(VoxelVolume volume, Unity.Collections.NativeArray<PhysicsTriangle> tris, int count)
        {
            // Welding Logic (same as VoxelPhysicsBaker)
            List<Vector3> weldedVertices = new List<Vector3>(count);
            List<int> weldedIndices = new List<int>(count * 3);
            Dictionary<Vector3, int> vertexMap = new Dictionary<Vector3, int>(count);

            for (int i = 0; i < count; i++)
            {
                var t = tris[i];
                WeldVertex(t.v1, weldedVertices, weldedIndices, vertexMap);
                WeldVertex(t.v2, weldedVertices, weldedIndices, vertexMap);
                WeldVertex(t.v3, weldedVertices, weldedIndices, vertexMap);
            }

            Mesh mesh = new Mesh();
            mesh.name = "VoxelPhysicsMesh_" + volume.name;
            if (weldedVertices.Count > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(weldedVertices);
            mesh.SetIndices(weldedIndices.ToArray(), MeshTopology.Triangles, 0);
            mesh.Optimize();
            mesh.UploadMeshData(true);

            // Assign
            AssignMeshToCollider(volume, mesh);
        }

        private void WeldVertex(Vector3 v, List<Vector3> vertices, List<int> indices, Dictionary<Vector3, int> vertexMap)
        {
            if (vertexMap.TryGetValue(v, out int index))
            {
                indices.Add(index);
            }
            else
            {
                index = vertices.Count;
                vertices.Add(v);
                vertexMap[v] = index;
                indices.Add(index);
            }
        }

        private void AssignMeshToCollider(VoxelVolume volume, Mesh mesh)
        {
            volume.meshCol.gameObject.SetActive(true);

            // MeshFilter for debug/vis (optional)
            MeshFilter filter = volume.meshFil;
            filter.sharedMesh = mesh;

            MeshCollider collider = volume.meshCol;
            collider.sharedMesh = mesh;
        }
    }
}
