using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using VoxelEngine.Core;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Generators; // Required for DynamicSDFManager
using VoxelEngine.Core.Rendering;

namespace VoxelEngine.Core.Editing
{
    /// <summary>
    /// A "Fake" Terrain Editor that places Dynamic SDF Objects (Spheres/Cubes) 
    /// instead of modifying the underlying voxel volume memory.
    /// </summary>
    public class DynamicTerrainEditorTool : MonoBehaviour
    {
        [Header("Brush Configuration")]
        public float brushRadius = 2.0f;
        public int brushMaterial = 1;
        
        [Tooltip("0 = Sphere, 1 = Cube")]
        public int brushShape = 0; 
        
        [Tooltip("Smoothness of the blend with the terrain.")]
        [Range(0.1f, 10.0f)]
        public float blendSmoothness = 2.0f;

        [Tooltip("Minimum seconds between placing objects while holding click.")]
        public float editRate = 0.1f;

        private InputSystem_Actions _input;
        private Vector3 _currentHitPoint;
        private bool _hasHit;
        private float _lastEditTime;

        // Async Request for GPU Raycast results
        private AsyncGPUReadbackRequest _readbackRequest;
        private bool _readbackPending;

        private void Awake()
        {
            _input = new InputSystem_Actions();
        }

        private void OnEnable()
        {
            _input.Player.Attack.Enable();
        }

        private void OnDisable()
        {
            _input.Player.Attack.Disable();
        }

        private void Update()
        {
            // 1. Synchronize Mouse Position for the Voxel Raytracer
            // The raytracer needs to know where the cursor is to output the specific hit depth/position to the buffer.
            Vector2 mousePos = Mouse.current.position.ReadValue();
            VoxelRaytracerFeature.MousePosition = mousePos;

            // 2. Request Readback of Hit Data from GPU
            if (!_readbackPending && VoxelRaytracerFeature.RaycastHitBuffer != null)
            {
                _readbackRequest = AsyncGPUReadback.Request(VoxelRaytracerFeature.RaycastHitBuffer, OnReadbackComplete);
                _readbackPending = true;
            }

            // 3. Handle Input
            if (_input.Player.Attack.IsPressed())
            {
                if (Time.time - _lastEditTime > editRate && _hasHit)
                {
                    // Check for shift key to subtract (if desired), otherwise Add
                    // For simplicity, we default to Union (Add) here.
                    // To implement subtract, we would check keyboard state.
                    bool isSubtract = Keyboard.current.shiftKey.isPressed;
                    
                    SpawnDynamicSDF(isSubtract ? 1 : 0); // 0=Union, 1=Subtract
                    _lastEditTime = Time.time;
                }
            }
        }

        private void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            _readbackPending = false;
            if (request.hasError) return;

            // The raytracer shader writes: float4(hitPos.x, hitPos.y, hitPos.z, hitFlag)
            var data = request.GetData<Vector4>();
            if (data.Length == 0) return;

            Vector4 hitData = data[0]; 
            
            // w > 0.5 indicates a valid hit
            if (hitData.w > 0.5f)
            {
                _currentHitPoint = new Vector3(hitData.x, hitData.y, hitData.z);
                _hasHit = true;
            }
            else
            {
                _hasHit = false;
            }
        }

        private void SpawnDynamicSDF(int operation)
        {
            if (DynamicSDFManager.Instance == null) return;

            // 1. Calculate Dimensions
            // The shader logic for SDF primitives usually expects Scale to represent the full diameter (2 * Radius)
            // for correct distance calculation: d = (length(p) - 0.5) * scale
            float scaleValue = brushRadius * 2.0f;

            // 2. Calculate Bounds
            // Crucial: The bounds must include the object size PLUS the blend smoothness.
            // If the bounds are too tight, the smooth blending "glow" will be clipped by the BVH.
            float boundPadding = blendSmoothness + 2.0f;
            float totalBoundRadius = brushRadius + boundPadding;

            // 3. Construct the Object
            SDFObject newObj = new SDFObject
            {
                position = _currentHitPoint,
                rotation = Quaternion.identity, // Axis aligned for now
                scale = Vector3.one * scaleValue,
                
                // Define the bounding box for the BVH
                boundsMin = _currentHitPoint - Vector3.one * totalBoundRadius,
                boundsMax = _currentHitPoint + Vector3.one * totalBoundRadius,
                
                type = brushShape,    // 0 = Sphere, 1 = Cube
                operation = operation,// 0 = Union, 1 = Subtract
                blendFactor = blendSmoothness,
                materialId = brushMaterial
            };

            // 4. Register with Manager
            // This triggers a Dirty Region add and a BVH Rebuild automatically
            DynamicSDFManager.Instance.RegisterObject(newObj);
            
            Debug.Log($"Placed Dynamic SDF at {_currentHitPoint}. Total Objects: {DynamicSDFManager.Instance.ObjectCount}");
        }

        private void OnDrawGizmos()
        {
            if (_hasHit)
            {
                // Visualize the brush cursor
                Gizmos.color = Color.cyan;
                if (brushShape == 0)
                    Gizmos.DrawWireSphere(_currentHitPoint, brushRadius);
                else
                    Gizmos.DrawWireCube(_currentHitPoint, Vector3.one * brushRadius * 2.0f);
                    
                // Visualize the blend influence area
                Gizmos.color = new Color(0, 1, 1, 0.3f);
                float influence = brushRadius + blendSmoothness;
                Gizmos.DrawWireSphere(_currentHitPoint, influence);
            }
        }
    }
}