using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

using VoxelEngine.Core.Streaming;

namespace VoxelEngine.Core.Rendering
{
    public class VoxelRaytracerFeature : ScriptableRendererFeature
    {
        public VoxelRaytracerSettings settings = new VoxelRaytracerSettings();
        private VoxelRaytracerOrchestratorPass _pass;
        private Material _compositeMaterial;
        private Material _fxaaMaterial;
        private Material _taaMaterial;
        private Material _godRayMaterial;

        public static Vector2 MousePosition;
        public static GraphicsBuffer RaycastHitBuffer;

        public override void Create()
        {
            _pass = new VoxelRaytracerOrchestratorPass(settings);

            if (settings.compositeShader != null)
                _compositeMaterial = new Material(settings.compositeShader);
            else
                _compositeMaterial = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/Universal Render Pipeline/Blit"));

            if (settings.fxaaShader != null)
                _fxaaMaterial = new Material(settings.fxaaShader);

            if (settings.taaShader != null)
                _taaMaterial = new Material(settings.taaShader);

            if (settings.godRayShader != null)
                _godRayMaterial = new Material(settings.godRayShader);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (settings.raytraceShader == null) return;
            if (VoxelVolumePool.Instance == null) return;

            _pass.UpdateSettings(settings);
            _pass.Setup(_compositeMaterial, _fxaaMaterial, _taaMaterial, _godRayMaterial);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_compositeMaterial);
            CoreUtils.Destroy(_fxaaMaterial);
            CoreUtils.Destroy(_taaMaterial);
            CoreUtils.Destroy(_godRayMaterial);
            _pass?.Dispose();
        }
    }
}
