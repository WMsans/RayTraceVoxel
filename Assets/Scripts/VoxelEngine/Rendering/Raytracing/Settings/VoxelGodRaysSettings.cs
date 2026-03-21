using UnityEngine;

namespace VoxelEngine.Core.Rendering
{
    [System.Serializable]
    public class VoxelGodRaysSettings
    {
        public Shader godRayShader;

        [Tooltip("Threshold when the sun is directly overhead (Noon). Controls the size of the sun disk source.")]
        [Range(0.0f, 1.0f)] public float noonSunThreshold = 0.95f;

        [Tooltip("Threshold when the sun is at the horizon (Dawn/Dusk).")]
        [Range(0.0f, 1.0f)] public float dawnSunThreshold = 0.99f;

        [Range(0.0f, 5.0f)] public float rayDensity = 1.0f;
        [Range(0.0f, 1.0f)] public float rayDecay = 0.95f;
        [Range(0.0f, 1.0f)] public float rayWeight = 0.1f;
        [Range(0.0f, 5.0f)] public float rayExposure = 1.0f;
        [Range(16, 128)] public int raySamples = 32;
        public Color lightSourceColor = new Color(1.0f, 0.95f, 0.8f);
    }
}
