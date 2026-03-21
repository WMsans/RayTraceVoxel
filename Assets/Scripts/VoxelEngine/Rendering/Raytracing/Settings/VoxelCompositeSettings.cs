using UnityEngine;

namespace VoxelEngine.Core.Rendering
{
    [System.Serializable]
    public class VoxelCompositeSettings
    {
        public enum UpscalingMode { Bilinear, SpatialFSR }

        public Shader compositeShader;
        public Shader fxaaShader;

        [Header("Upscaling")]
        public UpscalingMode upscalingMode = UpscalingMode.SpatialFSR;
        [Range(0.0f, 1.0f)] public float sharpness = 0.5f;

        [Header("Anti-Aliasing")]
        public bool enableFXAA = true;

        [Header("Outline")]
        public bool enableOutline = false;
        [Range(0.0f, 5.0f)] public float outlineThickness = 1.0f;
        [Range(0.0f, 1.0f)] public float outlineShadowStrength = 0.5f;
        [Range(0.0f, 1.0f)] public float outlineStrength = 0.5f;
        public Color outlineColor = Color.black;

        [Header("Normal Highlight")]
        [Range(0.0f, 1.0f)] public float normalHighlightStrength = 0.5f;
        [Range(0.0f, 2.0f)] public float normalThreshold = 0.6f;
        [Range(0.0f, 500.0f)] public float normalFadeDistance = 50.0f;
        public Color normalHighlightColor = Color.white;
    }
}
