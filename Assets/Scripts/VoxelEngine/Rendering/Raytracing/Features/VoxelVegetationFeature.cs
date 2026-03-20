using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelVegetationFeature : ScriptableRendererFeature
    {
        private VoxelVegetationRenderPass _pass;
        private Material _copyMaterial;

        public override void Create()
        {
            _pass = new VoxelVegetationRenderPass();
            _copyMaterial = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/Universal Render Pipeline/Blit"));
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            _pass.SetCopyMaterial(_copyMaterial);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_copyMaterial);
        }

        private sealed class VoxelVegetationRenderPass : ScriptableRenderPass
        {
            private readonly VoxelVegetationPass _vegetationPass = new VoxelVegetationPass();
            private Material _copyMaterial;

            public VoxelVegetationRenderPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
            }

            public void SetCopyMaterial(Material mat) => _copyMaterial = mat;

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!frameData.Contains<VoxelFrameData>()) return;
                var voxelData = frameData.Get<VoxelFrameData>();

                _vegetationPass.Record(renderGraph, voxelData.ScaledWidth, voxelData.ScaledHeight, voxelData.Color, voxelData.Depth, voxelData.Normals, _copyMaterial);
            }
        }
    }
}
