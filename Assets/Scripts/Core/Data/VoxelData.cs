using UnityEngine;
using System.Runtime.InteropServices;

namespace VoxelEngine.Core.Data
{
    public enum SDFObjectType { Sphere = 0, Cube = 1, VoxelGrid = 2 } 
    public enum SDFOperation { Union = 0, Subtract = 1, Intersect = 2, SmoothUnion = 3 }

    [StructLayout(LayoutKind.Sequential)]
    public struct SVONode
    {
        public uint topology; 
        public uint lodColor;
        public uint packedInfo; 
        
        public const int BRICK_SIZE = 4;        
        public const int BRICK_PADDING = 1;     
        public const int BRICK_STORAGE_SIZE = 6; 
        public const int BRICK_VOXEL_COUNT = 216; 
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
        
        public int type;      // 0=Sphere, 1=Cube, 2=VoxelGrid
        public int operation; // 0=Union, 1=Subtract, 2=Intersect, 3=Smooth
        public float blendFactor;
        public int materialId;
        
        public int textureIndex; // Points to the slice in the Texture3D Atlas
        public Vector3 padding; 
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LBVHNode
    {
        public Vector3 boundsMin;
        public int leftChild; 
        public Vector3 boundsMax;
        public int rightChild;
    }
}