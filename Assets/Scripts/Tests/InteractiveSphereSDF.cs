using UnityEngine;
using VoxelEngine.Core.Generators; // For DynamicSDFManager
using VoxelEngine.Core.Data;       // For SDFObject struct

namespace VoxelEngine.Core.Testing
{
    [ExecuteAlways] // Allows updating in Editor Mode (if Manager is running) or Play Mode
    public class InteractiveSphereSDF : MonoBehaviour
    {
        [Header("Shape Settings")]
        [Tooltip("The radius of the sphere in world units.")]
        public float radius = 5.0f;

        [Tooltip("How smoothly the sphere blends with the terrain. Higher = Goopier/Softer.")]
        [Range(0.1f, 20.0f)]
        public float blendSmoothness = 5.0f;

        [Tooltip("The Material ID to apply (see VoxelDefinitionManager).")]
        public int materialID = 3;

        [Header("Debug")]
        public int objectIndex = -1;

        private void OnEnable()
        {
            // Wait for Manager to exist
            if (DynamicSDFManager.Instance == null) return;

            RegisterSphere();
        }

        private void OnDisable()
        {
            // Note: In a robust system, we would remove the object here. 
            // However, DynamicSDFManager.cs currently only supports ClearObjects().
            // For testing purposes, we simply stop updating.
        }

        private void Update()
        {
            if (DynamicSDFManager.Instance == null) return;

            // If we haven't registered yet (e.g. Manager initialized after this script), try again
            if (objectIndex == -1 || objectIndex >= DynamicSDFManager.Instance.ObjectCount)
            {
                // Simple re-registration check
                RegisterSphere();
            }

            UpdateSphere();
        }

        private void RegisterSphere()
        {
            // We assume we are appending to the end of the list.
            // In a production environment, DynamicSDFManager should return a unique ID.
            objectIndex = DynamicSDFManager.Instance.ObjectCount;
            
            SDFObject initialData = CreateSDFData();
            DynamicSDFManager.Instance.RegisterObject(initialData);
        }

        private void UpdateSphere()
        {
            if (objectIndex != -1)
            {
                SDFObject data = CreateSDFData();
                DynamicSDFManager.Instance.UpdateObject(objectIndex, data);
            }
        }

        private SDFObject CreateSDFData()
        {
            // 1. Calculate Scale
            // In GeneratorPipeline.hlsl: d = (length(p) - 0.5) * scale;
            // Therefore, Scale = Radius * 2.
            float scaleValue = radius * 2.0f;
            
            // 2. Calculate Bounds
            // Important: Bounds must include the object size PLUS the blend factor.
            // If bounds are too small, the smooth blending "glow" will be clipped.
            float boundsPadding = blendSmoothness + 2.0f; 
            float boundRadius = radius + boundsPadding;

            Vector3 pos = transform.position;

            SDFObject obj = new SDFObject
            {
                position = pos,
                rotation = transform.rotation,
                scale = Vector3.one * scaleValue,
                
                // Bounds determine where the GPU calculations run
                boundsMin = pos - Vector3.one * boundRadius,
                boundsMax = pos + Vector3.one * boundRadius,
                
                type = 0,         // 0 = Sphere (defined in VoxelData.cs / GeneratorPipeline.hlsl)
                operation = 0,    // 0 = Union (triggers UnionSmooth in GeneratorPipeline.hlsl)
                blendFactor = blendSmoothness, 
                materialId = materialID
            };

            return obj;
        }
    }
}