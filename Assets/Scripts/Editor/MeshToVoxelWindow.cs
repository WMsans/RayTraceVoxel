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
using Unity.Collections;

namespace VoxelEngine.Editor
{
    public class MeshToVoxelWindow : EditorWindow
    {
        // --- Data Structures ---
        [System.Serializable]
        public class MaterialMapping
        {
            public Material unityMaterial;
            public int voxelMaterialID = 1; // Default to Solid
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TriangleEx
        {
            public Vector3 v0;
            public Vector3 v1;
            public Vector3 v2;
            public int materialId;
        }

        // --- State ---
        private GameObject sourceRoot;
        private float importScale = 1.0f;
        private int gridResolution = 64;
        private string outputFilename = "Assets/Resources/NewVoxelVolume.vxvol";

        private List<MaterialMapping> materialMappings = new List<MaterialMapping>();
        private Vector2 scrollPos;

        [MenuItem("Window/Voxel/Mesh To Voxel Converter")]
        public static void ShowWindow()
        {
            GetWindow<MeshToVoxelWindow>("Mesh Voxelizer");
        }

        private void OnGUI()
        {
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            GUILayout.Label("1. Input Configuration", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            sourceRoot = (GameObject)EditorGUILayout.ObjectField("Source Root Object", sourceRoot, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck() && sourceRoot != null)
            {
                ScanMaterials();
            }

            importScale = EditorGUILayout.FloatField("Import Scale", importScale);
            gridResolution = EditorGUILayout.IntField("Grid Resolution", gridResolution);

            if (!IsPowerOfTwo(gridResolution))
            {
                EditorGUILayout.HelpBox("Resolution must be a Power of 2 (32, 64, 128).", MessageType.Warning);
            }

            GUILayout.Space(10);
            GUILayout.Label("2. Material Mapping", EditorStyles.boldLabel);
            
            if (GUILayout.Button("Refresh Materials"))
            {
                ScanMaterials();
            }

            if (materialMappings.Count > 0)
            {
                foreach (var map in materialMappings)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.ObjectField(map.unityMaterial, typeof(Material), false, GUILayout.Width(200));
                    EditorGUILayout.LabelField("=>", GUILayout.Width(30));
                    map.voxelMaterialID = EditorGUILayout.IntField(map.voxelMaterialID);
                    EditorGUILayout.EndHorizontal();
                }
            }
            else
            {
                EditorGUILayout.HelpBox("No Renderers found in hierarchy.", MessageType.Info);
            }

            GUILayout.Space(10);
            GUILayout.Label("3. Output", EditorStyles.boldLabel);
            outputFilename = EditorGUILayout.TextField("Output Filename", outputFilename);

            GUILayout.Space(20);

            if (GUILayout.Button("Voxelize & Save"))
            {
                if (ValidateInputs())
                {
                    PrepareAndProcess();
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void ScanMaterials()
        {
            materialMappings.Clear();
            if (sourceRoot == null) return;

            var renderers = sourceRoot.GetComponentsInChildren<Renderer>();
            HashSet<Material> uniqueMats = new HashSet<Material>();

            foreach (var r in renderers)
            {
                if (r is MeshRenderer || r is SkinnedMeshRenderer)
                {
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m != null) uniqueMats.Add(m);
                    }
                }
            }

            foreach (var m in uniqueMats)
            {
                materialMappings.Add(new MaterialMapping { unityMaterial = m, voxelMaterialID = 1 });
            }
        }

        private bool ValidateInputs()
        {
            if (sourceRoot == null)
            {
                EditorUtility.DisplayDialog("Error", "Please assign a Root Object.", "OK");
                return false;
            }
            if (string.IsNullOrEmpty(outputFilename)) return false;
            return true;
        }

        // ----------------------------------------------------------------------------------
        // CORE LOGIC: The Super Buffer Construction
        // ----------------------------------------------------------------------------------

        private void PrepareAndProcess()
        {
            Debug.Log($"Starting Voxelization pipeline for hierarchy: {sourceRoot.name}...");

            // 1. Build Lookup Dictionary for speed
            Dictionary<Material, int> matLookup = new Dictionary<Material, int>();
            foreach (var map in materialMappings)
            {
                if (map.unityMaterial != null) matLookup[map.unityMaterial] = map.voxelMaterialID;
            }

            // 2. Flatten Hierarchy & Bake Transforms
            List<TriangleEx> superBuffer = new List<TriangleEx>();
            RecurseAndCollect(sourceRoot.transform, superBuffer, matLookup);

            if (superBuffer.Count == 0)
            {
                Debug.LogError("No geometry found in hierarchy!");
                return;
            }

            Debug.Log($"Phase 1 Complete: Aggregated {superBuffer.Count} triangles from hierarchy.");

            // 3. Calculate Global Bounds
            Bounds globalBounds = new Bounds(superBuffer[0].v0, Vector3.zero);
            foreach (var t in superBuffer)
            {
                globalBounds.Encapsulate(t.v0);
                globalBounds.Encapsulate(t.v1);
                globalBounds.Encapsulate(t.v2);
            }

            // Expand slightly to ensure no boundary clipping
            float maxDim = Mathf.Max(globalBounds.size.x, Mathf.Max(globalBounds.size.y, globalBounds.size.z));
            maxDim *= 1.05f; 
            Vector3 boundsSize = new Vector3(maxDim, maxDim, maxDim);
            Vector3 boundsMin = globalBounds.center - boundsSize * 0.5f;

            // 4. Dispatch to GPU Pipeline (Modified to handle TriangleEx)
            ExecuteGPUPipeline(superBuffer, boundsMin, boundsSize);
        }

        private void RecurseAndCollect(Transform node, List<TriangleEx> buffer, Dictionary<Material, int> matLookup)
        {
            Mesh mesh = null;
            Material[] sharedMats = null;

            // Handle MeshFilter + MeshRenderer
            MeshFilter mf = node.GetComponent<MeshFilter>();
            MeshRenderer mr = node.GetComponent<MeshRenderer>();
            
            if (mf != null && mr != null && mf.sharedMesh != null)
            {
                mesh = mf.sharedMesh;
                sharedMats = mr.sharedMaterials;
            }
            
            // Handle SkinnedMeshRenderer
            SkinnedMeshRenderer smr = node.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
            {
                mesh = smr.sharedMesh;
                sharedMats = smr.sharedMaterials;
                // Note: We are baking the "bind pose" mesh transformed by the gameobject. 
                // Creating a snapshot of the currently deformed skinned mesh is more complex (requires Mesh.BakeMesh).
            }

            if (mesh != null)
            {
                // Bake Transform: Local -> World
                Matrix4x4 localToWorld = node.localToWorldMatrix;

                // Scale adjustment if user requested import scale
                if (importScale != 1.0f)
                {
                    Matrix4x4 scaleMatrix = Matrix4x4.Scale(Vector3.one * importScale);
                    localToWorld = localToWorld * scaleMatrix;
                }

                Vector3[] originalVerts = mesh.vertices;
                Vector3[] worldVerts = new Vector3[originalVerts.Length];

                // Optimization: Transform all verts once per mesh
                for (int i = 0; i < originalVerts.Length; i++)
                {
                    worldVerts[i] = localToWorld.MultiplyPoint3x4(originalVerts[i]);
                }

                // Iterate Submeshes
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    // Determine Material ID
                    int voxelId = 1; // Default
                    if (sharedMats != null && sub < sharedMats.Length && sharedMats[sub] != null)
                    {
                        if (matLookup.TryGetValue(sharedMats[sub], out int id))
                            voxelId = id;
                    }

                    int[] indices = mesh.GetTriangles(sub);
                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        buffer.Add(new TriangleEx
                        {
                            v0 = worldVerts[indices[i]],
                            v1 = worldVerts[indices[i + 1]],
                            v2 = worldVerts[indices[i + 2]],
                            materialId = voxelId
                        });
                    }
                }
            }

