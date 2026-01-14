using UnityEngine;

namespace VoxelEngine.Core.Interfaces
{
    public interface IVoxelStorage
    {
        GraphicsBuffer NodeBuffer { get; }
        GraphicsBuffer PayloadBuffer { get; }
        
        // Merged Buffer
        GraphicsBuffer BrickDataBuffer { get; } 
        
        GraphicsBuffer CounterBuffer { get; }
        
        int Resolution { get; }
        int MaxNodes { get; }
        int MaxBricks { get; }
        bool IsReady { get; }
    }
}