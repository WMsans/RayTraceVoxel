using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelGodRaysFeature : ScriptableRendererFeature
    {
        public VoxelGodRaysSettings settings = new VoxelGodRaysSettings();

        private VoxelGodRaysRenderPass _pass;
        private Material _godRayMaterial;

        public override void Create()
        {
            _pass = new VoxelGodRaysRenderPass();

            if (settings.godRayShader != null)
                _godRayMaterial = new Material(settings.godRayShader);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_godRayMaterial == null) return;

            _pass.Setup(settings, _godRayMaterial);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_godRayMaterial);
        }

        private sealed class VoxelGodRaysRenderPass : ScriptableRenderPass
        {
            private VoxelGodRaysSettings _settings;
            private Material _godRayMaterial;
            private readonly GodRaysPass _godRaysPass = new GodRaysPass();

            public VoxelGodRaysRenderPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
            }

            public void Setup(VoxelGodRaysSettings settings, Material material)
            {
                _settings = settings;
                _godRayMaterial = material;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!frameData.Contains<VoxelFrameData>()) return;
                var voxelData = frameData.Get<VoxelFrameData>();
                var cameraData = frameData.Get<UniversalCameraData>();

                _godRaysPass.Record(renderGraph, cameraData, _settings, _godRayMaterial, voxelData.Depth, voxelData.Color, voxelData.MainLightPosition, voxelData.ScaledWidth, voxelData.ScaledHeight);
            }
        }
    }
}
