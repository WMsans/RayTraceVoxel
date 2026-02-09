using UnityEngine;
using VoxelEngine.Core;
using System;

namespace VoxelEngine.Core.Streaming
{
    public enum NodeState { Uninitialized, Pending, Empty, Solid, Active }

    /// <summary>
    /// Represents a node in the infinite world octree (CPU-side).
    /// Manages spatial data and holds a reference to a physical VoxelVolume if active.
    /// Includes robust state management for Async LOD transitions.
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
        
        /// <summary>
        /// The state of THIS node's volume. 
        /// Note: A Branch node (with children) can still have a 'Pending' or 'Active' state 
        /// if it is currently handling an LOD transition (Merging/Splitting).
        /// </summary>
        public NodeState State { get; private set; } = NodeState.Uninitialized;

        // --- Safety ---
        // Used to invalidate pending async generation requests if the node changes state/topology
        private int _generationRequestId = 0; 

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
            // If we are subdividing, we must abort any pending Merge operation on this node.
            // This handles the "Merge -> Split" race condition.
            if (State == NodeState.Pending)
            {
                CancelGeneration();
            }

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
            
            // recursively merge children
            foreach (var child in Children)
            {
                child.Merge(); 
                child.DisableVolume(); // Ensures children return their resources
            }
            Children = null;
        }

        // --- Volume Management ---

        /// <summary>
        /// Cancels any pending generation request for this node.
        /// Useful when interrupting a Merge to switch back to Splitting.
        /// </summary>
        public void CancelGeneration()
        {
            if (State == NodeState.Pending)
            {
                // Incrementing the ID ensures the in-flight callback will fail the "requestId match" check
                _generationRequestId++;
                State = NodeState.Uninitialized;
            }
        }

        public bool AreChildrenReady
        {
            get
            {
                if (IsLeaf || Children == null) return true;

                // RACE CONDITION FIX:
                // If THIS node is 'Pending', it means we are actively generating our own mesh to Merge.
                // In this state, we should strictly prioritize the Merge and NOT swap to children,
                // even if the children happen to finish loading in the background.
                // This prevents "Flickering" (Parent -> Children -> Parent) during rapid movement.
                if (State == NodeState.Pending) return false;
                
                // Otherwise, check if children are ready to be shown
                for (int i = 0; i < Children.Length; i++)
                {
                    // Children must be in a final state (Active, Empty, or Solid) to be considered ready.
                    if (Children[i].State == NodeState.Pending || Children[i].State == NodeState.Uninitialized)
                        return false;
                }
                return true;
            }
        }

        public void RequestGeneration(MonoBehaviour runner, Action<bool> onComplete = null, bool forMerge = false)
        {
            // If we are already busy, do not restart unless explicitly cancelled.
            if (State != NodeState.Uninitialized) return;
            
            if (VoxelVolumePool.Instance == null) 
            {
                onComplete?.Invoke(false);
                return;
            }

            State = NodeState.Pending;
            
            // Increment ID to identify this specific request. 
            // Any previous pending callbacks will see a mismatch and abort.
            _generationRequestId++;
            int currentRequestId = _generationRequestId;

            Vector3 minCorner = Center - (Vector3.one * Size * 0.5f);

            // Audit the chunk using the Transient Pool
            VoxelVolumePool.Instance.AuditChunk(minCorner, Size, 64, (result) => 
            {
                // --- STATE & RACE CONDITION CHECKS ---

                // 1. Request ID Match: Has CancelGeneration() or DisableVolume() been called?
                if (currentRequestId != _generationRequestId)
                {
                    // Obsolete request. Cleanup if we accidentally got a volume.
                    if (result.type == AuditResultType.Complex && result.volume != null)
                        VoxelVolumePool.Instance.ReturnVolume(result.volume);
                    
                    onComplete?.Invoke(false);
                    return;
                }

                // 2. State Check: Are we still Pending? (Double check)
                if (State != NodeState.Pending)
                {
                    if (result.type == AuditResultType.Complex && result.volume != null)
                        VoxelVolumePool.Instance.ReturnVolume(result.volume);
                        
                    onComplete?.Invoke(false);
                    return;
                }

                // 3. Topology Check:
                // If this was a standard generation (!forMerge), we MUST be a Leaf.
                // If we became a Branch (Subdivide called) while waiting, this mesh is invalid.
                if (!forMerge && !IsLeaf)
                {
                    if (result.type == AuditResultType.Complex && result.volume != null)
                        VoxelVolumePool.Instance.ReturnVolume(result.volume);

                    // We don't change state here; Subdivide() handled the state change.
                    // We just abort this specific callback.
                    onComplete?.Invoke(false);
                    return;
                }

                // --- APPLY RESULT ---

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
                        State = NodeState.Uninitialized; // Reset to try again later
                        onComplete?.Invoke(false);
                        return;
                }

                onComplete?.Invoke(true);
            });
        }

        public void DisableVolume()
        {
            // Cancel any pending operations immediately
            CancelGeneration();

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