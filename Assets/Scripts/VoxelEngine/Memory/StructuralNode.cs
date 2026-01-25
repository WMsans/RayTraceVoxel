using System.Collections.Generic;
using UnityEngine;

namespace VoxelEngine.Core.Structural
{
    // Simple enum for the 6 cardinal directions
    public enum StructuralFace { Left = 0, Right = 1, Down = 2, Up = 3, Back = 4, Forward = 5, None = 6 }

    public class StructuralNode
    {
        public Vector3Int Coordinate; // Global Brick Coordinate
        public bool IsAnchored;       // Connected to bedrock/ground?
        
        // Bitmask representing internal paths. 
        // If bit (FromFace * 6 + ToFace) is set, you can travel from Start -> End through this brick.
        // Size: 6x6 = 36 bits. 
        public ulong InternalConnectivityMask;

        // The neighboring nodes in the global graph (Edges)
        // Indexed by StructuralFace (0-5)
        public StructuralNode[] Neighbors = new StructuralNode[6];

        public StructuralNode(Vector3Int coord)
        {
            Coordinate = coord;
            // Default anchor rule: Y=0 is bedrock.
            IsAnchored = coord.y <= 0; 
        }

        // Helper to check if we can traverse from entryFace to exitFace
        public bool CanTraverse(StructuralFace entry, StructuralFace exit)
        {
            if (entry == StructuralFace.None || exit == StructuralFace.None) return false;
            int bitIndex = (int)entry * 6 + (int)exit;
            return (InternalConnectivityMask & (1UL << bitIndex)) != 0;
        }

        public void SetConnectivity(StructuralFace entry, StructuralFace exit, bool connected)
        {
            int bitIndex = (int)entry * 6 + (int)exit;
            if (connected) InternalConnectivityMask |= (1UL << bitIndex);
            else InternalConnectivityMask &= ~(1UL << bitIndex);
        }
    }
}
