using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    internal sealed class GodRaysPass
    {
        public void Record(RenderGraph renderGraph, UniversalCameraData cameraData, VoxelGodRaysSettings settings, Material godRayMaterial, TextureHandle lowResDepth, TextureHandle lowResResult, Vector4 mainLightPosition, int scaledWidth, int scaledHeight)
        {
            if (godRayMaterial == null)
            {
                return;
            }

            Vector3 vectorToSun = new Vector3(mainLightPosition.x, mainLightPosition.y, mainLightPosition.z).normalized;
            if (vectorToSun == Vector3.zero) vectorToSun = Vector3.up;

            float sunHeight = Mathf.Clamp01(Vector3.Dot(vectorToSun, Vector3.up));
            float dynamicSunThreshold = Mathf.SmoothStep(settings.dawnSunThreshold, settings.noonSunThreshold, sunHeight);

            Vector3 cameraPos = cameraData.camera.transform.position;
            Vector3 sunWorldPos = cameraPos + vectorToSun * 10000.0f;
            Vector3 viewportPos = cameraData.camera.WorldToViewportPoint(sunWorldPos);
            float isVisible = (viewportPos.z > 0) ? 1.0f : 0.0f;

            TextureDesc occDesc = new TextureDesc(scaledWidth, scaledHeight)
            {
                colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
                name = "GodRays_Occluders"
            };
            TextureHandle occluderTex = renderGraph.CreateTexture(occDesc);

            TextureDesc blurDesc = new TextureDesc(scaledWidth, scaledHeight)
            {
                colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
                name = "GodRays_Blur"
            };
            TextureHandle blurTex = renderGraph.CreateTexture(blurDesc);

            using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.GodRayPassData>("God Rays Occluders", out var grData))
            {
                builder.AllowGlobalStateModification(true);

                grData.sourceDepth = lowResDepth;
                grData.occluderTex = occluderTex;
                grData.material = godRayMaterial;
                grData.lightPosScreen = new Vector3(viewportPos.x, viewportPos.y, isVisible);
                grData.lightColor = settings.lightSourceColor;
                grData.sunThreshold = dynamicSunThreshold;

                builder.UseTexture(grData.sourceDepth, AccessFlags.Read);
                builder.SetRenderAttachment(grData.occluderTex, 0, AccessFlags.Write);

                builder.SetRenderFunc((PassDataClasses.GodRayPassData d, RasterGraphContext ctx) =>
                {
                    ctx.cmd.SetGlobalTexture(ShaderParamIDs._VoxelDepthTextureParams, d.sourceDepth);
                    d.material.SetVector(ShaderParamIDs._LightPositionParams, d.lightPosScreen);
                    d.material.SetColor(ShaderParamIDs._LightColorGodRayParams, d.lightColor);
                    d.material.SetFloat(ShaderParamIDs._SunThresholdParams, d.sunThreshold);
                    Blitter.BlitTexture(ctx.cmd, d.sourceDepth, new Vector4(1, 1, 0, 0), d.material, 0);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.GodRayPassData>("God Rays Blur", out var grData))
            {
                grData.occluderTex = occluderTex;
                grData.blurTex = blurTex;
                grData.material = godRayMaterial;
                grData.lightPosScreen = new Vector3(viewportPos.x, viewportPos.y, isVisible);
                grData.density = settings.rayDensity;
                grData.decay = settings.rayDecay;
                grData.weight = settings.rayWeight;
                grData.exposure = settings.rayExposure;
                grData.samples = settings.raySamples;

                builder.UseTexture(grData.occluderTex, AccessFlags.Read);
                builder.SetRenderAttachment(grData.blurTex, 0, AccessFlags.Write);

                builder.SetRenderFunc((PassDataClasses.GodRayPassData d, RasterGraphContext ctx) =>
                {
                    d.material.SetVector(ShaderParamIDs._LightPositionParams, d.lightPosScreen);
                    d.material.SetFloat(ShaderParamIDs._DensityParams, d.density);
                    d.material.SetFloat(ShaderParamIDs._DecayParams, d.decay);
                    d.material.SetFloat(ShaderParamIDs._WeightParams, d.weight);
                    d.material.SetFloat(ShaderParamIDs._ExposureParams, d.exposure);
                    d.material.SetInt(ShaderParamIDs._SamplesParams, d.samples);
                    Blitter.BlitTexture(ctx.cmd, d.occluderTex, new Vector4(1, 1, 0, 0), d.material, 1);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.GodRayPassData>("God Rays Blend", out var grData))
            {
                grData.blurTex = blurTex;
                grData.destTex = lowResResult;
                grData.material = godRayMaterial;

                builder.UseTexture(grData.blurTex, AccessFlags.Read);
                builder.SetRenderAttachment(grData.destTex, 0, AccessFlags.ReadWrite);

                builder.SetRenderFunc((PassDataClasses.GodRayPassData d, RasterGraphContext ctx) =>
                {
                    Blitter.BlitTexture(ctx.cmd, d.blurTex, new Vector4(1, 1, 0, 0), d.material, 2);
                });
            }
        }
    }
}
