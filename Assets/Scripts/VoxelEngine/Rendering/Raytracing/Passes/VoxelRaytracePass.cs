using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

using VoxelEngine.Core.Data;
using VoxelEngine.Core.Streaming;

namespace VoxelEngine.Core.Rendering
{
    internal sealed class VoxelRaytracePass
    {
        internal struct RaytraceOutput
        {
            public TextureHandle LowResResult;
            public TextureHandle LowResDepth;
            public TextureHandle LowResNormals;
            public TextureHandle MotionVectors;

            public RaytraceOutput(TextureHandle lowResResult, TextureHandle lowResDepth, TextureHandle lowResNormals, TextureHandle motionVectors)
            {
                LowResResult = lowResResult;
                LowResDepth = lowResDepth;
                LowResNormals = lowResNormals;
                MotionVectors = motionVectors;
            }
        }

        private VoxelRaytraceSettings _settings;
        private ComputeShader _shader;

        private RTHandle _albedoHandle;
        private RTHandle _normalHandle;
        private RTHandle _maskHandle;
        private RTHandle _blueNoiseHandle;

        public VoxelRaytracePass(VoxelRaytraceSettings settings)
        {
            UpdateSettings(settings);
        }

        public void UpdateSettings(VoxelRaytraceSettings newSettings)
        {
            _settings = newSettings;
            _shader = newSettings.raytraceShader;
        }

        public void Dispose()
        {
            _albedoHandle?.Release();
            _normalHandle?.Release();
            _maskHandle?.Release();
            _blueNoiseHandle?.Release();

            if (VoxelRaytraceFeature.RaycastHitBuffer != null)
            {
                VoxelRaytraceFeature.RaycastHitBuffer.Release();
                VoxelRaytraceFeature.RaycastHitBuffer = null;
            }
        }

