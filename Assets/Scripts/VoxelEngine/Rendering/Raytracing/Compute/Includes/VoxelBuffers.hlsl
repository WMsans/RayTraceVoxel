// -----------------------------------------------------------------------------
// Global Buffers and Uniforms
// -----------------------------------------------------------------------------
StructuredBuffer<SVONode> _GlobalNodeBuffer;
StructuredBuffer<VoxelPayload> _GlobalPayloadBuffer;
StructuredBuffer<uint> _GlobalBrickDataBuffer;
StructuredBuffer<uint> _PageTableBuffer;
StructuredBuffer<ChunkDef> _ChunkBuffer;
int _ChunkCount;

StructuredBuffer<TLASCell> _TLASGridBuffer;
StructuredBuffer<int> _TLASChunkIndexBuffer;

float3 _TLASBoundsMin;
float3 _TLASBoundsMax;
int _TLASResolution;

StructuredBuffer<VoxelTypeGPU> _VoxelMaterialBuffer;

Texture2D<float> _CameraDepthTexture;
Texture2D<float4> _BlueNoiseTexture;
Texture2D<float4> _SourceTex;
SamplerState sampler_CameraDepthTexture 
{ 
    Filter = MIN_MAG_MIP_POINT; 
    AddressU = Clamp; 
    AddressV = Clamp; 
};

Texture2DArray _AlbedoTextureArray;
Texture2DArray _NormalTextureArray;
Texture2DArray _MaskTextureArray;

SamplerState sampler_LinearRepeat 
{ 
    Filter = MIN_MAG_MIP_LINEAR; 
    AddressU = Wrap; 
    AddressV = Wrap;
};

// [SHADOWS] Unity Shadow Assets
// Bound by VoxelRaytracerFeature via cmd.SetComputeTextureParam
Texture2D<float> _MainLightShadowmapTexture;

// Globals set by URP MainLightShadowCasterPass
float4x4 _MainLightWorldToShadow[4];
float4 _CascadeShadowSplitSpheres0;
float4 _CascadeShadowSplitSpheres1;
float4 _CascadeShadowSplitSpheres2;
float4 _CascadeShadowSplitSpheres3;

RWTexture2D<float4> _Result;
RWTexture2D<float> _ResultDepth;
RWTexture2D<float4> _ResultNormals;
RWStructuredBuffer<float4> _RaycastBuffer;
RWTexture2D<float2> _MotionVectorTexture;

float4x4 _PrevViewProjMatrix;
float4x4 _CameraToWorld;
float4x4 _CameraInverseProjection;
float4x4 _CameraViewProjection;
float4 _ZBufferParams;
float4 _MainLightPosition;
float4 _MainLightColor;
float4 _RaytraceParams;
float4 _CelShadeParams; // x: steps, y: minBrightness/darkness, z: unused

// [ATMOSPHERE]
float4 _AtmosphereParams; // x: density
float4 _AtmosphereColor;

float _DebugNormalDelta;
float _DebugViewNormals;
float _DebugViewBricks;
float4 _MousePosition;
int _MaxIterations;
int _MaxMarchSteps;
int _BounceCount;
