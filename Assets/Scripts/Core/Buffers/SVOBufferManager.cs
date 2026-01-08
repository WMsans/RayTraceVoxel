using UnityEngine;
using System.Runtime.InteropServices;
using VoxelEngine.Core.Data;

namespace VoxelEngine.Core.Buffers
{
    public class SVOBufferManager : System.IDisposable
    {
        public GraphicsBuffer NodeBuffer { get; private set; }
        public GraphicsBuffer PayloadBuffer { get; private set; }
        public GraphicsBuffer BrickBuffer { get; private set; }
        public GraphicsBuffer BrickMaterialBuffer { get; private set; }
        public GraphicsBuffer CounterBuffer { get; private set; }

        private int _maxNodes;
        private int _maxBricks;

        public SVOBufferManager(int maxNodes, int maxBricks)
        {
            _maxNodes = maxNodes;
            _maxBricks = maxBricks;
            Initialize();
        }

        private void Initialize()
        {
            NodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _maxNodes, Marshal.SizeOf<SVONode>());
            PayloadBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _maxNodes, Marshal.SizeOf<VoxelPayload>());
            
            int brickVoxels = SVONode.BRICK_VOXEL_COUNT; // 64
            
            BrickBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _maxBricks * brickVoxels, sizeof(float));
            BrickMaterialBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _maxBricks * brickVoxels, sizeof(uint));
            
            CounterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, sizeof(uint));
            ResetCounters();
        }

        public void ResetCounters()
        {
            if (CounterBuffer != null)
            {
                // [0] = Node Count, [1] = Payload Count, [2] = Brick Float Index
                CounterBuffer.SetData(new uint[] { 0, 0, 0 });
            }
        }

        public void Dispose()
        {
            NodeBuffer?.Release();
            PayloadBuffer?.Release();
            BrickBuffer?.Release();
            BrickMaterialBuffer?.Release();
            CounterBuffer?.Release();
        }
    }
}