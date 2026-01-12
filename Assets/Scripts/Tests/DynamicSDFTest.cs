using UnityEngine;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Generators;

public class DynamicSDFTest : MonoBehaviour
{
    [Header("Animation Settings")]
    public bool animate = true;
    public float orbitRadius = 30.0f;
    public float rotationSpeed = 1.0f;
    public float heightOffset = 0.0f; // Base height (0 blends with ground usually)

    private void Start()
    {
        if (DynamicSDFManager.Instance == null) return;

        // Clear existing objects to prevent duplicates on reload
        DynamicSDFManager.Instance.ClearObjects();

        // 1. Register a Sphere (Union)
        // Initial position will be overwritten in Update, but good to set defaults
        SDFObject sphere = new SDFObject
        {
            position = new Vector3(orbitRadius, heightOffset, 0),
            rotation = Quaternion.identity,
            scale = Vector3.one * 1.5f, // Slightly larger
            boundsMin = Vector3.zero, // Will be updated
            boundsMax = Vector3.zero,
            type = 0, // Sphere
            operation = 0, // Union
            blendFactor = 8.0f, // Large smooth blend
            materialId = 3 // Special material (e.g., Purple)
        };
        DynamicSDFManager.Instance.RegisterObject(sphere);

        // 2. Register a Cube (Subtraction)
        SDFObject cube = new SDFObject
        {
            position = new Vector3(-orbitRadius, heightOffset, 0),
            rotation = Quaternion.identity,
            scale = Vector3.one,
            boundsMin = Vector3.zero,
            boundsMax = Vector3.zero,
            type = 1, // Cube
            operation = 1, // Subtract
            blendFactor = 3.0f,
            materialId = 0 // Air (doesn't matter much for subtraction)
        };
        DynamicSDFManager.Instance.RegisterObject(cube);
    }

    private void Update()
    {
        if (!animate || DynamicSDFManager.Instance == null) return;

        float t = Time.time * rotationSpeed;

        // --- Animate Sphere ---
        // Rotates around (0,0,0) and bobs up and down
        var sphere = DynamicSDFManager.Instance.GetObject(0);
        
        float sx = Mathf.Cos(t) * orbitRadius;
        float sz = Mathf.Sin(t) * orbitRadius;
        float sy = heightOffset + Mathf.Sin(t * 2.5f) * 10.0f; // Bob between -10 and 10

        sphere.position = new Vector3(sx, sy, sz);
        
        // Update Bounds (Crucial for rendering!)
        // Bounds must cover the object AND the blend radius
        float sphereBoundsSize = 20.0f; 
        sphere.boundsMin = sphere.position - Vector3.one * sphereBoundsSize;
        sphere.boundsMax = sphere.position + Vector3.one * sphereBoundsSize;
        
        DynamicSDFManager.Instance.UpdateObject(0, sphere);

        // --- Animate Cube ---
        // Rotates opposite side, spinning on its own axis
        var cube = DynamicSDFManager.Instance.GetObject(1);
        
        float cx = Mathf.Cos(t + Mathf.PI) * orbitRadius; // +PI to be on opposite side
        float cz = Mathf.Sin(t + Mathf.PI) * orbitRadius;
        
        cube.position = new Vector3(cx, heightOffset, cz);
        cube.rotation = Quaternion.Euler(t * 50f, t * 30f, 0f);
        
        float cubeBoundsSize = 25.0f; // Larger bounds for the rotating cube corners
        cube.boundsMin = cube.position - Vector3.one * cubeBoundsSize;
        cube.boundsMax = cube.position + Vector3.one * cubeBoundsSize;
        
        DynamicSDFManager.Instance.UpdateObject(1, cube);
    }
}