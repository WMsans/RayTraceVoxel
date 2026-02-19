using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    public sealed class EdgeBlendPass
    {
        public TextureHandle Record(RenderGraph renderGraph, TextureHandle fullSource, TextureHandle edgeSource, TextureHandle target, Material material, float edgeWidth)
        {
            if (material == null)
                return fullSource;

            material.SetTexture(ShaderParamIDs._EdgeSourceParams, edgeSource);
            material.SetFloat(ShaderParamIDs._EdgeWidthParams, edgeWidth);

            var passName = "VoxelEdgeBlend";
            var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData);
            passData.source = fullSource;
            passData.target = target;
            passData.material = material;

            builder.UseTexture(fullSource);
            builder.UseTexture(edgeSource);
            builder.SetRenderAttachment(target, 0);

            builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
            {
                Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, 0);
            });

            return target;
        }

        private sealed class PassData
        {
            public TextureHandle source;
            public TextureHandle target;
            public Material material;
        }
    }
}
