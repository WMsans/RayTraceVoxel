using UnityEngine;
using System.Collections.Generic;
using VoxelEngine.Core.Editing;

namespace VoxelEngine.Core.Structural
{
    public class StructuralGraphManager : MonoSingleton<StructuralGraphManager>
    {
        [Header("Debug")]
        public bool showGraph = true;
        public bool showInternalMask = false;

        // Graph Storage: Key = Global Brick Coordinate
        private Dictionary<Vector3Int, StructuralNode> _graph = new Dictionary<Vector3Int, StructuralNode>();
        
        // Neighbor offsets matching StructuralFace enum order
        private readonly Vector3Int[] _neighborOffsets = new Vector3Int[]
        {
            new Vector3Int(-1, 0, 0), // Left
            new Vector3Int( 1, 0, 0), // Right
            new Vector3Int( 0,-1, 0), // Down
            new Vector3Int( 0, 1, 0), // Up
            new Vector3Int( 0, 0,-1), // Back
            new Vector3Int( 0, 0, 1)  // Forward
        };

        private void Start()
        {
            if (VoxelEditManager.Instance != null)
            {
                VoxelEditManager.Instance.OnBrickModified += HandleBrickModification;
            }
        }

        private void OnDestroy()
        {
            if (VoxelEditManager.Instance != null)
            {
                VoxelEditManager.Instance.OnBrickModified -= HandleBrickModification;
            }
        }

        /// <summary>
        /// Phase 2: Localized Update Trigger
        /// Called when VoxelModifier writes changes to the VoxelEditManager.
        /// </summary>
        private void HandleBrickModification(Vector3Int coord, uint[] newData)
        {
            // 1. Get or Create Node
            if (!_graph.TryGetValue(coord, out StructuralNode node))
            {
                node = new StructuralNode(coord);
                _graph[coord] = node;
            }

            // 2. Intra-Brick Analysis (CPU Flood Fill)
            // Check if the brick is solid, empty, or complex
            node.InternalConnectivityMask = StructuralAnalysis.CalculateConnectivity(newData);

            // If mask is 0, the brick is effectively air/empty. 
            // We might remove it from the graph or mark it dead.
            if (node.InternalConnectivityMask == 0)
            {
                RemoveNode(coord);
                return;
            }

            // 3. Inter-Brick Update (Reconnect Edges)
            UpdateNeighbors(node);

            // 4. (Future Phase 3) Trigger Floating Search here
            // CheckFloatingIslands(node);
            Debug.Log($"[Structural] Updated Node {coord}. Connectivity: {node.InternalConnectivityMask:X}");
        }

        private void UpdateNeighbors(StructuralNode node)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborCoord = node.Coordinate + _neighborOffsets[i];
                StructuralFace direction = (StructuralFace)i;
                StructuralFace opposite = GetOppositeFace(direction);

                if (_graph.TryGetValue(neighborCoord, out StructuralNode neighbor))
                {
                    // FIX: Only link if the faces are physically connected (have solid voxels at the interface)
                    // We check if the node has solid voxels on the outgoing face,
                    // AND if the neighbor has solid voxels on the incoming face.
                    // (CanTraverse(face, face) checks if the face bit is set in the mask).
                    
                    bool selfFaceActive = node.CanTraverse(direction, direction);
                    bool neighborFaceActive = neighbor.CanTraverse(opposite, opposite);

                    if (selfFaceActive && neighborFaceActive)
                    {
                        // Valid physical connection
                        node.Neighbors[i] = neighbor;
                        neighbor.Neighbors[(int)opposite] = node;
                    }
                    else
                    {
                        // Ensure they are disconnected if faces don't touch
                        // This handles the case where a neighbor exists but the specific face is empty
                        node.Neighbors[i] = null;
                        neighbor.Neighbors[(int)opposite] = null;
                    }
                }
            }
        }

        private void RemoveNode(Vector3Int coord)
        {
            if (_graph.TryGetValue(coord, out StructuralNode node))
            {
                // Sever connections
                for (int i = 0; i < 6; i++)
                {
                    if (node.Neighbors[i] != null)
                    {
                        StructuralFace opposite = GetOppositeFace((StructuralFace)i);
                        node.Neighbors[i].Neighbors[(int)opposite] = null;
                    }
                }
                _graph.Remove(coord);
                Debug.Log($"[Structural] Removed Node {coord}");
            }
        }

        private StructuralFace GetOppositeFace(StructuralFace face)
        {
            switch(face)
            {
                case StructuralFace.Left: return StructuralFace.Right;
                case StructuralFace.Right: return StructuralFace.Left;
                case StructuralFace.Up: return StructuralFace.Down;
                case StructuralFace.Down: return StructuralFace.Up;
                case StructuralFace.Forward: return StructuralFace.Back;
                case StructuralFace.Back: return StructuralFace.Forward;
                default: return StructuralFace.None;
            }
        }

        /// <summary>
        /// Phase 1: Initialization Loop
        /// Typically called after world generation.
        /// </summary>
        public void BuildGraphFromEdits()
        {
            _graph.Clear();
            // In a real scenario, you might iterate VoxelEditManager._sparseDatabase
            // But since that is private, we assume we can iterate known keys or the manager exposes them.
            // For now, this is reactive.
        }

        private void OnDrawGizmos()
        {
            if (!showGraph || _graph == null) return;

            // Cache the brick size (4.0 if voxelSize is 1.0)
            float brickSize = 4.0f; 
            if (VoxelEngine.Core.Editing.VoxelEditManager.Instance != null)
            {
                brickSize = 4.0f * VoxelEngine.Core.Editing.VoxelEditManager.Instance.voxelSize;
            }

            Vector3 halfSize = Vector3.one * brickSize * 0.5f;

            foreach (var kvp in _graph)
            {
                StructuralNode node = kvp.Value;
                Vector3 center = (Vector3)node.Coordinate * brickSize + halfSize;

                // 1. Draw Node Box
                // Green = Anchored (Stable), Cyan = Floating (Unstable - for future use)
                Gizmos.color = node.IsAnchored ? Color.green : Color.cyan;
                Gizmos.DrawWireCube(center, Vector3.one * brickSize * 0.9f);

                // 2. Draw Connections to Neighbors
                Gizmos.color = Color.yellow;
                for (int i = 0; i < 6; i++)
                {
                    // Only draw if the neighbor exists in the graph and is linked
                    if (node.Neighbors[i] != null)
                    {
                        Vector3 neighborCenter = (Vector3)node.Neighbors[i].Coordinate * brickSize + halfSize;
                        Gizmos.DrawLine(center, Vector3.Lerp(center, neighborCenter, 0.5f));
                    }
                }
            }
        }
    }
}
