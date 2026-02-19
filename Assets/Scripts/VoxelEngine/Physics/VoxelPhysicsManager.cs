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
        [Tooltip("Base sampling stride for the highest detail (Leaf) chunks.")]
        public int stride = 4;
        [Tooltip("Max vertices limit for buffer.")]
        public int maxVertices = 65536;

        [Header("LOD & Priority")]
        [Tooltip("The viewer (usually Main Camera) used for priority sorting.")]
        public Transform viewer;
        [Tooltip("The size of the smallest chunk (Leaf Node). Used to calculate LOD ratios.")]
        public float baseChunkSize = 32.0f;

        // Use a List for sorting and a HashSet for O(1) lookups
        private List<VoxelVolume> _dirtyList = new List<VoxelVolume>();
        private HashSet<VoxelVolume> _dirtySet = new HashSet<VoxelVolume>();
        
        private float _timer;
        private ComputeBuffer _edgeTableBuffer;
        private ComputeBuffer _triTableBuffer;
        
        private struct PhysicsRequest
        {
            public VoxelVolume volume;
            public ComputeBuffer vertexBuffer;
            public ComputeBuffer countBuffer;
        }
        
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
            if (!_dirtySet.Contains(volume))
            {
                _dirtySet.Add(volume);
                _dirtyList.Add(volume);
            }
        }

        public void Remove(VoxelVolume volume)
        {
            if (volume == null) return;
            if (_dirtySet.Contains(volume))
            {
                _dirtySet.Remove(volume);
                _dirtyList.Remove(volume);
            }
        }

        public void ClearCollider(VoxelVolume volume)
        {
            if (volume.meshCol != null)
            {
                volume.meshCol.sharedMesh = null;
                volume.meshCol.enabled = false;
            }

            BoxCollider bc = volume.GetComponent<BoxCollider>();
            if (bc != null) bc.enabled = false;
        }

        private void Update()
        {
            if (_dirtyList.Count == 0) return;

            _timer += Time.deltaTime;
            if (_timer >= (1.0f / updateFrequency))
            {
                _timer = 0;
                ProcessBatch();
            }
        }

        private void ProcessBatch()
        {
            // 1. Clean up nulls or inactive volumes first
            for (int i = _dirtyList.Count - 1; i >= 0; i--)
            {
                if (_dirtyList[i] == null || !_dirtyList[i].isActiveAndEnabled)
                {
                    _dirtySet.Remove(_dirtyList[i]);
                    _dirtyList.RemoveAt(i);
                }
            }

            if (_dirtyList.Count == 0) return;

            // 2. Sort by distance to viewer (Closest First)
            if (viewer != null)
            {
                Vector3 viewPos = viewer.position;
                _dirtyList.Sort((a, b) => 
                {
                    float distA = (a.transform.position - viewPos).sqrMagnitude;
                    float distB = (b.transform.position - viewPos).sqrMagnitude;
                    return distA.CompareTo(distB);
                });
            }

            // 3. Process the top N (closest) items
            int count = Mathf.Min(batchSize, _dirtyList.Count);
            
            for (int i = 0; i < count; i++)
            {
                VoxelVolume vol = _dirtyList[i];
                
                // Double check validity (though we just cleaned)
                if (vol != null && vol.IsReady)
                {
                    DispatchPhysics(vol);
                }
                
                // Remove from Set immediately so it can be re-queued if needed
                _dirtySet.Remove(vol);
            }

            // Remove processed range from List
            _dirtyList.RemoveRange(0, count);
        }

        private void DispatchPhysics(VoxelVolume volume)
        {
            if (physicsShader == null) return;

            // --- Dynamic LoD Calculation ---
            // Calculate effective stride based on chunk size relative to base size.
            // Larger chunks (farther away) get a higher stride, reducing resolution.
            
            int useStride = stride;
            // Only apply LOD stride logic to standard terrain, typically non-transient
            if (volume.WorldSize > baseChunkSize * 1.1f && !volume.IsTransient) 
            {
                float ratio = volume.WorldSize / baseChunkSize;
                useStride = Mathf.RoundToInt(stride * ratio);
                useStride = Mathf.Max(stride, useStride); 
            }

            int maxTriangles = maxVertices / 3;
            ComputeBuffer vertexOutput = new ComputeBuffer(maxTriangles, 36, ComputeBufferType.Append);
            vertexOutput.SetCounterValue(0);

            ComputeBuffer countBuffer = new ComputeBuffer(4, sizeof(int), ComputeBufferType.IndirectArguments);
            countBuffer.SetData(new int[] { 0, 1, 0, 0 });

            // [FIX] Determine correct generation size.
            // Standard Terrain (Non-Transient): Uses Transform Scaling for LOD, so we generate in 'baseChunkSize' (unscaled space).
            // Debris (Transient): Uses 1:1 Scale, so we must generate in 'volume.WorldSize' (actual size).
            float generationSize = volume.IsTransient ? volume.WorldSize : baseChunkSize;

            PhysicsGenerator.Generate(
                physicsShader, 
                volume.BufferManager, 
                vertexOutput, 
                countBuffer, 
                volume.Resolution, 
                useStride, 
                volume.WorldOrigin,
                generationSize, 
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

            Mesh mesh = new Mesh();
            mesh.name = "VoxelPhysicsMesh_" + context.volume.name;
            
            if (outputVerts.Length > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            
            mesh.SetVertices(outputVerts.AsArray());
            mesh.SetIndices(outputIndices.AsArray(), MeshTopology.Triangles, 0);
            
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);

            AssignMeshToCollider(context.volume, mesh);

            outputVerts.Dispose();
            outputIndices.Dispose();
        }

        private void AssignMeshToCollider(VoxelVolume volume, Mesh mesh)
        {
            if (volume == null) return;
            volume.meshCol.enabled = true;
            if (volume.meshFil != null) volume.meshFil.sharedMesh = mesh;
            volume.meshCol.sharedMesh = mesh;
            if (volume.IsTransient) volume.meshCol.convex = true;
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