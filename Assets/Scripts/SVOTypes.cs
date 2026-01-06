using UnityEngine;

public struct SVONode
{
    // x: [ChildrenMask (8 bits) | ChildPointer (24 bits)]
    // y: [PayloadPointer (32 bits)]
    public uint topology; 
    public uint payloadIndex;
    
    public const int BRICK_SIZE = 4;
    public const int BRICK_VOXEL_COUNT = 64;
}

// Updated Payload: Removed materialId, as it is now per-voxel
public struct VoxelPayload
{
    public uint brickDataIndex; // Pointer to start of arrays in BrickBuffer & BrickMaterialBuffer
}

[System.Serializable]
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
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

public enum BrushShape { Sphere = 0, Cube = 1, Plane = 2 }
public enum BrushOp { Add = 0, Subtract = 1, Paint = 2 }

[System.Serializable]
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct VoxelBrush
{
    public Vector3 position;
    public Vector3 bounds;
    public float radius;
    public int materialId;
    public int shape;
    public int op;
}