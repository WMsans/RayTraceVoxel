using UnityEngine;
using VoxelEngine.Core;

namespace VoxelEngine.Core.Streaming
{
    public enum NodeState { Uninitialized, Pending, Empty, Solid, Active }
    /// <summary>
    /// Represents a node in the infinite world octree (CPU-side).
    /// Manages spatial data and holds a reference to a physical VoxelVolume if active.
    /// </summary>
    public class WorldOctreeNode
    {
        // --- Properties ---
        public Vector3 Center { get; private set; }
        public float Size { get; private set; }
        public int Depth { get; private set; }
        
        // --- Hierarchy ---
        public WorldOctreeNode Parent { get; private set; }
        public WorldOctreeNode[] Children { get; private set; }
        public bool IsLeaf => Children == null;

        public Bounds Bounds => new Bounds(Center, Vector3.one * Size);

        // --- Payload ---
        public VoxelVolume ActiveVolume { get; private set; }
        public NodeState State { get; private set; } = NodeState.Uninitialized;

        // --- Constants ---
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
        }

        public void Subdivide()
        {
            if (!IsLeaf) return;
            Children = new WorldOctreeNode[8];
            float quarterSize = Size * 0.25f;
            float childSize = Size * 0.5f;

            for (int i = 0; i < 8; i++)
            {
                Vector3 childPos = Center + (ChildOffsets[i] * quarterSize);
                Children[i] = new WorldOctreeNode(childPos, childSize, Depth + 1, this);
            }
        }

        public void Merge()
        {
            if (IsLeaf) return;
            foreach (var child in Children)
            {
                child.Merge(); 
                child.DisableVolume();
            }
            Children = null;
        }

        // --- Volume Management (UPDATED for Transient Auditor) ---

        public void RequestGeneration(MonoBehaviour runner)
        {
            if (State != NodeState.Uninitialized) return;
            
            if (VoxelVolumePool.Instance == null) return;

            State = NodeState.Pending;
            Vector3 minCorner = Center - (Vector3.one * Size * 0.5f);

            // Audit the chunk using the Transient Pool
            VoxelVolumePool.Instance.AuditChunk(minCorner, Size, 64, (result) => 
            {
                switch (result.type)
                {
                    case AuditResultType.Empty:
                        State = NodeState.Empty;
                        ActiveVolume = null;
                        break;
                    case AuditResultType.Solid:
                        State = NodeState.Solid;
                        ActiveVolume = null;
                        break;
                    case AuditResultType.Complex:
                        State = NodeState.Active;
                        ActiveVolume = result.volume;
                        if (ActiveVolume != null)
                        {
                            ActiveVolume.name = $"Volume_D{Depth}_{Center}";
                        }
                        break;
                    case AuditResultType.Retry:
                        State = NodeState.Uninitialized; // Try again later
                        break;
                }
            });
        }

        public void DisableVolume()
        {
            if (ActiveVolume != null)
            {
                if (VoxelVolumePool.Instance != null)
                {
                    VoxelVolumePool.Instance.ReturnVolume(ActiveVolume);
                }
                ActiveVolume = null;
            }
            State = NodeState.Uninitialized;
        }
    }
}