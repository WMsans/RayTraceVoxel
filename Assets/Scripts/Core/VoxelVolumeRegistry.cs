using System.Collections.Generic;
using UnityEngine;

namespace VoxelEngine.Core
{
    public static class VoxelVolumeRegistry
    {
        private static readonly List<VoxelVolume> _volumes = new List<VoxelVolume>();

        public static IReadOnlyList<VoxelVolume> Volumes => _volumes;

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
    }
}
