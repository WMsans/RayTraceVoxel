using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    internal sealed class TAAPass
    {
        public TextureHandle Record(RenderGraph renderGraph, bool useTAA, Material taaMaterial, TextureHandle source, TextureHandle historyRead, TextureHandle historyWrite, TextureHandle motionVector, float blend)
        {
            if (!useTAA)
            {
                return source;
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassDataClasses.TAAPassData>("Voxel TAA", out var taaData))
            {
                taaData.source = source;
                taaData.history = historyRead;
                taaData.motion = motionVector;
                taaData.destination = historyWrite;
                taaData.material = taaMaterial;
                taaData.blend = blend;
                builder.UseTexture(taaData.source, AccessFlags.Read);
                builder.UseTexture(taaData.history, AccessFlags.Read);
                builder.UseTexture(taaData.motion, AccessFlags.Read);
                builder.SetRenderAttachment(taaData.destination, 0, AccessFlags.Write);
                builder.SetRenderFunc((PassDataClasses.TAAPassData tData, RasterGraphContext context) =>
                {
                    tData.material.SetTexture(ShaderParamIDs._HistoryTexParams, tData.history);
                    tData.material.SetTexture(ShaderParamIDs._MotionVectorTextureParams, tData.motion);
                    tData.material.SetFloat(ShaderParamIDs._BlendParams, tData.blend);
                    Blitter.BlitTexture(context.cmd, tData.source, new Vector4(1, 1, 0, 0), tData.material, 0);
                });
            }

            return historyWrite;
        }
    }
}
