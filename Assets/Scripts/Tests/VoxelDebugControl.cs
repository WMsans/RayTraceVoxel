using UnityEngine;

namespace VoxelEngine.Core.Debugging
{
    [ExecuteAlways]
    public class VoxelDebugControl : MonoBehaviour
    {
        [Header("Normal Artifact Debugger")]
        [Tooltip("Standard Trilinear Gradient requires d >= 0.5 to look smooth. Small values (0.1) show 'squares'.")]
        [Range(0.01f, 2.0f)]
        public float normalDelta = 0.1f;

        [Tooltip("Check this to visualize the raw normals. Faceted look = Logic Issue.")]
        public bool showNormals = false;

        private void Update()
        {
            // Update global shader variables
            Shader.SetGlobalFloat("_DebugNormalDelta", normalDelta);
            Shader.SetGlobalFloat("_DebugViewNormals", showNormals ? 1.0f : 0.0f);
        }

        private void OnGUI()
        {
            // Simple on-screen GUI for quick testing
            GUILayout.BeginArea(new Rect(10, 10, 300, 150), "Voxel Debug", GUI.skin.box);
            GUILayout.Label($"Normal Delta: {normalDelta:F3}");
            normalDelta = GUILayout.HorizontalSlider(normalDelta, 0.01f, 2.0f);
            
            showNormals = GUILayout.Toggle(showNormals, "Show Normals");
            
            if (GUILayout.Button("Set Optimal (0.5)")) normalDelta = 0.5f;
            if (GUILayout.Button("Set Faceted (0.1)")) normalDelta = 0.1f;
            
            GUILayout.EndArea();
        }
    }
}