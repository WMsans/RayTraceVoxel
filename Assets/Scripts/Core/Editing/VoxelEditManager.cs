using System.Collections.Generic;
using UnityEngine;
using VoxelEngine.Core.Data;

namespace VoxelEngine.Core.Editing
{
    public class VoxelEditManager : MonoSingleton<VoxelEditManager>
    {
        [Header("Global Configuration")]
        [Tooltip("The world-space size of a single voxel (matches the scale of Leaf nodes).")]
        public float voxelSize = 1.0f;
    }
}