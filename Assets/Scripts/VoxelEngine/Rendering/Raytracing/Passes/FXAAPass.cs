using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    internal sealed class FXAAPass
    {
        public void Record(RenderGraph renderGraph, bool useFXAA, Material fxaaMaterial, TextureHandle source, TextureHandle activeColorTexture)
        {
            if (!useFXAA)
            {
                return;
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.FXAAPassData>("FXAA Pass", out var fxaaData))
            {
                fxaaData.source = source;
                fxaaData.material = fxaaMaterial;
                builder.UseTexture(fxaaData.source, AccessFlags.Read);
                builder.SetRenderAttachment(activeColorTexture, 0, AccessFlags.Write);
                builder.SetRenderFunc((PassDataClasses.FXAAPassData fData, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, fData.source, new Vector4(1, 1, 0, 0), fData.material, 0);
                });
            }
        }
    }
}
