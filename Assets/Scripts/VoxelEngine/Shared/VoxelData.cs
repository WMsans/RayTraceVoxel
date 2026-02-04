using UnityEngine;
using System.Runtime.InteropServices;

namespace VoxelEngine.Core.Data
{
    public enum SDFObjectType { Sphere = 0, Cube = 1 }
    public enum SDFOperation { Union = 0, Subtract = 1, Intersect = 2, SmoothUnion = 3 }

    [StructLayout(LayoutKind.Sequential)]
    public struct SVONode
    {
        public uint topology; 
        public uint lodColor;
        public uint packedInfo; // Packed: [Payload 16] [Material 8] [Flags 8]
        
        // --- Updated Constants ---
        public const int BRICK_SIZE = 4;        // Logical size (World space coverage relative to scale)
        public const int BRICK_PADDING = 1;     // Padding on each side
        public const int BRICK_STORAGE_SIZE = 6; // 4 + 1 + 1
        public const int BRICK_VOXEL_COUNT = 216; // 6 * 6 * 6
        
        public const int PAGE_SIZE = 2048;
    }

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

    /// <summary>
    /// Represents a dynamic SDF object in the scene.
    /// Aligned to 16 bytes for HLSL StructuredBuffer compatibility.
    /// </summary>
    [System.Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct SDFObject
    {
        public Vector3 position;
        public float pad0; 
        
        public Quaternion rotation; 
        
        public Vector3 scale;
        public float pad1; 
        
        public Vector3 boundsMin; 
        public float pad2;
        
        public Vector3 boundsMax; 
        public float pad3;
        
        public int type;      // 0=Sphere, 1=Cube
        public int operation; // 0=Union, 1=Subtract, 2=Intersect, 3=Smooth
        public float blendFactor;
        public int materialId;
        
        public int padUnused; // Was textureIndex
        public Vector3 padding; 
    }

    /// <summary>
    /// A node in the Linear Bounding Volume Hierarchy.
    /// Used to cull SDF objects against terrain bricks efficiently.
    /// Size: 32 bytes (16-byte aligned)
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LBVHNode
    {
        public Vector3 boundsMin;
        /// <summary>
        /// Index of the left child. 
        /// If negative, this node is a Leaf, and the index is ~LeftChild (bitwise not) pointing to the object index.
        /// </summary>
        public int leftChild; 
        
        public Vector3 boundsMax;
        /// <summary>
        /// Index of the right child. Unused if Leaf.
        /// </summary>
        public int rightChild;
    }
}