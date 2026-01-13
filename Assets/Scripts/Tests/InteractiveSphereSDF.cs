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

        // --- OPTIMIZATION: Change Tracking ---
        private Vector3 _lastPosition;
        private Quaternion _lastRotation;
        private float _lastRadius;
        private float _lastBlendSmoothness;
        private int _lastMaterialID;
        private bool _isInitialized = false;

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
                RegisterSphere();
            }

            // OPTIMIZATION: Only update the manager if the object has actually changed.
            // This prevents the WorldManager from constantly regenerating chunks (Red Debug Boxes).
            if (HasChanged())
            {
                UpdateSphere();
                UpdateCache();
            }
        }

        private bool HasChanged()
        {
            if (!_isInitialized) return true;

            // Check Transform
            if (transform.position != _lastPosition) return true;
            if (transform.rotation != _lastRotation) return true; // Unity's Quaternion != handles epsilon comparison

            // Check Properties
            if (!Mathf.Approximately(radius, _lastRadius)) return true;
            if (!Mathf.Approximately(blendSmoothness, _lastBlendSmoothness)) return true;
            if (materialID != _lastMaterialID) return true;

            return false;
        }

        private void UpdateCache()
        {
            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
            _lastRadius = radius;
            _lastBlendSmoothness = blendSmoothness;
            _lastMaterialID = materialID;
            _isInitialized = true;
        }

        private void RegisterSphere()
        {
            // We assume we are appending to the end of the list.
            // In a production environment, DynamicSDFManager should return a unique ID.
            objectIndex = DynamicSDFManager.Instance.ObjectCount;
            
            SDFObject initialData = CreateSDFData();
            DynamicSDFManager.Instance.RegisterObject(initialData);
            
            UpdateCache(); // Ensure we don't immediately trigger an update next frame
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