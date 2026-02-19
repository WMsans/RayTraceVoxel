using UnityEngine;

namespace VoxelEngine.Core.Rendering
{
    public static class ShaderParamIDs
    {
        public static readonly int Result = Shader.PropertyToID("_Result");
        public static readonly int ResultDepth = Shader.PropertyToID("_ResultDepth");
        public static readonly int ResultNormals = Shader.PropertyToID("_ResultNormals");
        public static readonly int CameraToWorld = Shader.PropertyToID("_CameraToWorld");
        public static readonly int CameraInverseProjection = Shader.PropertyToID("_CameraInverseProjection");
        public static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
        public static readonly int VoxelDepthTexture = Shader.PropertyToID("_VoxelDepthTexture");
        public static readonly int VoxelNormalTexture = Shader.PropertyToID("_VoxelNormalTexture");
        public static readonly int ZBufferParams = Shader.PropertyToID("_ZBufferParams");
        public static readonly int RaytraceParams = Shader.PropertyToID("_RaytraceParams");
        public static readonly int CelShadeParams = Shader.PropertyToID("_CelShadeParams");
        public static readonly int AtmosphereParams = Shader.PropertyToID("_AtmosphereParams");
        public static readonly int AtmosphereColor = Shader.PropertyToID("_AtmosphereColor");

        public static readonly int GlobalNodeBuffer = Shader.PropertyToID("_GlobalNodeBuffer");
        public static readonly int GlobalPayloadBuffer = Shader.PropertyToID("_GlobalPayloadBuffer");
        public static readonly int GlobalBrickDataBuffer = Shader.PropertyToID("_GlobalBrickDataBuffer");
        public static readonly int PageTableBuffer = Shader.PropertyToID("_PageTableBuffer");
        public static readonly int TLASGridBuffer = Shader.PropertyToID("_TLASGridBuffer");
        public static readonly int TLASChunkIndexBuffer = Shader.PropertyToID("_TLASChunkIndexBuffer");
        public static readonly int TLASBoundsMin = Shader.PropertyToID("_TLASBoundsMin");
        public static readonly int TLASBoundsMax = Shader.PropertyToID("_TLASBoundsMax");
        public static readonly int TLASResolution = Shader.PropertyToID("_TLASResolution");
        public static readonly int ChunkBuffer = Shader.PropertyToID("_ChunkBuffer");
        public static readonly int ChunkCount = Shader.PropertyToID("_ChunkCount");
        public static readonly int VoxelMaterialBuffer = Shader.PropertyToID("_VoxelMaterialBuffer");
        public static readonly int AlbedoTextureArray = Shader.PropertyToID("_AlbedoTextureArray");
        public static readonly int NormalTextureArray = Shader.PropertyToID("_NormalTextureArray");
        public static readonly int MaskTextureArray = Shader.PropertyToID("_MaskTextureArray");
        public static readonly int MainLightPosition = Shader.PropertyToID("_MainLightPosition");
        public static readonly int MainLightColor = Shader.PropertyToID("_MainLightColor");
        public static readonly int RaycastBuffer = Shader.PropertyToID("_RaycastBuffer");
        public static readonly int FrameCount = Shader.PropertyToID("_FrameCount");
        public static readonly int BlueNoiseTexture = Shader.PropertyToID("_BlueNoiseTexture");
        public static readonly int MousePosition = Shader.PropertyToID("_MousePosition");
        public static readonly int MaxIterations = Shader.PropertyToID("_MaxIterations");
        public static readonly int MaxMarchSteps = Shader.PropertyToID("_MaxMarchSteps");
        public static readonly int CameraViewProjection = Shader.PropertyToID("_CameraViewProjection");
        public static readonly int PrevViewProjMatrix = Shader.PropertyToID("_PrevViewProjMatrix");
        public static readonly int MotionVectorTexture = Shader.PropertyToID("_MotionVectorTexture");
        public static readonly int SourceTex = Shader.PropertyToID("_SourceTex");
        public static readonly int Sharpness = Shader.PropertyToID("_Sharpness");
        public static readonly int HistoryTex = Shader.PropertyToID("_HistoryTex");
        public static readonly int Blend = Shader.PropertyToID("_Blend");

        public static readonly int OutlineParams = Shader.PropertyToID("_OutlineParams");
        public static readonly int OutlineColor = Shader.PropertyToID("_OutlineColor");
        public static readonly int NormalOutlineParams = Shader.PropertyToID("_NormalOutlineParams");
        public static readonly int NormalOutlineColor = Shader.PropertyToID("_NormalOutlineColor");
        public static readonly int MainLightDirection = Shader.PropertyToID("_MainLightDirection");
        public static readonly int OutlineShadowStrength = Shader.PropertyToID("_OutlineShadowStrength");

        public static readonly int VoxelDepthCopy = Shader.PropertyToID("_VoxelDepthCopy");

        public static readonly int LightPosition = Shader.PropertyToID("_LightPosition");
        public static readonly int SunThreshold = Shader.PropertyToID("_SunThreshold");
        public static readonly int Density = Shader.PropertyToID("_Density");
        public static readonly int Decay = Shader.PropertyToID("_Decay");
        public static readonly int Weight = Shader.PropertyToID("_Weight");
        public static readonly int Exposure = Shader.PropertyToID("_Exposure");
        public static readonly int Samples = Shader.PropertyToID("_Samples");
        public static readonly int LightColor = Shader.PropertyToID("_LightColor");

        public static readonly int DebugViewNormals = Shader.PropertyToID("_DebugViewNormals");
        public static readonly int DebugViewBricks = Shader.PropertyToID("_DebugViewBricks");
    }
}