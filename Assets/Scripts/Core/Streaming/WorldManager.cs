using UnityEngine;

namespace VoxelEngine.Core.Streaming
{
    // Require the pool to be present
    [RequireComponent(typeof(VoxelVolumePool))]
    public class WorldManager : MonoBehaviour
    {
        [Header("Configuration")]
        public int initialWorldSize = 1024;
        
        private WorldOctreeNode _rootNode;
        private VoxelVolumePool _pool;

        private void Start()
        {
            _pool = GetComponent<VoxelVolumePool>();
            
            // Initialize Root Node at (0,0,0)
            _rootNode = new WorldOctreeNode(Vector3.zero, initialWorldSize, 0, null);

            // Test logic:
            // 1. Subdivide root
            _rootNode.Subdivide();
            
            // 2. Enable volumes for children (using Pool)
            foreach (var child in _rootNode.Children)
            {
                child.EnableVolume(this.transform);
                
                // Recursive Split Test
                if (child.Center.x > 0 && child.Center.y > 0 && child.Center.z > 0)
                {
                    child.DisableVolume();
                    child.Subdivide();
                    foreach (var grandChild in child.Children)
                    {
                        grandChild.EnableVolume(this.transform);
                    }
                }
            }
        }

        private void OnDestroy()
        {
            if (_rootNode != null)
            {
                _rootNode.Merge();
                _rootNode.DisableVolume();
            }
        }
    }
}