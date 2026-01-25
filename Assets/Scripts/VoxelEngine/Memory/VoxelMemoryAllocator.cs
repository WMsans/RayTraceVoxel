using System.Collections.Generic;
using UnityEngine;

namespace VoxelEngine.Core.Memory
{
    public class VoxelMemoryAllocator
    {
        private class MemoryBlock
        {
            public int start;
            public int size;
            public bool isFree;

            public MemoryBlock(int start, int size, bool isFree)
            {
                this.start = start;
                this.size = size;
                this.isFree = isFree;
            }
        }

        private List<MemoryBlock> _blocks;
        private int _totalSize;

        public int TotalFree { get; private set; }
        public int TotalSize => _totalSize;

        public VoxelMemoryAllocator(int totalSize)
        {
            _totalSize = totalSize;
            TotalFree = totalSize;
            _blocks = new List<MemoryBlock>();
            _blocks.Add(new MemoryBlock(0, totalSize, true));
        }

        public bool Allocate(int size, out int offset)
        {
            offset = -1;
            if (size <= 0) return false;

            // Best Fit Strategy: Find the smallest free block that is large enough
            int bestIndex = -1;
            int bestSize = int.MaxValue;

            for (int i = 0; i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                if (block.isFree && block.size >= size)
                {
                    if (block.size < bestSize)
                    {
                        bestSize = block.size;
                        bestIndex = i;
                        // Optimization: if exact match, take it immediately
                        if (block.size == size) break;
                    }
                }
            }

            if (bestIndex != -1)
            {
                var block = _blocks[bestIndex];
                
                // If block is larger than requested, split it
                if (block.size > size)
                {
                    var newBlock = new MemoryBlock(block.start + size, block.size - size, true);
                    _blocks.Insert(bestIndex + 1, newBlock);
                    
                    block.size = size;
                }
                
                block.isFree = false;
                offset = block.start;
                TotalFree -= size;
                return true;
            }

            Debug.LogWarning($"VoxelMemoryAllocator: Out of memory. Requested: {size}, Free: {TotalFree}");
            return false;
        }

        public void Free(int offset, int size)
        {
            // Find the block
            // This could be optimized with a dictionary or by keeping the list sorted (it is naturally sorted by offset in this impl)
            
            // Binary search could be used if list is large, but linear scan is fine for now assuming not too many fragments
            for (int i = 0; i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                if (block.start == offset)
                {
                    if (block.isFree)
                    {
                        Debug.LogError($"VoxelMemoryAllocator: Double free at {offset}");
                        return;
                    }
                    
                    if (block.size != size)
                    {
                        Debug.LogWarning($"VoxelMemoryAllocator: Free size mismatch. Block: {block.size}, Requested: {size}. Freeing block size.");
                        // We trust the block size over the requested size in this simple implementation
                    }

                    block.isFree = true;
                    TotalFree += block.size;

                    // Coalesce with next
                    if (i + 1 < _blocks.Count && _blocks[i+1].isFree)
                    {
                        block.size += _blocks[i+1].size;
                        _blocks.RemoveAt(i + 1);
                    }

                    // Coalesce with prev
                    if (i - 1 >= 0 && _blocks[i-1].isFree)
                    {
                        var prev = _blocks[i-1];
                        prev.size += block.size;
                        _blocks.RemoveAt(i);
                    }
                    
                    return;
                }
            }
            
            Debug.LogError($"VoxelMemoryAllocator: Block not found for free at {offset}");
        }
        
        public void Reset()
        {
            _blocks.Clear();
            _blocks.Add(new MemoryBlock(0, _totalSize, true));
            TotalFree = _totalSize;
        }
    }
}
