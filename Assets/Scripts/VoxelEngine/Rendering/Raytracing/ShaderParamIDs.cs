using UnityEngine;

namespace VoxelEngine.Core.Rendering
{
    public static class ShaderParamIDs
    {
        public static readonly int _ResultParams = Shader.PropertyToID("_Result");
        public static readonly int _ResultDepthParams = Shader.PropertyToID("_ResultDepth");
        public static readonly int _ResultNormalsParams = Shader.PropertyToID("_ResultNormals");
        public static readonly int _CameraToWorldParams = Shader.PropertyToID("_CameraToWorld");
        public static readonly int _CameraInverseProjectionParams = Shader.PropertyToID("_CameraInverseProjection");
        public static readonly int _CameraDepthTextureParams = Shader.PropertyToID("_CameraDepthTexture");
        public static readonly int _VoxelDepthTextureParams = Shader.PropertyToID("_VoxelDepthTexture");
        public static readonly int _VoxelNormalTextureParams = Shader.PropertyToID("_VoxelNormalTexture");
        public static readonly int _ZBufferParamsID = Shader.PropertyToID("_ZBufferParams");
        public static readonly int _RaytraceParams = Shader.PropertyToID("_RaytraceParams");
        public static readonly int _CelShadeParams = Shader.PropertyToID("_CelShadeParams");
        public static readonly int _AtmosphereParams = Shader.PropertyToID("_AtmosphereParams");
        public static readonly int _AtmosphereColor = Shader.PropertyToID("_AtmosphereColor");

        public static readonly int _GlobalNodeBufferParams = Shader.PropertyToID("_GlobalNodeBuffer");
        public static readonly int _GlobalPayloadBufferParams = Shader.PropertyToID("_GlobalPayloadBuffer");
        public static readonly int _GlobalBrickDataBufferParams = Shader.PropertyToID("_GlobalBrickDataBuffer");
        public static readonly int _PageTableBufferParams = Shader.PropertyToID("_PageTableBuffer");
        public static readonly int _TLASGridBufferParams = Shader.PropertyToID("_TLASGridBuffer");
        public static readonly int _TLASChunkIndexBufferParams = Shader.PropertyToID("_TLASChunkIndexBuffer");
        public static readonly int _TLASBoundsMinParams = Shader.PropertyToID("_TLASBoundsMin");
        public static readonly int _TLASBoundsMaxParams = Shader.PropertyToID("_TLASBoundsMax");
        public static readonly int _TLASResolutionParams = Shader.PropertyToID("_TLASResolution");
        public static readonly int _ChunkBufferParams = Shader.PropertyToID("_ChunkBuffer");
        public static readonly int _ChunkCountParams = Shader.PropertyToID("_ChunkCount");
        public static readonly int _VoxelMaterialBufferParams = Shader.PropertyToID("_VoxelMaterialBuffer");
        public static readonly int _AlbedoTextureArrayParams = Shader.PropertyToID("_AlbedoTextureArray");
        public static readonly int _NormalTextureArrayParams = Shader.PropertyToID("_NormalTextureArray");
        public static readonly int _MaskTextureArrayParams = Shader.PropertyToID("_MaskTextureArray");
        public static readonly int _MainLightPositionParams = Shader.PropertyToID("_MainLightPosition");
        public static readonly int _MainLightColorParams = Shader.PropertyToID("_MainLightColor");
        public static readonly int _RaycastBufferParams = Shader.PropertyToID("_RaycastBuffer");
        public static readonly int _FrameCountParams = Shader.PropertyToID("_FrameCount");
        public static readonly int _BlueNoiseTextureParams = Shader.PropertyToID("_BlueNoiseTexture");
        public static readonly int _MousePositionParams = Shader.PropertyToID("_MousePosition");
        public static readonly int _MaxIterationsParams = Shader.PropertyToID("_MaxIterations");
        public static readonly int _MaxMarchStepsParams = Shader.PropertyToID("_MaxMarchSteps");
        public static readonly int _BounceCountParams = Shader.PropertyToID("_BounceCount");
        public static readonly int _CameraViewProjectionParams = Shader.PropertyToID("_CameraViewProjection");
        public static readonly int _PrevViewProjMatrixParams = Shader.PropertyToID("_PrevViewProjMatrix");
        public static readonly int _MotionVectorTextureParams = Shader.PropertyToID("_MotionVectorTexture");
        public static readonly int _SourceTexParams = Shader.PropertyToID("_SourceTex");
        public static readonly int _EdgeSourceParams = Shader.PropertyToID("_EdgeSource");
        public static readonly int _SharpnessParams = Shader.PropertyToID("_Sharpness");
        public static readonly int _HistoryTexParams = Shader.PropertyToID("_HistoryTex");
        public static readonly int _BlendParams = Shader.PropertyToID("_Blend");
        public static readonly int _EdgeWidthParams = Shader.PropertyToID("_EdgeWidth");

        public static readonly int _OutlineParamsID = Shader.PropertyToID("_OutlineParams");
        public static readonly int _OutlineColorParams = Shader.PropertyToID("_OutlineColor");
        public static readonly int _NormalOutlineParamsID = Shader.PropertyToID("_NormalOutlineParams");
        public static readonly int _NormalOutlineColorParams = Shader.PropertyToID("_NormalOutlineColor");
        public static readonly int _MainLightDirectionID = Shader.PropertyToID("_MainLightDirection");
        public static readonly int _OutlineShadowStrengthID = Shader.PropertyToID("_OutlineShadowStrength");

        public static readonly int _VoxelDepthCopyParams = Shader.PropertyToID("_VoxelDepthCopy");
        public static readonly int _VegetationScreenSizeParams = Shader.PropertyToID("_VegetationScreenSize");

        public static readonly int _LightPositionParams = Shader.PropertyToID("_LightPosition");
        public static readonly int _SunThresholdParams = Shader.PropertyToID("_SunThreshold");
        public static readonly int _DensityParams = Shader.PropertyToID("_Density");
        public static readonly int _DecayParams = Shader.PropertyToID("_Decay");
        public static readonly int _WeightParams = Shader.PropertyToID("_Weight");
        public static readonly int _ExposureParams = Shader.PropertyToID("_Exposure");
        public static readonly int _SamplesParams = Shader.PropertyToID("_Samples");
        public static readonly int _LightColorGodRayParams = Shader.PropertyToID("_LightColor");

        public static readonly int _DebugViewNormalsParams = Shader.PropertyToID("_DebugViewNormals");
        public static readonly int _DebugViewBricksParams = Shader.PropertyToID("_DebugViewBricks");
    }
}
