using UnityEngine;

public struct SVONode
{
    // x: [ChildrenMask (8 bits) | ChildPointer (24 bits)]
    // y: [PayloadPointer (32 bits)]
    public uint topology; 
    public uint payloadIndex;
    
    // Constants for logic
    public const int BRICK_SIZE = 4; // 4x4x4 = 64 voxels per brick
    public const int BRICK_VOXEL_COUNT = 64;
}

// Lightweight header stored in the PayloadBuffer
public struct VoxelPayload
{
    public uint brickDataIndex; // Pointer to start of float array in BrickBuffer
    public uint materialId;
}

/// <summary>
/// GPU representation of a Voxel Definition.
/// Stride should match the HLSL struct.
/// </summary>
[System.Serializable]
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct VoxelTypeGPU
{
    public uint sideAlbedoIndex;
    public uint sideNormalIndex;
    public uint sideMaskIndex; // R=Metallic(Unused if float), G=AO, B=Smoothness
    
    public uint topAlbedoIndex;
    public uint topNormalIndex;
    public uint topMaskIndex;
    
    public float sideMetallic;
    public float topMetallic;
    
    public uint renderType;
    
    // Padding to ensure 4-byte alignment or specific stride if needed. 
    // Currently: 6 uints + 2 floats + 1 uint = 36 bytes.
    // If strict 16-byte alignment is needed (e.g. for constant buffers), more padding is required.
    // For StructuredBuffer, 36 bytes is usually fine as long as HLSL matches.
}

public enum BrushShape
{
    Sphere = 0,
    Cube = 1,
    Plane = 2
}

public enum BrushOp
{
    Add = 0,
    Subtract = 1,
    Paint = 2
}

[System.Serializable]
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct VoxelBrush
{
    public Vector3 position;
    public Vector3 bounds; // Size for Cube/Plane
    public float radius;   // For Sphere
    public int materialId;
    public int shape;      // BrushShape
    public int op;         // BrushOp
}