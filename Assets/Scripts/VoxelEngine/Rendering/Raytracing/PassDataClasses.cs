using UnityEngine;
using UnityEngine.Rendering.RenderGraphModule;

namespace VoxelEngine.Core.Rendering
{
    internal static class PassDataClasses
    {
        internal class PassData
        {
            public ComputeShader computeShader;
            public int kernel;
            public TextureHandle targetColor;
            public TextureHandle targetDepth;
            public TextureHandle targetMotionVector;
            public TextureHandle targetNormals;
            public TextureHandle sourceDepth;
            public TextureHandle sourceColor;
            public Matrix4x4 cameraToWorld;
            public Matrix4x4 cameraInverseProjection;
            public Matrix4x4 viewProj;
            public Matrix4x4 prevViewProj;
            public Vector4 zBufferParams;
            public int width;
            public int height;
            public Vector4 mainLightPosition;
            public Vector4 mainLightColor;
            public Vector4 raytraceParams;
            public GraphicsBuffer nodeBuffer;
            public GraphicsBuffer payloadBuffer;
            public GraphicsBuffer brickDataBuffer;
            public GraphicsBuffer pageTableBuffer;
            public GraphicsBuffer tlasGridBuffer;
            public GraphicsBuffer tlasChunkIndexBuffer;
            public Vector3 tlasBoundsMin;
            public Vector3 tlasBoundsMax;
            public int tlasResolution;
            public GraphicsBuffer chunkBuffer;
            public int chunkCount;
            public GraphicsBuffer materialBuffer;
            public GraphicsBuffer raycastBuffer;
            public TextureHandle albedoArray;
            public TextureHandle normalArray;
            public TextureHandle maskArray;
            public int frameCount;
            public TextureHandle blueNoise;
            public Vector2 mousePosition;
            public int maxIterations;
            public int maxMarchSteps;
            public int bounceCount;
            public float debugNormals;
            public float debugBricks;
            public Vector4 celShadeParams;
            public Vector4 atmosphereParams;
            public Vector4 atmosphereColor;
        }

        internal class CompositePassData
        {
            public TextureHandle source;
            public TextureHandle depthSource;
            public TextureHandle normalSource;
            public Material material;
            public bool useFSR;
            public float sharpness;
            public bool enableOutline;
            public float outlineThickness;
            public float outlineStrength;
            public float outlineShadowStrength;
            public Color outlineColor;
            public Vector4 mainLightColor;
            public Vector4 mainLightDirection;
            public float normalStrength;
            public float normalThreshold;
            public float normalFadeDistance;
            public Color normalColor;
        }

        internal class FXAAPassData
        {
            public TextureHandle source;
            public Material material;
        }

        internal class TAAPassData
        {
            public TextureHandle source;
            public TextureHandle history;
            public TextureHandle motion;
            public TextureHandle destination;
            public Material material;
            public float blend;
        }

        internal class VegetationPassData
        {
            public TextureHandle colorTarget;
            public TextureHandle depthTarget;
            public TextureHandle normalTarget;
            public TextureHandle depthCopy;
            public TextureHandle tempDepthBuffer;
            public Vector4 vegetationScreenSize;
        }

        internal class CopyPassData
        {
            public TextureHandle source;
            public TextureHandle dest;
            public Material material;
        }

        internal class GodRayPassData
        {
            public TextureHandle sourceDepth;
            public TextureHandle occluderTex;
            public TextureHandle blurTex;
            public TextureHandle destTex;
            public Material material;
            public Vector3 lightPosScreen;
            public Color lightColor;
            public float sunThreshold;
            public float density;
            public float decay;
            public float weight;
            public float exposure;
            public int samples;
        }
    }
}
