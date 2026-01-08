using UnityEngine;

namespace VoxelEngine.Core.Streaming
{
    public class WorldManager : MonoBehaviour
    {
        [Header("Configuration")]
        public VoxelVolume volumePrefab;
        public int initialWorldSize = 1024;
        public int maxDepth = 3;
        
        private WorldOctreeNode _rootNode;

        private void Start()
        {
            // Initialize Root Node at (0,0,0)
            _rootNode = new WorldOctreeNode(Vector3.zero, initialWorldSize, 0, null);

            // Test: Subdivide root to verify structure
            _rootNode.Subdivide();
            
            // Test: Enable volumes for the children (Depth 1)
            foreach (var child in _rootNode.Children)
            {
                child.EnableVolume(volumePrefab, this.transform);
                
                // Recursive test: Subdivide one child further
                if (child.Center.x > 0 && child.Center.y > 0 && child.Center.z > 0)
                {
                    child.DisableVolume(); // Disable parent volume before splitting
                    child.Subdivide();
                    foreach (var grandChild in child.Children)
                    {
                        grandChild.EnableVolume(volumePrefab, this.transform);
                    }
                }
            }
        }

        private void OnDestroy()
        {
            if (_rootNode != null)
            {
                _rootNode.Merge(); // Cleans up all volumes
                _rootNode.DisableVolume();
            }
        }
    }
}