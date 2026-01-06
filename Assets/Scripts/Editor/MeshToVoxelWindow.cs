using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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

        // Internal Data for Phase 2 Hand-off
        public float[] denseSDF; // The calculated SDF grid

        [MenuItem("Voxel/Mesh To Voxel Converter")]
        public static void ShowWindow()
        {
            GetWindow<MeshToVoxelWindow>("Mesh Voxelizer");
        }

        private void OnGUI()
        {
            GUILayout.Label("Configuration", EditorStyles.boldLabel);

            // Source selection
            sourceGameObject = (GameObject)EditorGUILayout.ObjectField("Source GameObject", sourceGameObject, typeof(GameObject), true);
            if (sourceGameObject != null)
            {
                MeshFilter mf = sourceGameObject.GetComponent<MeshFilter>();
                if (mf != null) sourceMesh = mf.sharedMesh;
                
                SkinnedMeshRenderer smr = sourceGameObject.GetComponent<SkinnedMeshRenderer>();
                if (smr != null) sourceMesh = smr.sharedMesh;
            }
            
            // Allow manual override or direct assignment
            sourceMesh = (Mesh)EditorGUILayout.ObjectField("Source Mesh", sourceMesh, typeof(Mesh), false);

            importScale = EditorGUILayout.FloatField("Import Scale", importScale);
            
            voxelMaterialID = EditorGUILayout.IntField("Voxel Material ID", voxelMaterialID);
            
            gridResolution = EditorGUILayout.IntField("Grid Resolution", gridResolution);
            if (!IsPowerOfTwo(gridResolution))
            {
                EditorGUILayout.HelpBox("Resolution must be a Power of 2 (e.g., 32, 64, 128, 256).", MessageType.Warning);
            }

            GUILayout.Space(10);
            GUILayout.Label("Output", EditorStyles.boldLabel);
            outputFilename = EditorGUILayout.TextField("Output Filename", outputFilename);

            GUILayout.Space(20);

            if (GUILayout.Button("Voxelize Mesh"))
            {
                if (ValidateInputs())
                {
                    Voxelize();
                }
            }

            if (denseSDF != null)
            {
                GUILayout.Space(10);
                GUILayout.Label($"Voxelization Complete. SDF Data Size: {denseSDF.Length}", EditorStyles.helpBox);
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

        private void Voxelize()
        {
            Debug.Log($"Starting Voxelization for {sourceMesh.name} at resolution {gridResolution}...");

            // 1. Prepare Mesh Data
            List<Triangle> triangles = new List<Triangle>();
            Vector3[] vertices = sourceMesh.vertices;
            
            // Apply scale to vertices immediately? Or in shader?
            // Doing it here simplifies bounds calc.
            for(int i=0; i<vertices.Length; i++)
            {
                vertices[i] *= importScale;
            }

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

            if (triangles.Count == 0)
            {
                Debug.LogError("No triangles found in mesh!");
                return;
            }

            // 2. Calculate Bounds
            Bounds bounds = new Bounds(triangles[0].v0, Vector3.zero);
            foreach (var t in triangles)
            {
                bounds.Encapsulate(t.v0);
                bounds.Encapsulate(t.v1);
                bounds.Encapsulate(t.v2);
            }
            
            // Center the bounds?
            // The prompt says: "Apply the Import Scale and an offset to center the mesh within the voxel grid's coordinate system (e.g., center it at resolution / 2)."
            // The ComputeShader expects _BoundsMin and _BoundsSize to map 0..1 to WorldSpace.
            // If we want the mesh centered in the grid volume:
            // Grid Volume is defined by resolution. Let's say units = voxels.
            // But usually, we map the Mesh Bounds to fit *inside* the grid.
            // Or we define the Grid's World Bounds to encompass the Mesh.
            // Strategy: Make the Grid Bounds slightly larger than Mesh Bounds.
            
            float maxDim = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            maxDim *= 1.1f; // 10% padding
            Vector3 center = bounds.center;
            Vector3 boundsSize = new Vector3(maxDim, maxDim, maxDim);
            Vector3 boundsMin = center - boundsSize * 0.5f;

            Debug.Log($"Bounds: Min {boundsMin}, Size {boundsSize}, Triangles: {triangles.Count}");

            // 3. Setup Compute Shader
            ComputeShader compute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Scripts/Core/Compute/MeshSDF.compute");
            if (compute == null)
            {
                Debug.LogError("Could not find MeshSDF.compute!");
                return;
            }

            int kernel = compute.FindKernel("CSMain");

            // Buffers
            ComputeBuffer triBuffer = new ComputeBuffer(triangles.Count, Marshal.SizeOf<Triangle>());
            triBuffer.SetData(triangles);

            ComputeBuffer sdfBuffer = new ComputeBuffer(gridResolution * gridResolution * gridResolution, sizeof(float));

            compute.SetBuffer(kernel, "_Triangles", triBuffer);
            compute.SetBuffer(kernel, "_SDFBuffer", sdfBuffer);
            compute.SetInt("_TriangleCount", triangles.Count);
            compute.SetInt("_Resolution", gridResolution);
            compute.SetVector("_BoundsMin", boundsMin);
            compute.SetVector("_BoundsSize", boundsSize);

            // 4. Dispatch
            int threadGroups = Mathf.CeilToInt(gridResolution / 8.0f);
            compute.Dispatch(kernel, threadGroups, threadGroups, threadGroups);

            // 5. Readback
            denseSDF = new float[gridResolution * gridResolution * gridResolution];
            sdfBuffer.GetData(denseSDF);

            // Cleanup
            triBuffer.Release();
            sdfBuffer.Release();

            Debug.Log("Voxelization Complete. Data generated.");
            
            // Force repaint to show result label
            Repaint();
        }

        private bool IsPowerOfTwo(int x)
        {
            return (x != 0) && ((x & (x - 1)) == 0);
        }
    }
}