using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    internal sealed class VoxelVegetationPass
    {
        public void Record(RenderGraph renderGraph, int scaledWidth, int scaledHeight, TextureHandle lowResResult, TextureHandle lowResDepth, TextureHandle lowResNormals, Material copyMaterial)
        {
            bool hasGrass = VoxelGrassRenderer.ActiveRenderers.Count > 0;
            bool hasLeaves = VoxelLeafRenderer.ActiveLeafRenderers.Count > 0;

            if (!hasGrass && !hasLeaves)
            {
                return;
            }

            Vector4 vegScreenSize = new Vector4(scaledWidth, scaledHeight, 1.0f / scaledWidth, 1.0f / scaledHeight);

            TextureDesc copyDesc = new TextureDesc(scaledWidth, scaledHeight)
            {
                colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat,
                name = "VoxelDepthCopy"
            };
            TextureHandle voxelDepthCopy = renderGraph.CreateTexture(copyDesc);

            using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.CopyPassData>("Copy Voxel Depth", out var copyData))
            {
                copyData.source = lowResDepth;
                copyData.dest = voxelDepthCopy;
                copyData.material = copyMaterial;
                builder.UseTexture(copyData.source, AccessFlags.Read);
                builder.SetRenderAttachment(copyData.dest, 0, AccessFlags.Write);
                builder.SetRenderFunc((PassDataClasses.CopyPassData cData, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, cData.source, new Vector4(1, 1, 0, 0), cData.material, 0);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.VegetationPassData>("Voxel Vegetation", out var vegData))
            {
                builder.AllowGlobalStateModification(true);

                vegData.colorTarget = lowResResult;
                vegData.depthTarget = lowResDepth;
                vegData.normalTarget = lowResNormals;
                vegData.depthCopy = voxelDepthCopy;

                TextureDesc tempDepthDesc = new TextureDesc(scaledWidth, scaledHeight)
                {
                    depthBufferBits = DepthBits.Depth32,
                    name = "VegetationTempZ"
                };
                vegData.tempDepthBuffer = renderGraph.CreateTexture(tempDepthDesc);

                builder.SetRenderAttachment(vegData.colorTarget, 0, AccessFlags.Write);
                builder.SetRenderAttachment(vegData.depthTarget, 1, AccessFlags.Write);
                builder.SetRenderAttachment(vegData.normalTarget, 2, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(vegData.tempDepthBuffer, AccessFlags.Write);
                builder.UseTexture(vegData.depthCopy, AccessFlags.Read);

                vegData.vegetationScreenSize = vegScreenSize;

                builder.SetRenderFunc((PassDataClasses.VegetationPassData vData, RasterGraphContext context) =>
                {
                    context.cmd.ClearRenderTarget(true, false, Color.black);
                    context.cmd.SetGlobalTexture(ShaderParamIDs._VoxelDepthCopyParams, vData.depthCopy);
                    context.cmd.SetGlobalVector(ShaderParamIDs._VegetationScreenSizeParams, vData.vegetationScreenSize);
                    if (hasGrass)
                    {
                        foreach (var renderer in VoxelGrassRenderer.ActiveRenderers) renderer.Draw(context.cmd);
                    }
                    if (hasLeaves)
                    {
                        foreach (var renderer in VoxelLeafRenderer.ActiveLeafRenderers) renderer.Draw(context.cmd);
                    }
                });
            }
        }
    }
}
