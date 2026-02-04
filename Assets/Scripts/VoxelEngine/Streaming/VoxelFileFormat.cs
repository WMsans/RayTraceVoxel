using System.IO;

namespace VoxelEngine.Core.Serialization
{
    public static class VoxelFileFormat
    {
        public const string MAGIC = "VXVOL";
        public const int VERSION = 2; // Bumped Version

        public struct Header
        {
            public string Magic;
            public int Version;
            public int Resolution;
            public int NodeCount;
            public int PayloadCount;
            public int BrickDataCount; // Changed from BrickFloatCount
            
            public void Write(BinaryWriter writer)
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(Resolution);
                writer.Write(NodeCount);
                writer.Write(PayloadCount);
                writer.Write(BrickDataCount);
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
                    BrickDataCount = reader.ReadInt32()
                };
            }
        }
    }
}