        public RaytraceOutput Record(RenderGraph renderGraph, UniversalCameraData cameraData, UniversalResourceData resourceData, Vector4 mainLightPosition, Vector4 mainLightColor, Matrix4x4 viewProj, Matrix4x4 prevViewProj, int scaledWidth, int scaledHeight, float currentScale, float finalSpread, Vector2 jitter, int iterations, int marchSteps)
        {
            TextureDesc colorDesc = new TextureDesc(scaledWidth, scaledHeight)
            {
                colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat,
                enableRandomWrite = true,
                name = "VoxelRaytraceResult_LowRes"
            };
            TextureHandle lowResResult = renderGraph.CreateTexture(colorDesc);
            TextureDesc depthDesc = new TextureDesc(scaledWidth, scaledHeight)
            {
                colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat,
                enableRandomWrite = true,
                name = "VoxelRaytraceDepth_LowRes"
            };
            TextureHandle lowResDepth = renderGraph.CreateTexture(depthDesc);
            TextureDesc normDesc = new TextureDesc(scaledWidth, scaledHeight)
            {
                colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat,
                enableRandomWrite = true,
                name = "VoxelRaytraceNormals"
            };
            TextureHandle lowResNormals = renderGraph.CreateTexture(normDesc);
            TextureDesc mvDesc = new TextureDesc(scaledWidth, scaledHeight)
            {
                colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16_SFloat,
                enableRandomWrite = true,
                name = "VoxelMotionVectors"
            };
            TextureHandle motionVectorTex = renderGraph.CreateTexture(mvDesc);

            CheckTextureHandle(ref _albedoHandle, VoxelDefinitionManager.Instance.albedoTextureArray);
            CheckTextureHandle(ref _normalHandle, VoxelDefinitionManager.Instance.normalTextureArray);
            CheckTextureHandle(ref _maskHandle, VoxelDefinitionManager.Instance.maskTextureArray);
            CheckTextureHandle(ref _blueNoiseHandle, _settings.blueNoiseTexture);

            using (var builder = renderGraph.AddComputePass("Voxel Raytracer", out PassDataClasses.PassData data))
            {
                data.computeShader = _shader;
                data.kernel = _shader.FindKernel("CSMain");
                if (VoxelRaytraceFeature.RaycastHitBuffer == null || !VoxelRaytraceFeature.RaycastHitBuffer.IsValid())
                    VoxelRaytraceFeature.RaycastHitBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, 16);
                data.raycastBuffer = VoxelRaytraceFeature.RaycastHitBuffer;
                var pool = VoxelVolumePool.Instance;
                data.nodeBuffer = pool.GlobalNodeBuffer;
                data.payloadBuffer = pool.GlobalPayloadBuffer;
                data.brickDataBuffer = pool.GlobalBrickDataBuffer;
                data.pageTableBuffer = pool.GlobalPageTableBuffer;
                data.chunkBuffer = pool.ChunkBuffer;
                data.chunkCount = pool.VisibleChunkCount;
                data.tlasGridBuffer = pool.TLASGridBuffer;
                data.tlasChunkIndexBuffer = pool.TLASChunkIndexBuffer;
                data.tlasBoundsMin = pool.TLASBoundsMin;
                data.tlasBoundsMax = pool.TLASBoundsMax;
                data.tlasResolution = pool.TLASResolution;
                data.frameCount = Time.frameCount;
                data.materialBuffer = VoxelDefinitionManager.Instance.VoxelMaterialBuffer;
                if (_albedoHandle != null) data.albedoArray = renderGraph.ImportTexture(_albedoHandle);
                if (_normalHandle != null) data.normalArray = renderGraph.ImportTexture(_normalHandle);
                if (_maskHandle != null) data.maskArray = renderGraph.ImportTexture(_maskHandle);
                if (_blueNoiseHandle != null) data.blueNoise = renderGraph.ImportTexture(_blueNoiseHandle);
                data.width = scaledWidth;
                data.height = scaledHeight;
                data.cameraToWorld = cameraData.camera.cameraToWorldMatrix;
                data.cameraInverseProjection = cameraData.camera.projectionMatrix.inverse;
                data.viewProj = viewProj;
                data.prevViewProj = prevViewProj;
                data.zBufferParams = Shader.GetGlobalVector(ShaderParamIDs._ZBufferParamsID);
                data.sourceDepth = resourceData.cameraDepthTexture;
                data.sourceColor = resourceData.activeColorTexture;
                data.targetColor = lowResResult;
                data.targetDepth = lowResDepth;
                data.targetNormals = lowResNormals;
                data.targetMotionVector = motionVectorTex;
                data.mainLightPosition = mainLightPosition;
                data.mainLightColor = mainLightColor;
                data.raytraceParams = new Vector4(finalSpread, jitter.x, jitter.y, _settings.textureScale);
                data.mousePosition = VoxelRaytraceFeature.MousePosition * currentScale;
                data.maxIterations = iterations;
                data.maxMarchSteps = marchSteps;
                data.bounceCount = _settings.bounceCount;
                data.debugNormals = (_settings.debugMode == VoxelRaytraceSettings.DebugMode.Normals) ? 1.0f : 0.0f;
                data.debugBricks = (_settings.debugMode == VoxelRaytraceSettings.DebugMode.Bricks) ? 1.0f : 0.0f;
                data.celShadeParams = new Vector4((float)_settings.celSteps, _settings.shadowBrightness, 0, 0);
                data.atmosphereParams = new Vector4(_settings.enableAtmosphere ? _settings.atmosphereDensity : 0.0f, 0, 0, 0);
                data.atmosphereColor = _settings.atmosphereColor;

                builder.UseTexture(data.targetColor, AccessFlags.Write);
                builder.UseTexture(data.targetDepth, AccessFlags.Write);
                builder.UseTexture(data.targetNormals, AccessFlags.Write);
                builder.UseTexture(data.targetMotionVector, AccessFlags.Write);
                builder.UseTexture(data.sourceDepth, AccessFlags.Read);
                builder.UseTexture(data.sourceColor, AccessFlags.Read);
                if (data.albedoArray.IsValid()) builder.UseTexture(data.albedoArray, AccessFlags.Read);
                if (data.normalArray.IsValid()) builder.UseTexture(data.normalArray, AccessFlags.Read);
                if (data.maskArray.IsValid()) builder.UseTexture(data.maskArray, AccessFlags.Read);
                if (data.blueNoise.IsValid()) builder.UseTexture(data.blueNoise, AccessFlags.Read);

                builder.SetRenderFunc((PassDataClasses.PassData pd, ComputeGraphContext ctx) =>
                {
                    var cs = pd.computeShader;
                    var ker = pd.kernel;
                    var cmd = ctx.cmd;
                    cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._GlobalNodeBufferParams, pd.nodeBuffer);
                    cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._GlobalPayloadBufferParams, pd.payloadBuffer);
                    cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._GlobalBrickDataBufferParams, pd.brickDataBuffer);
                    cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._PageTableBufferParams, pd.pageTableBuffer);
                    cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._ChunkBufferParams, pd.chunkBuffer);
                    cmd.SetComputeIntParam(cs, ShaderParamIDs._ChunkCountParams, pd.chunkCount);
                    if (pd.tlasGridBuffer != null) cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._TLASGridBufferParams, pd.tlasGridBuffer);
                    if (pd.tlasChunkIndexBuffer != null) cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._TLASChunkIndexBufferParams, pd.tlasChunkIndexBuffer);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._TLASBoundsMinParams, pd.tlasBoundsMin);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._TLASBoundsMaxParams, pd.tlasBoundsMax);
                    cmd.SetComputeIntParam(cs, ShaderParamIDs._TLASResolutionParams, pd.tlasResolution);
                    cmd.SetComputeIntParam(cs, ShaderParamIDs._FrameCountParams, pd.frameCount);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._MousePositionParams, pd.mousePosition);
                    cmd.SetComputeIntParam(cs, ShaderParamIDs._MaxIterationsParams, pd.maxIterations);
                    cmd.SetComputeIntParam(cs, ShaderParamIDs._MaxMarchStepsParams, pd.maxMarchSteps);
                    cmd.SetComputeIntParam(cs, ShaderParamIDs._BounceCountParams, pd.bounceCount);
                    if (pd.blueNoise.IsValid()) cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._BlueNoiseTextureParams, pd.blueNoise);
                    if (pd.materialBuffer != null) cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._VoxelMaterialBufferParams, pd.materialBuffer);
                    if (pd.albedoArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._AlbedoTextureArrayParams, pd.albedoArray);
                    if (pd.normalArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._NormalTextureArrayParams, pd.normalArray);
                    if (pd.maskArray.IsValid()) cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._MaskTextureArrayParams, pd.maskArray);
                    cmd.SetComputeMatrixParam(cs, ShaderParamIDs._CameraToWorldParams, pd.cameraToWorld);
                    cmd.SetComputeMatrixParam(cs, ShaderParamIDs._CameraInverseProjectionParams, pd.cameraInverseProjection);
                    cmd.SetComputeMatrixParam(cs, ShaderParamIDs._CameraViewProjectionParams, pd.viewProj);
                    cmd.SetComputeMatrixParam(cs, ShaderParamIDs._PrevViewProjMatrixParams, pd.prevViewProj);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._ZBufferParamsID, pd.zBufferParams);
                    cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._CameraDepthTextureParams, pd.sourceDepth);
                    cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._SourceTexParams, pd.sourceColor);
                    cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._ResultParams, pd.targetColor);
                    cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._ResultDepthParams, pd.targetDepth);
                    cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._ResultNormalsParams, pd.targetNormals);
                    cmd.SetComputeTextureParam(cs, ker, ShaderParamIDs._MotionVectorTextureParams, pd.targetMotionVector);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._MainLightPositionParams, pd.mainLightPosition);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._MainLightColorParams, pd.mainLightColor);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._RaytraceParams, pd.raytraceParams);
                    cmd.SetComputeBufferParam(cs, ker, ShaderParamIDs._RaycastBufferParams, pd.raycastBuffer);
                    cmd.SetComputeFloatParam(cs, ShaderParamIDs._DebugViewNormalsParams, pd.debugNormals);
                    cmd.SetComputeFloatParam(cs, ShaderParamIDs._DebugViewBricksParams, pd.debugBricks);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._CelShadeParams, pd.celShadeParams);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._AtmosphereParams, pd.atmosphereParams);
                    cmd.SetComputeVectorParam(cs, ShaderParamIDs._AtmosphereColor, pd.atmosphereColor);

                    int groupsX = Mathf.CeilToInt(pd.width / 8.0f);
                    int groupsY = Mathf.CeilToInt(pd.height / 8.0f);
                    cmd.DispatchCompute(cs, ker, groupsX, groupsY, 1);
                });
            }

            return new RaytraceOutput(lowResResult, lowResDepth, lowResNormals, motionVectorTex);
        }

        private void CheckTextureHandle(ref RTHandle handle, Texture texture)
        {
            if (texture == null) return;
            if (handle == null || handle.rt != texture)
            {
                handle?.Release();
                handle = RTHandles.Alloc(texture);
            }
        }
    }
}
