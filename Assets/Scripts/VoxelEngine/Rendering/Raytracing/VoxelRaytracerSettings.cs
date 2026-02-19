using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    [System.Serializable]
    public class VoxelRaytracerSettings
    {
        public enum QualityLevel { High, Low, Custom }
        public enum UpscalingMode { Bilinear, SpatialFSR }
        public enum DebugMode { None, Normals, Bricks }

        public ComputeShader raytraceShader;
        public Shader taaShader;
        public Shader compositeShader;
        public Shader fxaaShader;
        public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingSkybox;

        [Header("Quality")]
        public QualityLevel qualityLevel = QualityLevel.High;
        [Range(0.1f, 1.0f)]
        public float renderScale = 1.0f;
        [Range(0.01f, 10.0f)] public float textureScale = 1.0f;
        public int iterations = 128;
        public int marchSteps = 64;

        [Header("Atmosphere")]
        public bool enableAtmosphere = true;
        public Color atmosphereColor = new Color(0.55f, 0.7f, 0.9f);
        [Range(0.0f, 0.1f)] public float atmosphereDensity = 0.005f;

        [Header("Cel Shading")]
        [Range(1, 10)]
        public int celSteps = 3;
        [Range(0.0f, 1.0f)]
        public float shadowBrightness = 0.2f;

        [Header("God Rays")]
        public bool enableGodRays = true;
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

        [Header("Upscaling & Anti-Aliasing")]
        public UpscalingMode upscalingMode = UpscalingMode.SpatialFSR;
        [Range(0.0f, 1.0f)] public float sharpness = 0.5f;
        public bool enableFXAA = true;
        public bool enableTAA = true;
        [Range(0.0f, 1.0f)] public float taaBlend = 0.93f;

        [Header("Outline")]
        public bool enableOutline = false;
        [Range(0.0f, 5.0f)] public float outlineThickness = 1.0f;

        [Header("Outline Lighting")]
        [Range(0.0f, 1.0f)] public float outlineShadowStrength = 0.5f;

        [Header("Depth Outline")]
        [Range(0.0f, 1.0f)] public float outlineStrength = 0.5f;
        public Color outlineColor = Color.black;

        [Header("Normal Highlight")]
        [Range(0.0f, 1.0f)] public float normalHighlightStrength = 0.5f;
        [Range(0.0f, 2.0f)] public float normalThreshold = 0.6f;
        [Range(0.0f, 500.0f)] public float normalFadeDistance = 50.0f;
        public Color normalHighlightColor = Color.white;

        [Header("LOD Settings")]
        [Range(1.0f, 200.0f)]
        public float lodBias = 1.0f;

        [Header("Culling")]
        public bool useCameraFarPlane = false;
        public bool cullFrustum = true;
        public float shadowDistance = 1500.0f;

        [Header("Dithering")]
        public Texture2D blueNoiseTexture;

        [Header("Debug")]
        public DebugMode debugMode = DebugMode.None;
    }
}