using System.IO;

namespace VoxelEngine.Core.Serialization
{
    public static class VoxelFileFormat
    {
        public const string MAGIC = "VXVOL";
        public const int VERSION = 1;

        public struct Header
        {
            public string Magic;
            public int Version;
            public int Resolution;
            public int NodeCount;
            public int PayloadCount;
            public int BrickFloatCount; // Number of floats in the brick buffer
            
            // Note: BrickMaterialBuffer size is calculated from BrickFloatCount / 64 * 64 (it's 1:1 with voxels usually?)
            // Wait, BrickBuffer is floats. BrickMaterialBuffer is uints. They are per-voxel.
            // SVOBufferManager:
            // BrickBuffer size: _maxBricks * 64 * sizeof(float)
            // BrickMaterialBuffer size: _maxBricks * 64 * sizeof(uint)
            // So BrickFloatCount directly maps to number of materials.
            
            public void Write(BinaryWriter writer)
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(Resolution);
                writer.Write(NodeCount);
                writer.Write(PayloadCount);
                writer.Write(BrickFloatCount);
            }

            public static Header Read(BinaryReader reader)
            {
                return new Header
                {
                    Magic = reader.ReadString(),
                    Version = reader.ReadInt32(),
                    Resolution = reader.ReadInt32(),
                    NodeCount = reader.ReadInt32(),
                    PayloadCount = reader.ReadInt32(),
                    BrickFloatCount = reader.ReadInt32()
                };
            }
        }
    }
}
