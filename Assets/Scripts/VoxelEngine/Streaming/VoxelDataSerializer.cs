using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine.Core.Data;
using Unity.Collections;

namespace VoxelEngine.Core.Serialization
{
    public static class VoxelDataSerializer
    {
        public static void Save(VoxelVolume volume, string filePath, Action<bool> onComplete)
        {
            if (volume == null || !volume.IsReady)
            {
                onComplete?.Invoke(false);
                return;
            }

            var counterBuffer = volume.CounterBuffer;
            AsyncGPUReadback.Request(counterBuffer, (request) =>
            {
                if (request.hasError) { onComplete?.Invoke(false); return; }

                using (var data = request.GetData<uint>())
                {
                    int nodeCount = (int)data[0];
                    int payloadCount = (int)data[1];
                    int brickVoxelCount = (int)data[2]; // Total voxels

                    ReadDataBuffers(volume, filePath, nodeCount, payloadCount, brickVoxelCount, onComplete);
                }
            });
        }

        private static void ReadDataBuffers(VoxelVolume volume, string filePath, int nodeCount, int payloadCount, int brickVoxelCount, Action<bool> onComplete)
        {
            // Now only 3 requests (Nodes, Payloads, BrickData)
            int pendingRequests = 3; 
            bool failed = false;

            NativeArray<SVONode> nodes = default;
            NativeArray<VoxelPayload> payloads = default;
            NativeArray<uint> brickData = default; // Packed Data

            void CheckComplete()
            {
                if (failed) return;
                if (pendingRequests == 0)
                {
                    try
                    {
                        WriteFile(filePath, volume.Resolution, nodeCount, payloadCount, brickVoxelCount, nodes, payloads, brickData);
                        onComplete?.Invoke(true);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Failed to write file: {e.Message}");
                        onComplete?.Invoke(false);
                    }
                    finally
                    {
                        if (nodes.IsCreated) nodes.Dispose();
                        if (payloads.IsCreated) payloads.Dispose();
                        if (brickData.IsCreated) brickData.Dispose();
                    }
                }
            }

            // 1. Nodes
            if (nodeCount > 0)
            {
                AsyncGPUReadback.Request(volume.NodeBuffer, nodeCount * System.Runtime.InteropServices.Marshal.SizeOf<SVONode>(), 0, (req) =>
                {
                    if (req.hasError) failed = true;
                    else {
                        var temp = new NativeArray<SVONode>(req.GetData<SVONode>().Length, Allocator.Persistent);
                        temp.CopyFrom(req.GetData<SVONode>());
                        nodes = temp;
                    }
                    pendingRequests--; CheckComplete();
                });
            }
            else { nodes = new NativeArray<SVONode>(0, Allocator.Persistent); pendingRequests--; CheckComplete(); }

            // 2. Payloads
            if (payloadCount > 0)
            {
                AsyncGPUReadback.Request(volume.PayloadBuffer, payloadCount * System.Runtime.InteropServices.Marshal.SizeOf<VoxelPayload>(), 0, (req) =>
                {
                    if (req.hasError) failed = true;
                    else {
                        var temp = new NativeArray<VoxelPayload>(req.GetData<VoxelPayload>().Length, Allocator.Persistent);
                        temp.CopyFrom(req.GetData<VoxelPayload>());
                        payloads = temp;
                    }
                    pendingRequests--; CheckComplete();
                });
            }
            else { payloads = new NativeArray<VoxelPayload>(0, Allocator.Persistent); pendingRequests--; CheckComplete(); }

            // 3. Brick Data (Packed UInts)
            if (brickVoxelCount > 0)
            {
                AsyncGPUReadback.Request(volume.BrickDataBuffer, brickVoxelCount * sizeof(uint), 0, (req) =>
                {
                    if (req.hasError) failed = true;
                    else {
                        var temp = new NativeArray<uint>(req.GetData<uint>().Length, Allocator.Persistent);
                        temp.CopyFrom(req.GetData<uint>());
                        brickData = temp;
                    }
                    pendingRequests--; CheckComplete();
                });
            }
            else { brickData = new NativeArray<uint>(0, Allocator.Persistent); pendingRequests--; CheckComplete(); }
        }

