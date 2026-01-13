using UnityEngine;
using VoxelEngine.Core.Generators; // For DynamicSDFManager
using VoxelEngine.Core.Data;       // For SDFObject, SDFShapeDefinition

namespace VoxelEngine.Core.Testing
{
    [ExecuteAlways]
    public class InteractiveBunnySDF : MonoBehaviour
    {
        [Header("Shape Asset")]
        [Tooltip("The definition containing the baked Bunny SDF Texture. Must be registered in SDFShapeManager.")]
        public SDFShapeDefinition shapeDefinition;

        [Header("Settings")]
        [Tooltip("How smoothly the bunny blends with the terrain.")]
        [Range(0.1f, 10.0f)]
        public float blendSmoothness = 1.0f;

        [Tooltip("The Material ID to apply.")]
        public int materialID = 2;

        [Tooltip("Operation: 0=Union, 1=Subtract")]
        public int operation = 0;

        [Header("Debug")]
        public int objectIndex = -1;
        public int atlasIndex = -1;

        // --- Change Tracking ---
        private Vector3 _lastPosition;
        private Quaternion _lastRotation;
        private Vector3 _lastScale;
        private float _lastBlendSmoothness;
        private int _lastMaterialID;
        private int _lastOperation;
        private SDFShapeDefinition _lastDefinition;
        private bool _isInitialized = false;

        private void OnEnable()
        {
            if (DynamicSDFManager.Instance == null) return;
            RegisterBunny();
        }

        private void OnDisable()
        {
            // Currently DynamicSDFManager does not support removing single objects efficiently
        }

        private void Update()
        {
            if (DynamicSDFManager.Instance == null) return;

            // Retry registration if failed previously (e.g. Manager wasn't ready)
            if (objectIndex == -1 || objectIndex >= DynamicSDFManager.Instance.ObjectCount)
            {
                RegisterBunny();
            }

            if (HasChanged())
            {
                UpdateBunny();
                UpdateCache();
            }
        }

        private bool HasChanged()
        {
            if (!_isInitialized) return true;

            // Check Transform
            if (transform.position != _lastPosition) return true;
            if (transform.rotation != _lastRotation) return true;
            if (transform.localScale != _lastScale) return true;

            // Check Properties
            if (!Mathf.Approximately(blendSmoothness, _lastBlendSmoothness)) return true;
            if (materialID != _lastMaterialID) return true;
            if (operation != _lastOperation) return true;
            if (shapeDefinition != _lastDefinition) return true;

            return false;
        }

        private void UpdateCache()
        {
            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
            _lastScale = transform.localScale;
            _lastBlendSmoothness = blendSmoothness;
            _lastMaterialID = materialID;
            _lastOperation = operation;
            _lastDefinition = shapeDefinition;
            _isInitialized = true;
        }

        private void RegisterBunny()
        {
            // We append to the manager's list
            objectIndex = DynamicSDFManager.Instance.ObjectCount;
            
            SDFObject initialData = CreateSDFData();
            DynamicSDFManager.Instance.RegisterObject(initialData); //
            
            UpdateCache(); 
        }

        private void UpdateBunny()
        {
            if (objectIndex != -1)
            {
                SDFObject data = CreateSDFData();
                DynamicSDFManager.Instance.UpdateObject(objectIndex, data); //
            }
        }

        private SDFObject CreateSDFData()
        {
            // 1. Resolve Texture Index
            // We must find where this shape is packed in the global atlas
            int texIndex = 0;
            if (SDFShapeManager.Instance != null && shapeDefinition != null)
            {
                texIndex = SDFShapeManager.Instance.GetShapeIndex(shapeDefinition); //
            }
            atlasIndex = texIndex;

            // 2. Calculate Bounds
            // Mesh SDFs are usually normalized to fit in a 1x1x1 box (-0.5 to 0.5).
            // We apply the transform's scale to this box.
            Vector3 worldSize = transform.lossyScale;
            float maxDimension = Mathf.Max(worldSize.x, Mathf.Max(worldSize.y, worldSize.z));
            
            // Add padding for the smooth blend (SDF needs to be evaluated slightly outside the object)
            float boundsPadding = blendSmoothness + 1.0f;
            Vector3 paddingVec = new Vector3(boundsPadding, boundsPadding, boundsPadding);

            Vector3 center = transform.position;
            Vector3 extent = worldSize * 0.5f;

            SDFObject obj = new SDFObject
            {
                position = center,
                rotation = transform.rotation,
                scale = transform.lossyScale, 

                // Bounds: Center +/- (Extent + Padding)
                boundsMin = center - (extent + paddingVec), //
                boundsMax = center + (extent + paddingVec),
                
                type = 2,           // 2 = Mesh/Texture3D
                operation = operation,
                blendFactor = blendSmoothness, 
                materialId = materialID,
                textureIndex = texIndex //
            };

            return obj;
        }
    }
}