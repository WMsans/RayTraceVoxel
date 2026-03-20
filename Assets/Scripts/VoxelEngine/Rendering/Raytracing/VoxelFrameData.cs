using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelFrameData : ContextItem
    {
        public TextureHandle Color;
        public TextureHandle Depth;
        public TextureHandle Normals;
        public TextureHandle MotionVectors;
        public int ScaledWidth;
        public int ScaledHeight;
        public float RenderScale;
        public Vector2 Jitter;
        public Matrix4x4 ViewProj;
        public Matrix4x4 PrevViewProj;
        public Vector4 MainLightPosition;
        public Vector4 MainLightColor;
        public float PixelSpread;

        public override void Reset()
        {
            Color = Depth = Normals = MotionVectors = TextureHandle.nullHandle;
            ScaledWidth = ScaledHeight = 0;
            RenderScale = 1f;
            Jitter = Vector2.zero;
            ViewProj = PrevViewProj = Matrix4x4.identity;
            MainLightPosition = new Vector4(0, 1, 0, 0);
            MainLightColor = Vector4.one;
            PixelSpread = 0f;
        }
    }
}
