using UnityEngine;
using System.Runtime.InteropServices;
using VoxelEngine.Core.Data;

namespace VoxelEngine.Core.Buffers
{
    /// <summary>
    /// Now acts as a lightweight handle/view into the Monolithic Global Buffers.
    /// Owned and managed by VoxelVolumePool.
    /// </summary>
    public class SVOBufferManager
    {
        // References to Global Buffers
        public GraphicsBuffer NodeBuffer { get; private set; }
        public GraphicsBuffer PayloadBuffer { get; private set; }
        public GraphicsBuffer BrickBuffer { get; private set; }
        public GraphicsBuffer BrickMaterialBuffer { get; private set; }
        
        // Local Counter Buffer (Still per-chunk for generation safety)
        public GraphicsBuffer CounterBuffer { get; private set; }

        // Offsets into Global Buffers
        public int NodeOffset { get; private set; }
        public int PayloadOffset { get; private set; }
        public int BrickOffset { get; private set; }

        public SVOBufferManager(
            GraphicsBuffer nodes, int nodeOffset,
            GraphicsBuffer payloads, int payloadOffset,
            GraphicsBuffer bricks, GraphicsBuffer materials, int brickOffset)
        {
            NodeBuffer = nodes;
            NodeOffset = nodeOffset;
            
            PayloadBuffer = payloads;
            PayloadOffset = payloadOffset;
            
            BrickBuffer = bricks;
            BrickMaterialBuffer = materials;
            BrickOffset = brickOffset;

            // Allocate a small local counter for generation logic
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
            // We DO NOT release the global buffers here.
            CounterBuffer?.Release();
        }
    }
}