using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    [System.Serializable]
    public class VoxelRaytraceSettings
    {
        public enum QualityLevel { High, Low, Custom }
        public enum DebugMode { None, Normals, Bricks }

        public ComputeShader raytraceShader;
        public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingSkybox;

        [Header("Quality")]
        public QualityLevel qualityLevel = QualityLevel.High;
        [Range(0, 8)] public int bounceCount = 3;
        [Range(0.1f, 1.0f)] public float renderScale = 1.0f;
        [Range(0.01f, 10.0f)] public float textureScale = 1.0f;
        public int iterations = 128;
        public int marchSteps = 64;

        [Header("Atmosphere")]
        public bool enableAtmosphere = true;
        public Color atmosphereColor = new Color(0.55f, 0.7f, 0.9f);
        [Range(0.0f, 0.1f)] public float atmosphereDensity = 0.005f;

        [Header("Cel Shading")]
        [Range(1, 10)] public int celSteps = 3;
        [Range(0.0f, 1.0f)] public float shadowBrightness = 0.2f;

        [Header("Edge Blur")]
        public bool enableEdgeBlur = true;
        [Range(0.01f, 0.5f)] public float edgeWidthPercent = 0.1f;
        [Range(0.1f, 1.0f)] public float edgeRenderScale = 0.5f;
        public Shader edgeBlendShader;

        [Header("Jitter (enable only with TAA)")]
        public bool enableJitter = false;

        [Header("LOD Settings")]
        [Range(1.0f, 200.0f)] public float lodBias = 1.0f;

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
