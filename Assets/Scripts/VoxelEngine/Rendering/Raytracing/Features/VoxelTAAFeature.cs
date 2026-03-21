using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelTAAFeature : ScriptableRendererFeature
    {
        public VoxelTAASettings settings = new VoxelTAASettings();

        private VoxelTAARenderPass _pass;
        private Material _taaMaterial;

        public override void Create()
        {
            _pass = new VoxelTAARenderPass();

            if (settings.taaShader != null)
                _taaMaterial = new Material(settings.taaShader);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_taaMaterial == null) return;

            _pass.Setup(settings, _taaMaterial);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_taaMaterial);
            _pass?.Dispose();
        }

        private sealed class VoxelTAARenderPass : ScriptableRenderPass
        {
            private VoxelTAASettings _settings;
            private Material _taaMaterial;
            private readonly TAAPass _taaPass = new TAAPass();
            private readonly CameraHistoryManager _cameraHistoryManager = new CameraHistoryManager();

            public VoxelTAARenderPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
            }

            public void Setup(VoxelTAASettings settings, Material material)
            {
                _settings = settings;
                _taaMaterial = material;
            }

            public void Dispose()
            {
                _cameraHistoryManager.Release();
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!frameData.Contains<VoxelFrameData>()) return;
                var voxelData = frameData.Get<VoxelFrameData>();

                _cameraHistoryManager.GetHistoryTextures(
                    frameData.Get<UniversalCameraData>().camera,
                    renderGraph,
                    voxelData.ScaledWidth,
                    voxelData.ScaledHeight,
                    out TextureHandle historyRead,
                    out TextureHandle historyWrite);

                voxelData.Color = _taaPass.Record(renderGraph, true, _taaMaterial, voxelData.Color, historyRead, historyWrite, voxelData.MotionVectors, _settings.taaBlend);
            }
        }
    }
}
