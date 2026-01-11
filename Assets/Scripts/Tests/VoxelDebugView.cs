using UnityEngine;

namespace VoxelEngine.Debugging
{
    [ExecuteAlways]
    public class VoxelDebugView : MonoBehaviour
    {
        [Header("Debug Settings")]
        [Tooltip("Enable to render each 4x4x4 brick with a unique random color.")]
        public bool showBricks = false;

        [Tooltip("Enable to visualize normal directions (already in shader).")]
        public bool showNormals = false;

        private static readonly int DebugViewBricksId = Shader.PropertyToID("_DebugViewBricks");
        private static readonly int DebugViewNormalsId = Shader.PropertyToID("_DebugViewNormals");

        private void Update()
        {
            // Update global shader variables
            Shader.SetGlobalFloat(DebugViewBricksId, showBricks ? 1.0f : 0.0f);
            Shader.SetGlobalFloat(DebugViewNormalsId, showNormals ? 1.0f : 0.0f);
        }

        private void OnDisable()
        {
            // Reset when disabled
            Shader.SetGlobalFloat(DebugViewBricksId, 0.0f);
            Shader.SetGlobalFloat(DebugViewNormalsId, 0.0f);
        }
    }
}