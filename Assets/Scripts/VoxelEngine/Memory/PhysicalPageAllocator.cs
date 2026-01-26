using System.Collections.Generic;
using UnityEngine;

namespace VoxelEngine.Core.Memory
{
    public class PhysicalPageAllocator
    {
        private Stack<int> _freePages;
        public int PageSize { get; private set; }
        public int TotalPages { get; private set; }
        public int FreePageCount => _freePages.Count;

        public PhysicalPageAllocator(int totalPages, int pageSize)
        {
            TotalPages = totalPages;
            PageSize = pageSize;
            _freePages = new Stack<int>(totalPages);
            // Push in reverse so we pop 0 first (deterministic)
            for (int i = totalPages - 1; i >= 0; i--)
            {
                _freePages.Push(i);
            }
        }

        public bool Allocate(int pagesNeeded, out int[] allocatedPages)
        {
            allocatedPages = null;
            if (_freePages.Count < pagesNeeded) return false;

            allocatedPages = new int[pagesNeeded];
            for (int i = 0; i < pagesNeeded; i++)
            {
                allocatedPages[i] = _freePages.Pop();
            }
            return true;
        }

        public void Free(int[] pages)
        {
            if (pages == null) return;
            foreach (var page in pages)
            {
                _freePages.Push(page);
            }
        }
    }
}
