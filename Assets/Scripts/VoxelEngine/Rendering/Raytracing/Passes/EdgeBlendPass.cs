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

            var passName = "VoxelEdgeBlend";
            
            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData))
            {
                passData.source = fullSource;
                passData.edgeSource = edgeSource;
                passData.edgeWidth = edgeWidth;
                passData.material = material;

                builder.UseTexture(passData.source, AccessFlags.Read);
                builder.UseTexture(passData.edgeSource, AccessFlags.Read);
                builder.SetRenderAttachment(target, 0);

                builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                {
                    data.material.SetTexture(ShaderParamIDs._EdgeSourceParams, data.edgeSource);
                    data.material.SetFloat(ShaderParamIDs._EdgeWidthParams, data.edgeWidth);
                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, 0);
                });
            }

            return target;
        }

        private sealed class PassData
        {
            public TextureHandle source;
            public TextureHandle edgeSource;
            public float edgeWidth;
            public Material material;
        }
    }
}