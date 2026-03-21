using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelCompositeFeature : ScriptableRendererFeature
    {
        public VoxelCompositeSettings settings = new VoxelCompositeSettings();

        private VoxelCompositeRenderPass _pass;
        private Material _compositeMaterial;
        private Material _fxaaMaterial;

        public override void Create()
        {
            _pass = new VoxelCompositeRenderPass();

            if (settings.compositeShader != null)
                _compositeMaterial = new Material(settings.compositeShader);
            else
                _compositeMaterial = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/Universal Render Pipeline/Blit"));

            if (settings.fxaaShader != null)
                _fxaaMaterial = new Material(settings.fxaaShader);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            _pass.Setup(settings, _compositeMaterial, _fxaaMaterial);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_compositeMaterial);
            CoreUtils.Destroy(_fxaaMaterial);
        }

        private sealed class VoxelCompositeRenderPass : ScriptableRenderPass
        {
            private VoxelCompositeSettings _settings;
            private Material _compositeMaterial;
            private Material _fxaaMaterial;
            private readonly CompositePass _compositePass = new CompositePass();
            private readonly FXAAPass _fxaaPass = new FXAAPass();

            public VoxelCompositeRenderPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
            }

            public void Setup(VoxelCompositeSettings settings, Material composite, Material fxaa)
            {
                _settings = settings;
                _compositeMaterial = composite;
                _fxaaMaterial = fxaa;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!frameData.Contains<VoxelFrameData>()) return;
                var voxelData = frameData.Get<VoxelFrameData>();
                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var cameraDesc = cameraData.cameraTargetDescriptor;

                bool useFXAA = _settings.enableFXAA && _fxaaMaterial != null;

                TextureHandle compositeOutput;
                if (useFXAA)
                {
                    TextureDesc fullScreenDesc = new TextureDesc(cameraDesc.width, cameraDesc.height)
                    {
                        colorFormat = cameraDesc.graphicsFormat,
                        name = "VoxelComposite_PreFXAA"
                    };
                    compositeOutput = renderGraph.CreateTexture(fullScreenDesc);
                }
                else
                {
                    compositeOutput = resourceData.activeColorTexture;
                }

                _compositePass.Record(renderGraph, _settings, _compositeMaterial, voxelData.Color, voxelData.Depth, voxelData.Normals, compositeOutput, resourceData.activeDepthTexture, useFXAA, voxelData.MainLightPosition, voxelData.MainLightColor);
                _fxaaPass.Record(renderGraph, useFXAA, _fxaaMaterial, compositeOutput, resourceData.activeColorTexture);
            }
        }
    }
}
