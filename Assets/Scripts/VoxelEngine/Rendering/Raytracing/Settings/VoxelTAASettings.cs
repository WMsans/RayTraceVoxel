using UnityEngine;

namespace VoxelEngine.Core.Rendering
{
    [System.Serializable]
    public class VoxelTAASettings
    {
        public Shader taaShader;
        [Range(0.0f, 1.0f)] public float taaBlend = 0.93f;
    }
}
