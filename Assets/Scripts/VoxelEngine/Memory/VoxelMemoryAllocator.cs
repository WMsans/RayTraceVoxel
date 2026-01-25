using System.Collections.Generic;
using UnityEngine;

namespace VoxelEngine.Core.Memory
{
    public struct MemoryBlock
    {
        public int start;
        public int size;

        public int end => start + size;
    }

    public class VoxelMemoryAllocator
    {
        private List<MemoryBlock> _freeBlocks = new List<MemoryBlock>();
        private int _totalSize;

        public int FreeSpace { get; private set; }

        public VoxelMemoryAllocator(int totalSize)
        {
            _totalSize = totalSize;
            FreeSpace = totalSize;
            _freeBlocks.Add(new MemoryBlock { start = 0, size = totalSize });
        }

        public bool Allocate(int requestedSize, out int offset)
        {
            offset = -1;
            // Best Fit Strategy
            int bestBlockIndex = -1;
            int minSizeDiff = int.MaxValue;

            for (int i = 0; i < _freeBlocks.Count; i++)
            {
                if (_freeBlocks[i].size >= requestedSize)
                {
                    int diff = _freeBlocks[i].size - requestedSize;
                    if (diff < minSizeDiff)
                    {
                        minSizeDiff = diff;
                        bestBlockIndex = i;
                    }
                }
            }

            if (bestBlockIndex != -1)
            {
                MemoryBlock block = _freeBlocks[bestBlockIndex];
                offset = block.start;

                if (block.size == requestedSize)
                {
                    _freeBlocks.RemoveAt(bestBlockIndex);
                }
                else
                {
                    // Split block
                    MemoryBlock newBlock = new MemoryBlock
                    {
                        start = block.start + requestedSize,
                        size = block.size - requestedSize
                    };
                    _freeBlocks[bestBlockIndex] = newBlock;
                }
                FreeSpace -= requestedSize;
                return true;
            }

            return false;
        }

        public void Free(int start, int size)
        {
            // Insert and merge
            MemoryBlock newBlock = new MemoryBlock { start = start, size = size };
            FreeSpace += size;
            
            // Find insertion point to keep list sorted by start address
            int insertIndex = 0;
            while (insertIndex < _freeBlocks.Count && _freeBlocks[insertIndex].start < start)
            {
                insertIndex++;
            }
            
            _freeBlocks.Insert(insertIndex, newBlock);

            // Merge with next
            if (insertIndex < _freeBlocks.Count - 1)
            {
                MemoryBlock current = _freeBlocks[insertIndex];
                MemoryBlock next = _freeBlocks[insertIndex + 1];
                if (current.end == next.start)
                {
                    current.size += next.size;
                    _freeBlocks[insertIndex] = current;
                    _freeBlocks.RemoveAt(insertIndex + 1);
                }
            }

            // Merge with prev (careful with index after potential merge above)
            if (insertIndex > 0)
            {
                // Re-fetch current because it might have changed (merged with next)
                // But insertIndex is still valid for the *current* block position in the list
                MemoryBlock prev = _freeBlocks[insertIndex - 1];
                MemoryBlock current = _freeBlocks[insertIndex];
                
                if (prev.end == current.start)
                {
                    prev.size += current.size;
                    _freeBlocks[insertIndex - 1] = prev;
                    _freeBlocks.RemoveAt(insertIndex);
                }
            }
        }
        
        public void Reset()
        {
            _freeBlocks.Clear();
            _freeBlocks.Add(new MemoryBlock { start = 0, size = _totalSize });
            FreeSpace = _totalSize;
        }
    }
}
