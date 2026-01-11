using UnityEngine;

namespace VoxelEngine.Core.Interfaces
{
    public interface IVoxelStorage
    {
        GraphicsBuffer NodeBuffer { get; }
        GraphicsBuffer PayloadBuffer { get; }
        GraphicsBuffer BrickBuffer { get; }
        GraphicsBuffer BrickMaterialBuffer { get; }
        GraphicsBuffer BrickNormalBuffer { get; }
        GraphicsBuffer CounterBuffer { get; }
        
        int Resolution { get; }
        int MaxNodes { get; }
        int MaxBricks { get; }
        bool IsReady { get; }
    }
}