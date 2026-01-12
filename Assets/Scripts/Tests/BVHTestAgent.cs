using UnityEngine;
using System.Collections.Generic;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Generators;

namespace VoxelEngine.Core.Testing
{
    public class BVHTestAgent : MonoBehaviour
    {
        [Header("Simulation")]
        public int objectCount = 50;
        public float spawnRadius = 50f;
        public float moveSpeed = 2.0f;
        public bool showLeafs = true;
        public bool showInternal = true;

        private List<Vector3> _startPositions = new List<Vector3>();
        private List<Vector3> _phaseOffsets = new List<Vector3>();

        private void Start()
        {
            // 1. Initialize Objects
            DynamicSDFManager.Instance.ClearObjects();
            _startPositions.Clear();
            _phaseOffsets.Clear();

            for (int i = 0; i < objectCount; i++)
            {
                Vector3 pos = Random.insideUnitSphere * spawnRadius;
                _startPositions.Add(pos);
                _phaseOffsets.Add(new Vector3(Random.value, Random.value, Random.value) * 10f);

                // Create initial dummy object
                SDFObject obj = new SDFObject
                {
                    position = pos,
                    scale = Vector3.one * 2.0f,
                    boundsMin = pos - Vector3.one,
                    boundsMax = pos + Vector3.one,
                    type = 0 // Sphere
                };
                DynamicSDFManager.Instance.RegisterObject(obj);
            }
        }

        private void Update()
        {
            // 2. Animate Objects
            for (int i = 0; i < objectCount; i++)
            {
                Vector3 basePos = _startPositions[i];
                Vector3 phase = _phaseOffsets[i];
                float t = Time.time * moveSpeed;

                Vector3 newPos = basePos + new Vector3(
                    Mathf.Sin(t + phase.x) * 10f,
                    Mathf.Cos(t + phase.y) * 10f,
                    Mathf.Sin(t + phase.z) * 10f
                );

                // Construct updated SDFObject
                SDFObject obj = new SDFObject
                {
                    position = newPos,
                    boundsMin = newPos - Vector3.one * 2.0f, // Bounds size 4
                    boundsMax = newPos + Vector3.one * 2.0f,
                    scale = Vector3.one * 2.0f,
                    type = 0
                };

                // Update in Manager
                DynamicSDFManager.Instance.UpdateObject(i, obj);
            }

            // 3. Force Rebuild (Since Manager is a ScriptableObject, we drive it here)
            DynamicSDFManager.Instance.RebuildBVH();
        }

        private void OnDrawGizmos()
        {
            if (!Application.isPlaying) return;

            // 4. Visualize from GPU Buffer (via CPU copy in Manager for now)
            // Accessing private nodes via Reflection or assuming we made them public for debug?
            // For this test, let's just make the node array public in DynamicSDFManager 
            // OR add a "DrawGizmos" method to the Manager that takes a transform/context.
            
            // Note: Since we implemented OnDrawGizmos inside DynamicSDFManager in the previous step,
            // we can just call it if we change that method to public or use this helper:
            
            DrawBVH();
        }

        // Re-implementing draw logic here since ScriptableObject gizmos are tricky
        private void DrawBVH()
        {
            var buffer = DynamicSDFManager.Instance.LBVHNodeBuffer;
            if (buffer == null || !buffer.IsValid()) return;

            // Read back for debug (Slow, editor only)
            LBVHNode[] nodes = new LBVHNode[buffer.count];
            buffer.GetData(nodes);

            if (nodes.Length > 0)
            {
                DrawNodeRecursive(nodes, 0, 0);
            }
        }

        private void DrawNodeRecursive(LBVHNode[] nodes, int index, int depth)
        {
            if (index < 0 || index >= nodes.Length) return;

            LBVHNode node = nodes[index];
            bool isLeaf = node.leftChild < 0;

            if (isLeaf)
            {
                if (showLeafs)
                {
                    Gizmos.color = Color.green;
                    Vector3 size = node.boundsMax - node.boundsMin;
                    Gizmos.DrawWireCube(node.boundsMin + size * 0.5f, size);
                }
            }
            else
            {
                if (showInternal)
                {
                    Gizmos.color = Color.Lerp(Color.cyan, Color.blue, depth / 10f);
                    Vector3 size = node.boundsMax - node.boundsMin;
                    Gizmos.DrawWireCube(node.boundsMin + size * 0.5f, size);
                }
                
                // Recurse
                DrawNodeRecursive(nodes, node.leftChild, depth + 1);
                DrawNodeRecursive(nodes, node.rightChild, depth + 1);
            }
        }
    }
}