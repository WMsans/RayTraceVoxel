using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    internal sealed class CompositePass
    {
        public void Record(RenderGraph renderGraph, VoxelCompositeSettings settings, Material compositeMaterial, TextureHandle source, TextureHandle depthSource, TextureHandle normalSource, TextureHandle compositeOutput, TextureHandle activeDepthTexture, bool useFXAA, Vector4 mainLightDirection, Vector4 mainLightColor)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.CompositePassData>("Composite & Upscale", out var compData))
            {
                compData.source = source;
                compData.depthSource = depthSource;
                compData.normalSource = normalSource;
                compData.material = compositeMaterial;
                compData.useFSR = (settings.upscalingMode == VoxelCompositeSettings.UpscalingMode.SpatialFSR);
                compData.sharpness = settings.sharpness;

                compData.enableOutline = settings.enableOutline;
                compData.outlineColor = settings.outlineColor;
                compData.outlineThickness = settings.outlineThickness;
                compData.outlineStrength = settings.outlineStrength;
                compData.outlineShadowStrength = settings.outlineShadowStrength;
                compData.outlineColor = settings.outlineColor;
                compData.mainLightColor = mainLightColor;
                compData.mainLightDirection = mainLightDirection;

                compData.normalColor = settings.normalHighlightColor;
                compData.normalStrength = settings.normalHighlightStrength;
                compData.normalThreshold = settings.normalThreshold;
                compData.normalFadeDistance = settings.normalFadeDistance;

                builder.UseTexture(compData.source, AccessFlags.Read);
                builder.UseTexture(compData.depthSource, AccessFlags.Read);
                builder.UseTexture(compData.normalSource, AccessFlags.Read);

                builder.SetRenderAttachment(compositeOutput, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(activeDepthTexture, AccessFlags.Write);

                builder.SetRenderFunc((PassDataClasses.CompositePassData cData, RasterGraphContext context) =>
                {
                    if (useFXAA) { context.cmd.ClearRenderTarget(false, true, Color.clear); }

                    cData.material.SetTexture(ShaderParamIDs._VoxelDepthTextureParams, cData.depthSource);
                    cData.material.SetTexture(ShaderParamIDs._VoxelNormalTextureParams, cData.normalSource);
                    cData.material.SetFloat(ShaderParamIDs._SharpnessParams, cData.sharpness);

                    if (cData.useFSR) cData.material.EnableKeyword("_UPSCALING_FSR");
                    else cData.material.DisableKeyword("_UPSCALING_FSR");

                    if (cData.enableOutline)
                    {
                        cData.material.EnableKeyword("_OUTLINE_ON");

                        cData.material.SetColor(ShaderParamIDs._MainLightColorParams, cData.mainLightColor);
                        cData.material.SetVector(ShaderParamIDs._MainLightDirectionID, cData.mainLightDirection);

                        cData.material.SetColor(ShaderParamIDs._OutlineColorParams, cData.outlineColor);
                        cData.material.SetVector(ShaderParamIDs._OutlineParamsID, new Vector4(cData.outlineThickness, cData.outlineStrength, 0, 0));
                        cData.material.SetFloat(ShaderParamIDs._OutlineShadowStrengthID, cData.outlineShadowStrength);

                        cData.material.SetColor(ShaderParamIDs._NormalOutlineColorParams, cData.normalColor);
                        cData.material.SetVector(ShaderParamIDs._NormalOutlineParamsID, new Vector4(cData.normalThreshold, cData.normalStrength, cData.normalFadeDistance, 0));
                    }
                    else
                    {
                        cData.material.DisableKeyword("_OUTLINE_ON");
                    }

                    Blitter.BlitTexture(context.cmd, cData.source, new Vector4(1, 1, 0, 0), cData.material, 0);
                });
            }
        }
    }
}