        private static void WriteFile(string filePath, int resolution, int nodeCount, int payloadCount, int brickVoxelCount,
            NativeArray<SVONode> nodes, NativeArray<VoxelPayload> payloads, NativeArray<uint> brickData)
        {
            using (var fs = new FileStream(filePath, FileMode.Create))
            using (var writer = new BinaryWriter(fs))
            {
                var header = new VoxelFileFormat.Header
                {
                    Magic = VoxelFileFormat.MAGIC,
                    Version = VoxelFileFormat.VERSION,
                    Resolution = resolution,
                    NodeCount = nodeCount,
                    PayloadCount = payloadCount,
                    BrickDataCount = brickVoxelCount
                };
                header.Write(writer);
                WriteCompressedBlock(writer, nodes);
                WriteCompressedBlock(writer, payloads);
                WriteCompressedBlock(writer, brickData);
            }
        }

        private static void WriteCompressedBlock<T>(BinaryWriter writer, NativeArray<T> data) where T : struct
        {
            int size = data.Length * System.Runtime.InteropServices.Marshal.SizeOf<T>();
            byte[] bytes = new byte[size];
            NativeArray<byte>.Copy(data.Reinterpret<byte>(System.Runtime.InteropServices.Marshal.SizeOf<T>()), bytes, size);

            using (var ms = new MemoryStream())
            {
                using (var gzip = new GZipStream(ms, CompressionMode.Compress))
                {
                    gzip.Write(bytes, 0, bytes.Length);
                }
                byte[] compressed = ms.ToArray();
                writer.Write(compressed.Length);
                writer.Write(compressed);
            }
        }

        public static void Load(VoxelVolume volume, string filePath)
        {
            if (!File.Exists(filePath)) return;

            using (var fs = new FileStream(filePath, FileMode.Open))
            using (var reader = new BinaryReader(fs))
            {
                var header = VoxelFileFormat.Header.Read(reader);
                if (header.Magic != VoxelFileFormat.MAGIC) return;

                byte[] nodeBytes = ReadCompressedBlock(reader);
                var nodes = BytesToNativeArray<SVONode>(nodeBytes, header.NodeCount);
                volume.NodeBuffer.SetData(nodes);
                nodes.Dispose();

                byte[] payloadBytes = ReadCompressedBlock(reader);
                var payloads = BytesToNativeArray<VoxelPayload>(payloadBytes, header.PayloadCount);
                volume.PayloadBuffer.SetData(payloads);
                payloads.Dispose();

                byte[] brickBytes = ReadCompressedBlock(reader);
                var brickData = BytesToNativeArray<uint>(brickBytes, header.BrickDataCount);
                volume.BrickDataBuffer.SetData(brickData);
                brickData.Dispose();

                // [0]=Nodes, [1]=Payloads, [2]=BrickVoxels
                volume.CounterBuffer.SetData(new uint[] { (uint)header.NodeCount, (uint)header.PayloadCount, (uint)header.BrickDataCount });
            }
        }

        private static byte[] ReadCompressedBlock(BinaryReader reader)
        {
            int size = reader.ReadInt32();
            byte[] compressed = reader.ReadBytes(size);
            using (var ms = new MemoryStream(compressed))
            using (var gzip = new GZipStream(ms, CompressionMode.Decompress))
            using (var outMs = new MemoryStream())
            {
                gzip.CopyTo(outMs);
                return outMs.ToArray();
            }
        }

        private static NativeArray<T> BytesToNativeArray<T>(byte[] bytes, int count) where T : struct
        {
            var array = new NativeArray<T>(count, Allocator.Temp);
            NativeArray<byte> byteView = array.Reinterpret<byte>(System.Runtime.InteropServices.Marshal.SizeOf<T>());
            NativeArray<byte>.Copy(bytes, byteView, bytes.Length);
            return array;
        }
    }
}