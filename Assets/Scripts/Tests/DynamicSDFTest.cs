using UnityEngine;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Generators;

public class DynamicSDFTest : MonoBehaviour
{
    [Header("Animation Settings")]
    public bool animate = true;
    public float orbitRadius = 30.0f;
    public float rotationSpeed = 1.0f;
    public float heightOffset = 0.0f; 

    private void Start()
    {
        if (DynamicSDFManager.Instance == null) return;

        DynamicSDFManager.Instance.ClearObjects();

        // 1. Register a Sphere (Union)
        float sphereBoundsSize = 20.0f;
        Vector3 spherePos = new Vector3(orbitRadius, heightOffset, 0);
        
        SDFObject sphere = new SDFObject
        {
            position = spherePos,
            rotation = Quaternion.identity,
            scale = Vector3.one * 1.5f,
            // Fix: Initialize valid bounds so initial SVO generation sees it!
            boundsMin = spherePos - Vector3.one * sphereBoundsSize,
            boundsMax = spherePos + Vector3.one * sphereBoundsSize,
            type = 0, // Sphere
            operation = 0, // Union
            blendFactor = 8.0f, 
            materialId = 3 
        };
        DynamicSDFManager.Instance.RegisterObject(sphere);

        // 2. Register a Cube (Subtraction)
        float cubeBoundsSize = 25.0f;
        Vector3 cubePos = new Vector3(-orbitRadius, heightOffset, 0);

        SDFObject cube = new SDFObject
        {
            position = cubePos,
            rotation = Quaternion.identity,
            scale = Vector3.one,
            // Fix: Initialize valid bounds
            boundsMin = cubePos - Vector3.one * cubeBoundsSize,
            boundsMax = cubePos + Vector3.one * cubeBoundsSize,
            type = 1, // Cube
            operation = 1, // Subtract
            blendFactor = 3.0f,
            materialId = 0 
        };
        DynamicSDFManager.Instance.RegisterObject(cube);
    }

    private void Update()
    {
        if (!animate || DynamicSDFManager.Instance == null) return;

        float t = Time.time * rotationSpeed;

        // --- Animate Sphere ---
        var sphere = DynamicSDFManager.Instance.GetObject(0);
        
        float sx = Mathf.Cos(t) * orbitRadius;
        float sz = Mathf.Sin(t) * orbitRadius;
        float sy = heightOffset + Mathf.Sin(t * 2.5f) * 10.0f; 

        sphere.position = new Vector3(sx, sy, sz);
        
        float sphereBoundsSize = 20.0f; 
        sphere.boundsMin = sphere.position - Vector3.one * sphereBoundsSize;
        sphere.boundsMax = sphere.position + Vector3.one * sphereBoundsSize;
        
        DynamicSDFManager.Instance.UpdateObject(0, sphere);

        // --- Animate Cube ---
        var cube = DynamicSDFManager.Instance.GetObject(1);
        
        float cx = Mathf.Cos(t + Mathf.PI) * orbitRadius; 
        float cz = Mathf.Sin(t + Mathf.PI) * orbitRadius;
        
        cube.position = new Vector3(cx, heightOffset, cz);
        cube.rotation = Quaternion.Euler(t * 50f, t * 30f, 0f);
        
        float cubeBoundsSize = 25.0f; 
        cube.boundsMin = cube.position - Vector3.one * cubeBoundsSize;
        cube.boundsMax = cube.position + Vector3.one * cubeBoundsSize;
        
        DynamicSDFManager.Instance.UpdateObject(1, cube);
    }
}