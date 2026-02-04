using UnityEngine;

namespace VoxelEngine.Core.Data
{
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
}
