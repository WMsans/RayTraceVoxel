using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace VoxelEngine.Core.Rendering
{
    internal sealed class CameraHistoryManager
    {
        private class CameraHistory
        {
            public RTHandle[] historyTextures = new RTHandle[2];
            public int currentIndex;
        }

        private readonly Dictionary<Camera, CameraHistory> _cameraHistory = new Dictionary<Camera, CameraHistory>();
        private readonly Dictionary<Camera, Matrix4x4> _prevMatrices = new Dictionary<Camera, Matrix4x4>();

        public bool TryGetPrevViewProj(Camera camera, Matrix4x4 currentViewProj, out Matrix4x4 prevViewProj)
        {
            if (!_prevMatrices.TryGetValue(camera, out prevViewProj))
            {
                prevViewProj = currentViewProj;
                _prevMatrices[camera] = currentViewProj;
                return false;
            }

            _prevMatrices[camera] = currentViewProj;
            return true;
        }

        public void GetHistoryTextures(Camera camera, RenderGraph renderGraph, int width, int height, out TextureHandle read, out TextureHandle write)
        {
            read = TextureHandle.nullHandle;
            write = TextureHandle.nullHandle;

            if (!_cameraHistory.TryGetValue(camera, out var history))
            {
                history = new CameraHistory();
                _cameraHistory[camera] = history;
            }

            for (int i = 0; i < 2; i++)
            {
                if (history.historyTextures[i] == null || history.historyTextures[i].rt.width != width || history.historyTextures[i].rt.height != height)
                {
                    history.historyTextures[i]?.Release();
                    history.historyTextures[i] = RTHandles.Alloc(width, height, colorFormat: UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, name: $"VoxelHistory_{i}");
                }
            }

            read = renderGraph.ImportTexture(history.historyTextures[history.currentIndex]);
            write = renderGraph.ImportTexture(history.historyTextures[(history.currentIndex + 1) % 2]);
            history.currentIndex = (history.currentIndex + 1) % 2;
        }

        public void Release()
        {
            foreach (var kvp in _cameraHistory)
            {
                kvp.Value.historyTextures[0]?.Release();
                kvp.Value.historyTextures[1]?.Release();
            }

            _cameraHistory.Clear();
            _prevMatrices.Clear();
        }
    }
}
