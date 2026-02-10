using UnityEngine;
using VoxelEngine.Core;
using System;

namespace VoxelEngine.Core.Streaming
{
    public enum NodeState { Uninitialized, Pending, Empty, Solid, Active }

    public class WorldOctreeNode
    {
        // --- Native Linkage ---
        public int Index { get; private set; } = -1;

        // --- Properties ---
        public Vector3 Center { get; private set; }
        public float Size { get; private set; }
        public int Depth { get; private set; }
        
        public WorldOctreeNode Parent { get; private set; }
        public WorldOctreeNode[] Children { get; private set; }
        public bool IsLeaf => Children == null;

        public Bounds Bounds => new Bounds(Center, Vector3.one * Size);
        public VoxelVolume ActiveVolume { get; private set; }
        public NodeState State { get; private set; } = NodeState.Uninitialized;

        private int _generationRequestId = 0; 
        private static readonly Vector3[] ChildOffsets = new Vector3[]
        {
            new Vector3(-1, -1, -1), new Vector3(1, -1, -1),
            new Vector3(-1, 1, -1),  new Vector3(1, 1, -1),
            new Vector3(-1, -1, 1),  new Vector3(1, -1, 1),
            new Vector3(-1, 1, 1),   new Vector3(1, 1, 1)
        };

        public WorldOctreeNode(Vector3 center, float size, int depth, WorldOctreeNode parent)
        {
            Center = center;
            Size = size;
            Depth = depth;
            Parent = parent;

            // Register with Manager for Burst tracking
            if (WorldManager.Instance != null)
            {
                Index = WorldManager.Instance.RegisterNode(this, center, size, depth);
            }
        }

        public void Subdivide()
        {
            if (State == NodeState.Pending) CancelGeneration();
            if (!IsLeaf) return;

            Children = new WorldOctreeNode[8];
            float quarterSize = Size * 0.25f;
            float childSize = Size * 0.5f;

            for (int i = 0; i < 8; i++)
            {
                Vector3 childPos = Center + (ChildOffsets[i] * quarterSize);
                Children[i] = new WorldOctreeNode(childPos, childSize, Depth + 1, this);
            }

            // Sync Struct
            if (WorldManager.Instance != null) 
                WorldManager.Instance.UpdateNodeStruct(Index, false); // No longer a leaf
        }

        public void Merge()
        {
            if (IsLeaf) return;

            // If we are already waiting for a merge generation, do nothing.
            if (State == NodeState.Pending) return;

            // 1. Request Parent Content
            RequestGeneration(WorldManager.Instance, (success) => 
            {
                if (success)
                {
                    if (IsLeaf) return; // Already merged?

                    // 2. Dispose Children
                    foreach (var child in Children)
                    {
                        child.Dispose();
                    }
                    Children = null;

                    // Sync Struct
                    if (WorldManager.Instance != null) 
                        WorldManager.Instance.UpdateNodeStruct(Index, true); // Now a leaf
                }
            }, forMerge: true);
        }

        public void Dispose()
        {
            Merge(); // Recursively clean children
            DisableVolume();
            
            if (WorldManager.Instance != null && Index != -1)
            {
                WorldManager.Instance.UnregisterNode(Index);
                Index = -1;
            }
        }

        public bool AreChildrenReady
        {
            get
            {
                if (IsLeaf || Children == null) return true;
                if (State == NodeState.Pending) return false;
                
                for (int i = 0; i < Children.Length; i++)
                {
                    // [FIX] Ignore children that are branches (they handle their own LOD).
                    // We only wait for leaf children to be ready (Generated/Empty).
                    if (!Children[i].IsLeaf) continue;

                    if (Children[i].State == NodeState.Pending || Children[i].State == NodeState.Uninitialized)
                        return false;
                }
                return true;
            }
        }

        public void RequestGeneration(MonoBehaviour runner, Action<bool> onComplete = null, bool forMerge = false)
        {
            if (State == NodeState.Active || State == NodeState.Empty || State == NodeState.Solid)
            {
                onComplete?.Invoke(true);
                return;
            }

            if (State != NodeState.Uninitialized) return;
            if (VoxelVolumePool.Instance == null) { onComplete?.Invoke(false); return; }

            State = NodeState.Pending;
            _generationRequestId++;
            int currentRequestId = _generationRequestId;
            Vector3 minCorner = Center - (Vector3.one * Size * 0.5f);

            VoxelVolumePool.Instance.AuditChunk(minCorner, Size, 64, (result) => 
            {
                if (currentRequestId != _generationRequestId || State != NodeState.Pending)
                {
                    if (result.type == AuditResultType.Complex && result.volume != null)
                        VoxelVolumePool.Instance.ReturnVolume(result.volume);
                    onComplete?.Invoke(false);
                    return;
                }

                if (!forMerge && !IsLeaf)
                {
                    if (result.type == AuditResultType.Complex && result.volume != null)
                        VoxelVolumePool.Instance.ReturnVolume(result.volume);
                    
                    // [FIX] Reset state so we don't get stuck in Pending forever.
                    State = NodeState.Uninitialized; 
                    onComplete?.Invoke(false);
                    return;
                }

                switch (result.type)
                {
                    case AuditResultType.Empty: State = NodeState.Empty; break;
                    case AuditResultType.Solid: State = NodeState.Solid; break;
                    case AuditResultType.Complex:
                        State = NodeState.Active;
                        ActiveVolume = result.volume;
                        if (ActiveVolume != null) ActiveVolume.name = $"Volume_D{Depth}_{Center}";
                        break;
                    case AuditResultType.Retry:
                        State = NodeState.Uninitialized;
                        onComplete?.Invoke(false);
                        return;
                }
                onComplete?.Invoke(true);
            });
        }

        public void DisableVolume()
        {
            CancelGeneration();
            if (ActiveVolume != null)
            {
                if (VoxelVolumePool.Instance != null) VoxelVolumePool.Instance.ReturnVolume(ActiveVolume);
                ActiveVolume = null;
            }
            State = NodeState.Uninitialized;
        }

        public void CancelGeneration()
        {
            if (State == NodeState.Pending)
            {
                _generationRequestId++;
                State = NodeState.Uninitialized;
            }
        }
    }
}