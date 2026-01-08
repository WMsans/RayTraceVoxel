using UnityEngine;
using VoxelEngine.Core.Streaming;

public class ChunkLifecycleTester : MonoBehaviour
{
    public int worldSize = 512;
    private WorldOctreeNode _rootNode;

    private void Start()
    {
        // 1. Initialize Root (No volume yet)
        _rootNode = new WorldOctreeNode(Vector3.zero, worldSize, 0, null);
        Debug.Log("Root Node Initialized. Press '1' to Show Root, '2' to Subdivide, '3' to Clear.");
    }

    private void Update()
    {
        // TEST 1: Activate Root Chunk (Pull from Pool)
        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            if (_rootNode.ActiveVolume == null)
            {
                _rootNode.EnableVolume(this.transform);
                Debug.Log("Test: Root Volume Pulled from Pool.");
            }
        }

        // TEST 2: Subdivide (Return Root to Pool, Pull 8 Children)
        if (Input.GetKeyDown(KeyCode.Alpha2))
        {
            if (_rootNode.IsLeaf)
            {
                // To subdivide, we must disable the parent volume first (streaming logic)
                _rootNode.DisableVolume();
                
                _rootNode.Subdivide();
                
                foreach (var child in _rootNode.Children)
                {
                    child.EnableVolume(this.transform);
                }
                Debug.Log("Test: Root Subdivided. 8 Child Volumes Pulled.");
            }
        }

        // TEST 3: Merge (Return 8 Children to Pool, Pull Root)
        if (Input.GetKeyDown(KeyCode.Alpha3))
        {
            if (!_rootNode.IsLeaf)
            {
                _rootNode.Merge(); // This calls DisableVolume on all children internally
                
                // Re-enable root
                _rootNode.EnableVolume(this.transform);
                Debug.Log("Test: Merged back to Root. Children returned to Pool.");
            }
        }
        
        // TEST 4: Clean Up (Return All)
        if (Input.GetKeyDown(KeyCode.Backspace))
        {
            _rootNode.Merge();
            _rootNode.DisableVolume();
            Debug.Log("Test: All volumes returned to pool.");
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