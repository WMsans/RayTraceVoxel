using System.Collections.Generic;
using UnityEngine;
using VoxelEngine.Core.Interfaces;

namespace VoxelEngine.Core
{
    public static class VoxelVolumeRegistry
    {
        private static readonly List<VoxelVolume> _volumes = new List<VoxelVolume>();
        private static readonly List<IVoxelStorage> _localVolumes = new List<IVoxelStorage>();

        public static IReadOnlyList<VoxelVolume> Volumes => _volumes;
        public static IReadOnlyList<IVoxelStorage> LocalVolumes => _localVolumes;

        public static void Register(VoxelVolume volume)
        {
            if (!_volumes.Contains(volume))
            {
                _volumes.Add(volume);
            }
        }

        public static void Unregister(VoxelVolume volume)
        {
            _volumes.Remove(volume);
        }

        public static void RegisterVolumeLocal(IVoxelStorage volume)
        {
            if (!_localVolumes.Contains(volume))
            {
                _localVolumes.Add(volume);
            }
        }

        public static void UnregisterVolumeLocal(IVoxelStorage volume)
        {
            _localVolumes.Remove(volume);
        }
    }
}
