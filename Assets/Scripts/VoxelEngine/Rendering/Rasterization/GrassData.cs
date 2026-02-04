using UnityEngine;

namespace VoxelEngine.Core.Data
{
    /// <summary>
    /// GPU-compatible struct for a single grass instance.
    /// Output.
    /// </summary>
    public struct GrassInstance
    {
        public Vector3 position;
        public float rotation;   // Random Y-rotation (radians)
        public uint packedData;  // [Color Variation 16] [Height Scale 8] [Type 8]
    }
}