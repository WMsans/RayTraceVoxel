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