            // Recurse Children
            foreach (Transform child in node)
            {
                RecurseAndCollect(child, buffer, matLookup);
            }
        }

        private void ExecuteGPUPipeline(List<TriangleEx> triangles, Vector3 boundsMin, Vector3 boundsSize)
        {
            Debug.Log("Preparing GPU Pipeline...");

            // --- GPU Setup ---
            ComputeShader sdfShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Scripts/Core/Compute/MeshSDF.compute");
            ComputeShader svoShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Scripts/Core/Compute/MeshToSVO.compute");

            if (sdfShader == null || svoShader == null)
            {
                Debug.LogError("Compute Shaders not found!");
                return;
            }

            // --- Phase 1: SDF & Material Map ---
            
            // 1. Upload Super Buffer
            GraphicsBuffer triBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, triangles.Count, Marshal.SizeOf<TriangleEx>());
            triBuffer.SetData(triangles);

            // 2. Prepare Destination Buffers
            int totalVoxels = gridResolution * gridResolution * gridResolution;
            GraphicsBuffer sdfBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalVoxels, sizeof(float));
            GraphicsBuffer denseMaterialBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalVoxels, sizeof(int));

            // 3. Dispatch MeshSDF
            int sdfKernel = sdfShader.FindKernel("CSMain");
            sdfShader.SetBuffer(sdfKernel, "_Triangles", triBuffer);
            sdfShader.SetBuffer(sdfKernel, "_SDFBuffer", sdfBuffer);
            sdfShader.SetBuffer(sdfKernel, "_DenseMaterialBuffer", denseMaterialBuffer);
            sdfShader.SetInt("_TriangleCount", triangles.Count);
            sdfShader.SetInt("_Resolution", gridResolution);
            sdfShader.SetVector("_BoundsMin", boundsMin);
            sdfShader.SetVector("_BoundsSize", boundsSize);

            int threadGroups = Mathf.CeilToInt(gridResolution / 8.0f);
            sdfShader.Dispatch(sdfKernel, threadGroups, threadGroups, threadGroups);
            
            Debug.Log($"Phase 1 Complete (SDF Generation).");

            // --- Phase 2: SVO Generation ---
            
            // 1. Allocate SVO Buffers
            int maxNodes = 250000;
            int maxBricks = 100000;
            
            GraphicsBuffer nodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxNodes, Marshal.SizeOf<SVONode>());
            GraphicsBuffer payloadBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxNodes, Marshal.SizeOf<VoxelPayload>());
            GraphicsBuffer brickBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxBricks * 64, sizeof(float));
            GraphicsBuffer brickMaterialBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxBricks * 64, sizeof(uint));
            GraphicsBuffer counterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, sizeof(uint)); 
            // Counters: [0]=Nodes, [1]=Payloads, [2]=BrickFloatIndex, [3]=padding/debug

            counterBuffer.SetData(new uint[] { 0, 0, 0, 0 });

            // 2. Init Dense Structure (Levels 0-3)
            int initKernel = svoShader.FindKernel("InitDenseStructure");
            svoShader.SetInt("_GridSize", gridResolution);
            svoShader.SetBuffer(initKernel, "_NodeBuffer", nodeBuffer);
            svoShader.SetBuffer(initKernel, "_CounterBuffer", counterBuffer);
            
            // Dispatch enough threads for the dense part (4681 nodes for res 64)
            svoShader.Dispatch(initKernel, Mathf.CeilToInt(4681f / 64f), 1, 1);

            // 3. Build Bricks (Leaf Nodes)
            int buildKernel = svoShader.FindKernel("BuildBricks");
            svoShader.SetBuffer(buildKernel, "_NodeBuffer", nodeBuffer);
            svoShader.SetBuffer(buildKernel, "_PayloadBuffer", payloadBuffer);
            svoShader.SetBuffer(buildKernel, "_BrickBuffer", brickBuffer);
            svoShader.SetBuffer(buildKernel, "_BrickMaterialBuffer", brickMaterialBuffer);
            svoShader.SetBuffer(buildKernel, "_CounterBuffer", counterBuffer);
            svoShader.SetBuffer(buildKernel, "_DenseSDFBuffer", sdfBuffer);
            svoShader.SetBuffer(buildKernel, "_DenseMaterialBuffer", denseMaterialBuffer);
            
            // Dispatch over Brick Grid
            int bricksAxis = gridResolution / 4; // 16 for 64
            int brickGroups = Mathf.CeilToInt(bricksAxis / 8.0f);
            svoShader.Dispatch(buildKernel, brickGroups, brickGroups, brickGroups);

            Debug.Log($"Phase 2 Complete (SVO Generation). Reading back...");

            // --- Phase 3: Readback & Save (Blocking) ---

            try
            {
                // Read Counters
                uint[] counters = new uint[4];
                counterBuffer.GetData(counters);
                
                int nodeCount = (int)counters[0];
                int payloadCount = (int)counters[1];
                int brickFloatCount = (int)counters[2];

                Debug.Log($"Readback Counts -> Nodes: {nodeCount}, Payloads: {payloadCount}, Bricks: {brickFloatCount/64}");

                // Read Data
                SVONode[] nodes = new SVONode[nodeCount];
                nodeBuffer.GetData(nodes, 0, 0, nodeCount);

                VoxelPayload[] payloads = new VoxelPayload[payloadCount];
                if (payloadCount > 0) 
                    payloadBuffer.GetData(payloads, 0, 0, payloadCount);

                float[] brickData = new float[brickFloatCount];
                if (brickFloatCount > 0)
                    brickBuffer.GetData(brickData, 0, 0, brickFloatCount);

                uint[] brickMaterials = new uint[brickFloatCount];
                if (brickFloatCount > 0)
                    brickMaterialBuffer.GetData(brickMaterials, 0, 0, brickFloatCount);

                // Save
                WriteFile(outputFilename, gridResolution, nodeCount, payloadCount, brickFloatCount, nodes, payloads, brickData, brickMaterials);
                
                // Alert User
                EditorUtility.DisplayDialog("Success", $"Voxel Volume saved to {outputFilename}", "OK");
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                Debug.LogError($"Error during readback/save: {e.Message}");
            }
            finally
            {
                // Cleanup
                triBuffer.Release();
                sdfBuffer.Release();
                denseMaterialBuffer.Release();
                nodeBuffer.Release();
                payloadBuffer.Release();
                brickBuffer.Release();
                brickMaterialBuffer.Release();
                counterBuffer.Release();
            }
        }

        private bool IsPowerOfTwo(int x)
        {
            return (x != 0) && ((x & (x - 1)) == 0);
        }

        // --- Serialization Helpers ---

        private void WriteFile(string filePath, int resolution, int nodeCount, int payloadCount, int brickFloatCount,
            SVONode[] nodes, VoxelPayload[] payloads, float[] brickData, uint[] brickMaterials)
        {
            // Create directory if it doesn't exist
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var fs = new FileStream(filePath, FileMode.Create))
            using (var writer = new BinaryWriter(fs))
            {
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

                WriteCompressedBlock(writer, nodes);
                WriteCompressedBlock(writer, payloads);
                WriteCompressedBlock(writer, brickData);
                WriteCompressedBlock(writer, brickMaterials);
            }
            
            Debug.Log($"Saved voxel volume to {filePath}. Nodes: {nodeCount}, Payloads: {payloadCount}, Bricks: {brickFloatCount / 64}");
        }

        private void WriteCompressedBlock<T>(BinaryWriter writer, T[] data) where T : struct
        {
            int elementSize = Marshal.SizeOf<T>();
            int size = data.Length * elementSize;
            byte[] bytes = new byte[size];
            
            // Copy data to byte array
            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                Marshal.Copy(handle.AddrOfPinnedObject(), bytes, 0, size);
            }
            finally
            {
                handle.Free();
            }

            // Compress
            using (var ms = new MemoryStream())
            {
                using (var gzip = new GZipStream(ms, CompressionMode.Compress))
                {
                    gzip.Write(bytes, 0, bytes.Length);
                }
                byte[] compressed = ms.ToArray();
                
                // Write size of compressed block for easier reading
                writer.Write(compressed.Length);
                writer.Write(compressed);
            }
        }
    }
}