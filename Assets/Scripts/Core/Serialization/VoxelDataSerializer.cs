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
                Debug.LogError("VoxelVolume is not ready to save.");
                onComplete?.Invoke(false);
                return;
            }

            var counterBuffer = volume.CounterBuffer;
            
            AsyncGPUReadback.Request(counterBuffer, (request) =>
            {
                if (request.hasError)
                {
                    Debug.LogError("Failed to read counter buffer.");
                    onComplete?.Invoke(false);
                    return;
                }

                using (var data = request.GetData<uint>())
                {
                    if (data.Length < 3)
                    {
                        Debug.LogError("Counter buffer data invalid.");
                        onComplete?.Invoke(false);
                        return;
                    }

                    int nodeCount = (int)data[0];
                    int payloadCount = (int)data[1];
                    int brickFloatCount = (int)data[2]; // Total floats in use

                    ReadDataBuffers(volume, filePath, nodeCount, payloadCount, brickFloatCount, onComplete);
                }
            });
        }

        private static void ReadDataBuffers(VoxelVolume volume, string filePath, int nodeCount, int payloadCount, int brickFloatCount, Action<bool> onComplete)
        {
            // We need to wait for 4 requests
            int pendingRequests = 4;
            bool failed = false;

            NativeArray<SVONode> nodes = default;
            NativeArray<VoxelPayload> payloads = default;
            NativeArray<float> brickData = default;
            NativeArray<uint> brickMaterials = default;

            void CheckComplete()
            {
                if (failed) return;
                
                if (pendingRequests == 0)
                {
                    try
                    {
                        WriteFile(filePath, volume.Resolution, nodeCount, payloadCount, brickFloatCount, nodes, payloads, brickData, brickMaterials);
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
                        if (brickMaterials.IsCreated) brickMaterials.Dispose();
                    }
                }
            }

            // 1. Nodes
            if (nodeCount > 0)
            {
                AsyncGPUReadback.Request(volume.NodeBuffer, nodeCount * System.Runtime.InteropServices.Marshal.SizeOf<SVONode>(), 0, (req) =>
                {
                    if (req.hasError) { failed = true; Debug.LogError("Node readback error"); }
                    else
                    {
                        var temp = new NativeArray<SVONode>(req.GetData<SVONode>().Length, Allocator.Persistent);
                        temp.CopyFrom(req.GetData<SVONode>());
                        nodes = temp;
                    }
                    
                    pendingRequests--;
                    CheckComplete();
                });
            }
            else
            {
                nodes = new NativeArray<SVONode>(0, Allocator.Persistent);
                pendingRequests--;
                CheckComplete();
            }

            // 2. Payloads
            if (payloadCount > 0)
            {
                AsyncGPUReadback.Request(volume.PayloadBuffer, payloadCount * System.Runtime.InteropServices.Marshal.SizeOf<VoxelPayload>(), 0, (req) =>
                {
                    if (req.hasError) { failed = true; Debug.LogError("Payload readback error"); }
                    else
                    {
                        var temp = new NativeArray<VoxelPayload>(req.GetData<VoxelPayload>().Length, Allocator.Persistent);
                        temp.CopyFrom(req.GetData<VoxelPayload>());
                        payloads = temp;
                    }
                    pendingRequests--;
                    CheckComplete();
                });
            }
            else
            {
                payloads = new NativeArray<VoxelPayload>(0, Allocator.Persistent);
                pendingRequests--;
                CheckComplete();
            }

            // 3. Brick Data (Floats)
            if (brickFloatCount > 0)
            {
                AsyncGPUReadback.Request(volume.BrickBuffer, brickFloatCount * sizeof(float), 0, (req) =>
                {
                    if (req.hasError) { failed = true; Debug.LogError("Brick data readback error"); }
                    else
                    {
                        var temp = new NativeArray<float>(req.GetData<float>().Length, Allocator.Persistent);
                        temp.CopyFrom(req.GetData<float>());
                        brickData = temp;
                    }
                    pendingRequests--;
                    CheckComplete();
                });
            }
            else
            {
                brickData = new NativeArray<float>(0, Allocator.Persistent);
                pendingRequests--;
                CheckComplete();
            }

            // 4. Brick Materials (Uints)
            // Note: BrickMaterialBuffer has same count as BrickBuffer (per voxel)
            if (brickFloatCount > 0)
            {
                AsyncGPUReadback.Request(volume.BrickMaterialBuffer, brickFloatCount * sizeof(uint), 0, (req) =>
                {
                    if (req.hasError) { failed = true; Debug.LogError("Brick material readback error"); }
                    else
                    {
                        var temp = new NativeArray<uint>(req.GetData<uint>().Length, Allocator.Persistent);
                        temp.CopyFrom(req.GetData<uint>());
                        brickMaterials = temp;
                    }
                    pendingRequests--;
                    CheckComplete();
                });
            }
            else
            {
                brickMaterials = new NativeArray<uint>(0, Allocator.Persistent);
                pendingRequests--;
                CheckComplete();
            }
        }

        private static void WriteFile(string filePath, int resolution, int nodeCount, int payloadCount, int brickFloatCount,
            NativeArray<SVONode> nodes, NativeArray<VoxelPayload> payloads, NativeArray<float> brickData, NativeArray<uint> brickMaterials)
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
                    BrickFloatCount = brickFloatCount
                };
                header.Write(writer);

                // Use a GZipStream for data
                // We need to write the compressed data. 
                // We can write to a MemoryStream first to compress, then write to file.
                // Or wrap the file stream in GZipStream? 
                // If we wrap the whole thing, the header is compressed too (harder to peek).
                // Better to write header normally, then compressed blocks.
                
                // Block 1: Nodes
                WriteCompressedBlock(writer, nodes);
                
                // Block 2: Payloads
                WriteCompressedBlock(writer, payloads);
                
                // Block 3: Brick Data
                WriteCompressedBlock(writer, brickData);
                
                // Block 4: Brick Materials
                WriteCompressedBlock(writer, brickMaterials);
            }
            
            Debug.Log($"Saved voxel volume to {filePath}. Nodes: {nodeCount}, Payloads: {payloadCount}, Bricks: {brickFloatCount/64}");
        }

        private static void WriteCompressedBlock<T>(BinaryWriter writer, NativeArray<T> data) where T : struct
        {
            // Convert NativeArray to byte array
            int size = data.Length * System.Runtime.InteropServices.Marshal.SizeOf<T>();
            byte[] bytes = new byte[size];
            NativeArray<byte>.Copy(data.Reinterpret<byte>(System.Runtime.InteropServices.Marshal.SizeOf<T>()), bytes, size);

            // Compress
            using (var ms = new MemoryStream())
            {
                using (var gzip = new GZipStream(ms, CompressionMode.Compress))
                {
                    gzip.Write(bytes, 0, bytes.Length);
                }
                byte[] compressed = ms.ToArray();
                
                // Write size of compressed block for easier reading
                writer.Write(compressed.Length);
                writer.Write(compressed);
            }
        }

        public static void Load(VoxelVolume volume, string filePath)
        {
            if (!File.Exists(filePath))
            {
                Debug.LogError($"File not found: {filePath}");
                return;
            }

            using (var fs = new FileStream(filePath, FileMode.Open))
            using (var reader = new BinaryReader(fs))
            {
                var header = VoxelFileFormat.Header.Read(reader);
                
                if (header.Magic != VoxelFileFormat.MAGIC)
                {
                    Debug.LogError("Invalid file format.");
                    return;
                }

                // Ensure volume capacity
                // We'll access private members via reflection or we need to expose a Reinitialize method on VoxelVolume/SVOBufferManager.
                // For now, let's assume VoxelVolume has a Reinitialize method or we can just access buffers if they fit.
                // But if the loaded file has more nodes than maxNodes, we must resize.
                
                // Let's implement a Resize/EnsureCapacity on VoxelVolume?
                // Or just check strictly against current settings.
                
                if (header.NodeCount > volume.MaxNodes || header.BrickFloatCount / 64 > volume.MaxBricks)
                {
                     Debug.LogError($"Volume capacity too small. File: Nodes={header.NodeCount}, Bricks={header.BrickFloatCount/64}. Volume: Nodes={volume.MaxNodes}, Bricks={volume.MaxBricks}");
                     return;
                }

                // Read and Decompress
                
                // Nodes
                byte[] nodeBytes = ReadCompressedBlock(reader);
                var nodes = BytesToNativeArray<SVONode>(nodeBytes, header.NodeCount);
                volume.NodeBuffer.SetData(nodes);
                nodes.Dispose();

                // Payloads
                byte[] payloadBytes = ReadCompressedBlock(reader);
                var payloads = BytesToNativeArray<VoxelPayload>(payloadBytes, header.PayloadCount);
                volume.PayloadBuffer.SetData(payloads);
                payloads.Dispose();

                // Brick Data
                byte[] brickBytes = ReadCompressedBlock(reader);
                var brickData = BytesToNativeArray<float>(brickBytes, header.BrickFloatCount);
                volume.BrickBuffer.SetData(brickData);
                brickData.Dispose();

                // Brick Materials
                byte[] matBytes = ReadCompressedBlock(reader);
                var brickMats = BytesToNativeArray<uint>(matBytes, header.BrickFloatCount);
                volume.BrickMaterialBuffer.SetData(brickMats);
                brickMats.Dispose();

                // Update Counters
                // [0]=AllocatedNodes, [1]=AllocatedPayloads, [2]=AllocatedBricksPtr
                volume.CounterBuffer.SetData(new uint[] { (uint)header.NodeCount, (uint)header.PayloadCount, (uint)header.BrickFloatCount });
                
                Debug.Log($"Loaded voxel volume from {filePath}");
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
