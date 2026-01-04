using UnityEngine;

// Corresponds to the User's "Node Struct (uint2)"
// We use the 'y' component to point to the Payload (data) if this is a leaf.
public struct SVONode
{
    // x: [ChildrenMask (8 bits) | ChildPointer (24 bits)]
    // y: [PayloadPointer (32 bits)] - Points to the start of the data in the Payload Buffer
    public uint topology; 
    public uint payloadIndex;

    // Helper to decode/encode in C# if needed
    public static uint EncodeTopology(uint mask, uint childPtr)
    {
        return ((mask & 0xFF) << 24) | (childPtr & 0xFFFFFF);
    }
}

// Corresponds to "Voxel Payload Struct"
public struct VoxelPayload
{
    public float sdfValue; // Signed Distance
    public uint materialId;
}
