using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.IO;
using System.IO.Compression;
using System;
using VoxelEngine.Core.Buffers;
using VoxelEngine.Core.Generators;
using VoxelEngine.Core.Serialization;
using VoxelEngine.Core.Data;

namespace VoxelEngine.Editor
{
    public class MeshToVoxelWindow : EditorWindow
    {
        private Mesh sourceMesh;
        private GameObject sourceGameObject;
        private float importScale = 1.0f;
        private int voxelMaterialID = 1;
        private int gridResolution = 64;
        private string outputFilename = "Assets/Resources/NewVoxelVolume.vxvol";

        [MenuItem("Voxel/Mesh To Voxel Converter")]
        public static void ShowWindow()
        {
            GetWindow<MeshToVoxelWindow>("Mesh Voxelizer");
        }

        private void OnGUI()
        {
            GUILayout.Label("Configuration", EditorStyles.boldLabel);

            sourceGameObject = (GameObject)EditorGUILayout.ObjectField("Source GameObject", sourceGameObject, typeof(GameObject), true);
            if (sourceGameObject != null)
            {
                MeshFilter mf = sourceGameObject.GetComponent<MeshFilter>();
                if (mf != null) sourceMesh = mf.sharedMesh;
                
                SkinnedMeshRenderer smr = sourceGameObject.GetComponent<SkinnedMeshRenderer>();
                if (smr != null) sourceMesh = smr.sharedMesh;
            }
            
            sourceMesh = (Mesh)EditorGUILayout.ObjectField("Source Mesh", sourceMesh, typeof(Mesh), false);
            importScale = EditorGUILayout.FloatField("Import Scale", importScale);
            voxelMaterialID = EditorGUILayout.IntField("Voxel Material ID", voxelMaterialID);
            
            gridResolution = EditorGUILayout.IntField("Grid Resolution", gridResolution);
            if (!IsPowerOfTwo(gridResolution))
            {
                EditorGUILayout.HelpBox("Resolution must be a Power of 2 (e.g., 32, 64, 128).", MessageType.Warning);
            }

            GUILayout.Space(10);
            GUILayout.Label("Output", EditorStyles.boldLabel);
            outputFilename = EditorGUILayout.TextField("Output Filename", outputFilename);

            GUILayout.Space(20);

            if (GUILayout.Button("Voxelize & Save"))
            {
                if (ValidateInputs())
                {
                    VoxelizeAndSave();
                }
            }
        }

        private bool ValidateInputs()
        {
            if (sourceMesh == null)
            {
                EditorUtility.DisplayDialog("Error", "Please assign a Source Mesh.", "OK");
                return false;
            }
            if (!IsPowerOfTwo(gridResolution))
            {
                EditorUtility.DisplayDialog("Error", "Grid Resolution must be a power of 2.", "OK");
                return false;
            }
            if (string.IsNullOrEmpty(outputFilename))
            {
                EditorUtility.DisplayDialog("Error", "Please specify an Output Filename.", "OK");
                return false;
            }
            return true;
        }

        struct Triangle
        {
            public Vector3 v0;
            public Vector3 v1;
            public Vector3 v2;
        }

        private void VoxelizeAndSave()
        {
            Debug.Log($"Starting Voxelization pipeline for {sourceMesh.name}...");

            // --- Phase 1: Mesh Data Prep ---
            List<Triangle> triangles = new List<Triangle>();
            Vector3[] vertices = sourceMesh.vertices;
            
            for(int i=0; i<vertices.Length; i++) vertices[i] *= importScale;

            for (int sub = 0; sub < sourceMesh.subMeshCount; sub++)
            {
                int[] indices = sourceMesh.GetTriangles(sub);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    triangles.Add(new Triangle
                    {
                        v0 = vertices[indices[i]],
                        v1 = vertices[indices[i+1]],
                        v2 = vertices[indices[i+2]]
                    });
                }
            }

            if (triangles.Count == 0) return;

            // Calculate Bounds
            Bounds bounds = new Bounds(triangles[0].v0, Vector3.zero);
            foreach (var t in triangles)
            {
                bounds.Encapsulate(t.v0);
                bounds.Encapsulate(t.v1);
                bounds.Encapsulate(t.v2);
            }
            
            float maxDim = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            maxDim *= 1.1f; // Padding
            Vector3 boundsSize = new Vector3(maxDim, maxDim, maxDim);
            Vector3 boundsMin = bounds.center - boundsSize * 0.5f;

            // --- GPU Setup ---
            ComputeShader sdfShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Scripts/Core/Compute/MeshSDF.compute");
            ComputeShader svoShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Scripts/Core/Compute/MeshToSVO.compute");

            if (sdfShader == null || svoShader == null)
            {
                Debug.LogError("Compute Shaders not found!");
                return;
            }

            // Phase 2: Mesh -> Dense SDF
            GraphicsBuffer triBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, triangles.Count, Marshal.SizeOf<Triangle>());
            triBuffer.SetData(triangles);
            
