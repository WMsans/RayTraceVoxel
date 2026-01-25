using UnityEngine;
using System.Runtime.InteropServices;
using VoxelEngine.Core.Data;

namespace VoxelEngine.Core.Buffers
{
    public class SVOBufferManager
    {
        public GraphicsBuffer NodeBuffer { get; private set; }
        public GraphicsBuffer PayloadBuffer { get; private set; }
        public GraphicsBuffer PageTableBuffer { get; private set; } // New
        
        // Merged Buffer: [Packed uint] per voxel
        public GraphicsBuffer BrickDataBuffer { get; private set; }
        
        public GraphicsBuffer CounterBuffer { get; private set; }

        public int NodeOffset { get; private set; }
        public int PayloadOffset { get; private set; }
        public int BrickDataOffset { get; private set; }
        public int PageTableOffset { get; private set; } // New

        public SVOBufferManager(
            GraphicsBuffer nodes, int nodeOffset,
            GraphicsBuffer payloads, int payloadOffset,
            GraphicsBuffer brickData, int brickDataOffset,
            GraphicsBuffer pageTable, int pageTableOffset) 
        {
            NodeBuffer = nodes;
            NodeOffset = nodeOffset;
            
            PayloadBuffer = payloads;
            PayloadOffset = payloadOffset;
            
            BrickDataBuffer = brickData;
            BrickDataOffset = brickDataOffset;

            PageTableBuffer = pageTable;
            PageTableOffset = pageTableOffset;

            CounterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, sizeof(uint));
            ResetCounters();
        }

        public void ResetCounters()
        {
            if (CounterBuffer != null)
            {
                // [0] = Node Count, [1] = Payload Count, [2] = Brick VOXEL Index (Start of next brick)
                CounterBuffer.SetData(new uint[] { 0, 0, 0 });
            }
        }

        public void Dispose()
        {
            CounterBuffer?.Release();
        }
    }
}