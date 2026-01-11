using UnityEngine;
using System.Runtime.InteropServices;

namespace VoxelEngine.Core.Data
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SVONode
    {
        public uint topology; 
        public uint payloadIndex;
        public uint lodColor;
        public uint lodMaterial;
        
        // --- Updated Constants ---
        public const int BRICK_SIZE = 4;        // Logical size (World space coverage relative to scale)
        public const int BRICK_PADDING = 1;     // Padding on each side
        public const int BRICK_STORAGE_SIZE = 6; // 4 + 1 + 1
        public const int BRICK_VOXEL_COUNT = 216; // 6 * 6 * 6
    }

    // ... [Rest of VoxelData.cs remains unchanged]
    [StructLayout(LayoutKind.Sequential)]
    public struct VoxelPayload
    {
        public uint brickDataIndex; 
    }

    [System.Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct VoxelTypeGPU
    {
        public uint sideAlbedoIndex;
        public uint sideNormalIndex;
        public uint sideMaskIndex;
        
        public uint topAlbedoIndex;
        public uint topNormalIndex;
        public uint topMaskIndex;
        
        public float sideMetallic;
        public float topMetallic;
        
        public uint renderType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VoxelLight
    {
        public Vector4 position;
        public Vector4 color;
        public Vector4 attenuation;
    }
}