            // Note: Using GraphicsBuffer for compatibility with SVOGenerator
            GraphicsBuffer sdfBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gridResolution * gridResolution * gridResolution, sizeof(float));

            int kernelSDF = sdfShader.FindKernel("CSMain");
            sdfShader.SetBuffer(kernelSDF, "_Triangles", triBuffer);
            sdfShader.SetBuffer(kernelSDF, "_SDFBuffer", sdfBuffer);
            sdfShader.SetInt("_TriangleCount", triangles.Count);
            sdfShader.SetInt("_Resolution", gridResolution);
            sdfShader.SetVector("_BoundsMin", boundsMin);
            sdfShader.SetVector("_BoundsSize", boundsSize);

            int threads = Mathf.CeilToInt(gridResolution / 8.0f);
            sdfShader.Dispatch(kernelSDF, threads, threads, threads);
            
            triBuffer.Release(); // Done with triangles

            // --- Phase 3: Dense SDF -> Sparse Voxel Octree (SVO) ---
            // Estimate max capacity (conservative estimate)
            int maxNodes = 200000; 
            int maxBricks = 100000;
            
            SVOBufferManager bufferManager = new SVOBufferManager(maxNodes, maxBricks);
            
            // Dispatch generation
            SVOGenerator.BuildFromSDF(svoShader, bufferManager, gridResolution, sdfBuffer, voxelMaterialID);
            
            sdfBuffer.Release(); // Done with SDF

            // --- Phase 4: Serialization ---
            try
            {
                PerformSerialization(bufferManager, outputFilename, gridResolution);
            }
            catch (Exception e)
            {
                Debug.LogError($"Serialization Failed: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                bufferManager.Dispose();
            }

            AssetDatabase.Refresh();
        }

        private void PerformSerialization(SVOBufferManager buffers, string filePath, int resolution)
        {
            // 1. Read Counters (Synchronous)
            // [0]=NodeCount, [1]=PayloadCount, [2]=BrickFloatCount
            uint[] counters = new uint[3];
            buffers.CounterBuffer.GetData(counters);

            int nodeCount = (int)counters[0];
            int payloadCount = (int)counters[1];
            int brickFloatCount = (int)counters[2];

            Debug.Log($"Extracting Data... Nodes: {nodeCount}, Payloads: {payloadCount}, Bricks: {brickFloatCount/64}");

            if (nodeCount == 0)
            {
                Debug.LogWarning("No nodes generated. Volume is empty.");
                return;
            }

            // 2. Buffer Extraction (Read only relevant data)
            SVONode[] nodes = new SVONode[nodeCount];
            buffers.NodeBuffer.GetData(nodes, 0, 0, nodeCount);

            VoxelPayload[] payloads = new VoxelPayload[payloadCount];
            if (payloadCount > 0)
                buffers.PayloadBuffer.GetData(payloads, 0, 0, payloadCount);

            float[] brickData = new float[brickFloatCount];
            uint[] brickMaterials = new uint[brickFloatCount];
            if (brickFloatCount > 0)
            {
                buffers.BrickBuffer.GetData(brickData, 0, 0, brickFloatCount);
                buffers.BrickMaterialBuffer.GetData(brickMaterials, 0, 0, brickFloatCount);
            }

            // 3. File Construction
            using (var fs = new FileStream(filePath, FileMode.Create))
            using (var writer = new BinaryWriter(fs))
            {
                // Write Header
                var header = new VoxelFileFormat.Header
                {
                    Magic = VoxelFileFormat.MAGIC,
                    Version = VoxelFileFormat.VERSION,
                    Resolution = resolution,
                    NodeCount = nodeCount,
                    PayloadCount = payloadCount,
                    BrickFloatCount = brickFloatCount
                };
                header.Write(writer);

                // Write Compressed Blocks
                WriteCompressedBlock(writer, nodes);
                WriteCompressedBlock(writer, payloads);
                WriteCompressedBlock(writer, brickData);
                WriteCompressedBlock(writer, brickMaterials);
            }

            Debug.Log($"<color=green>Success!</color> Saved Voxel Volume to: {filePath}");
        }

        // Helper to safely write arrays of structs or primitives using GCHandle
        private void WriteCompressedBlock<T>(BinaryWriter writer, T[] data) where T : struct
        {
            if (data == null || data.Length == 0)
            {
                writer.Write((int)0); // Size 0
                return;
            }

            int elementSize = Marshal.SizeOf<T>();
            int totalBytes = data.Length * elementSize;
            byte[] rawBytes = new byte[totalBytes];
            
            // Fix: Buffer.BlockCopy fails on struct arrays. Use GCHandle to pin and copy.
            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject();
                Marshal.Copy(ptr, rawBytes, 0, totalBytes);
            }
            finally
            {
                handle.Free();
            }

            // GZip Compression
            using (var ms = new MemoryStream())
            {
                using (var gzip = new GZipStream(ms, CompressionMode.Compress))
                {
                    gzip.Write(rawBytes, 0, rawBytes.Length);
                }
                byte[] compressed = ms.ToArray();

                // Write size then data
                writer.Write(compressed.Length);
                writer.Write(compressed);
            }
        }

        private bool IsPowerOfTwo(int x)
        {
            return (x != 0) && ((x & (x - 1)) == 0);
        }
    }
}