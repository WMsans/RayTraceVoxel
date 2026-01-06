using UnityEngine;
using UnityEditor;
using System.IO;

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

        private void Voxelize()
        {
            Debug.Log($"Starting Voxelization...\nMesh: {sourceMesh.name}\nRes: {gridResolution}\nScale: {importScale}\nMatID: {voxelMaterialID}");
            // Implementation deferred to Phase 2
        }

        private bool IsPowerOfTwo(int x)
        {
            return (x != 0) && ((x & (x - 1)) == 0);
        }
    }
}
