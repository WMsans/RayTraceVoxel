using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine.Core;
using VoxelEngine.Core.Streaming;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;

namespace VoxelEngine.Physics
{
    public class VoxelPhysicsManager : MonoBehaviour
    {
        public static VoxelPhysicsManager Instance { get; private set; }

        [Header("Settings")]
        public ComputeShader physicsShader;
        [Tooltip("Target updates per second.")]
        public float updateFrequency = 10.0f;
        [Tooltip("Max volumes to dispatch per update step.")]
        public int batchSize = 32;
        [Tooltip("Sampling stride. Higher means lower resolution mesh.")]
        public int stride = 4;
        [Tooltip("Max vertices limit for buffer.")]
        public int maxVertices = 65536;

        private HashSet<VoxelVolume> _dirtyQueue = new HashSet<VoxelVolume>();
        private float _timer;
        private ComputeBuffer _edgeTableBuffer;
        private ComputeBuffer _triTableBuffer;
        
        // Structure for readback context
        private struct PhysicsRequest
        {
            public VoxelVolume volume;
            public ComputeBuffer vertexBuffer;
            public ComputeBuffer countBuffer;
        }
        
        // Matches shader definition
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct PhysicsTriangle
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

            _edgeTableBuffer = new ComputeBuffer(256, 4);
            _edgeTableBuffer.SetData(MarchingCubesTables.EdgeTable);

            _triTableBuffer = new ComputeBuffer(256 * 16, 4);
            _triTableBuffer.SetData(MarchingCubesTables.TriTable);
        }

        private void OnDestroy()
        {
            if (_edgeTableBuffer != null) _edgeTableBuffer.Release();
            if (_triTableBuffer != null) _triTableBuffer.Release();
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
                volume.WorldSize,
                _edgeTableBuffer,
                _triTableBuffer
            );

            // Request Readback for Count
            ComputeBuffer.CopyCount(vertexOutput, countBuffer, 0);

            var req = new PhysicsRequest { volume = volume, vertexBuffer = vertexOutput, countBuffer = countBuffer };
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
                ClearCollider(context.volume);
                return;
            }

            int maxTriangles = maxVertices / 3;
            if (triangleCount > maxTriangles) triangleCount = maxTriangles;

            AsyncGPUReadback.Request(context.vertexBuffer, triangleCount * 36, 0, (r) => OnVertexReadback(r, context, triangleCount));
        }

        private void OnVertexReadback(AsyncGPUReadbackRequest request, PhysicsRequest context, int triangleCount)
        {
            // Always release buffers in the final callback
            context.vertexBuffer.Release();
            context.countBuffer.Release();

            if (request.hasError || context.volume == null) return;

            var inputTris = request.GetData<PhysicsTriangle>();
            
            // --- Burst Job for Welding ---
            var outputVerts = new NativeList<Vector3>(triangleCount * 3, Allocator.TempJob);
            var outputIndices = new NativeList<int>(triangleCount * 3, Allocator.TempJob);

            var job = new WeldVerticesJob
            {
                InputTriangles = inputTris,
                OutputVertices = outputVerts,
                OutputIndices = outputIndices,
                TriangleCount = triangleCount
            };

            job.Schedule().Complete();

            // --- Build Mesh ---
            Mesh mesh = new Mesh();
            mesh.name = "VoxelPhysicsMesh_" + context.volume.name;
            
            // Use IndexFormat.UInt32 if needed
            if (outputVerts.Length > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            // Direct assignment avoids some copies, but SetVertices(List) is standard.
            // Sadly Mesh.SetVertices doesn't take NativeList directly in older Unity versions, 
            // but newer ones (2019.3+) do. Assuming support or fallback.
            // If SetVertices(NativeArray) is not available, we have to copy.
            // Using SetVertexBufferData is complex for generic mesh.
            // Let's rely on implicit conversion or ToArray() for robustness if needed, 
            // but for performance: mesh.SetVertices(outputVerts.AsArray());
            
            mesh.SetVertices(outputVerts.AsArray());
            mesh.SetIndices(outputIndices.AsArray(), MeshTopology.Triangles, 0);
            
            // Optional: Recalculate Normals/Bounds if needed for physics? 
            // Physics usually needs geometry. Colliders don't strictly need normals unless for queries.
            // But let's RecalculateBounds.
            mesh.RecalculateBounds();
            
            // Skip Optimize() for speed. Physics cooking is the main cost anyway.
            mesh.UploadMeshData(true);

            AssignMeshToCollider(context.volume, mesh);

            outputVerts.Dispose();
            outputIndices.Dispose();
        }

        private void AssignMeshToCollider(VoxelVolume volume, Mesh mesh)
        {
            if (volume == null) return;
            volume.meshCol.gameObject.SetActive(true);
            
            // For debug visualization
            if (volume.meshFil != null) volume.meshFil.sharedMesh = mesh;

            volume.meshCol.sharedMesh = mesh;
        }

        [BurstCompile]
        struct WeldVerticesJob : IJob
        {
            [ReadOnly] public NativeArray<PhysicsTriangle> InputTriangles;
            public NativeList<Vector3> OutputVertices;
            public NativeList<int> OutputIndices;
            public int TriangleCount;

            public void Execute()
            {
                // Heuristic size: count * 1.5 vertices roughly? 
                // Or just use count * 3 capacity to be safe.
                var map = new NativeHashMap<float3, int>(TriangleCount * 3, Allocator.Temp);

                for (int i = 0; i < TriangleCount; i++)
                {
                    PhysicsTriangle t = InputTriangles[i];
                    ProcessVertex(t.v1, ref map);
                    ProcessVertex(t.v2, ref map);
                    ProcessVertex(t.v3, ref map);
                }
                
                map.Dispose();
            }

            private void ProcessVertex(float3 v, ref NativeHashMap<float3, int> map)
            {
                // We use float3 as key.
                if (map.TryGetValue(v, out int index))
                {
                    OutputIndices.Add(index);
                }
                else
                {
                    index = OutputVertices.Length;
                    OutputVertices.Add(v);
                    map.Add(v, index);
                    OutputIndices.Add(index);
                }
            }
        }
    }
}
