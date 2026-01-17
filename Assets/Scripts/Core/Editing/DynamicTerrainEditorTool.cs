using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using VoxelEngine.Core;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Generators;
using VoxelEngine.Core.Rendering;

namespace VoxelEngine.Core.Editing
{
    public class DynamicTerrainEditorTool : MonoBehaviour
    {
        public enum ToolMode
        {
            Paint = 0,   // Union (Add Material)
            Erase = 1,   // Subtract (Dig Hole)
            Delete = 2   // Remove Object (Delete Entity)
        }

        [Header("Tool Settings")]
        public ToolMode mode = ToolMode.Paint;
        
        [Tooltip("0 = Sphere, 1 = Cube")]
        public int brushShape = 0; 
        
        public float brushRadius = 2.0f;
        public int brushMaterial = 1;
        
        [Tooltip("Smoothness of the blend with the terrain.")]
        [Range(0.1f, 10.0f)]
        public float blendSmoothness = 2.0f;

        [Header("Timing")]
        [Tooltip("Seconds between edits while holding click.")]
        public float editRate = 0.1f;

        private InputSystem_Actions _input;
        private Vector3 _currentHitPoint;
        private bool _hasHit;
        private float _lastEditTime;

        // Async Request
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
            // 1. Sync Mouse Position for Raytracer
            Vector2 mousePos = Mouse.current.position.ReadValue();
            VoxelRaytracerFeature.MousePosition = mousePos;

            // 2. GPU Readback
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
                    ExecuteTool();
                    _lastEditTime = Time.time;
                }
            }
            
            // Mode Switching shortcuts (optional)
            if (Keyboard.current.pKey.wasPressedThisFrame) mode = ToolMode.Paint;
            if (Keyboard.current.eKey.wasPressedThisFrame) mode = ToolMode.Erase;
            if (Keyboard.current.deleteKey.wasPressedThisFrame) mode = ToolMode.Delete;
        }

        private void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            _readbackPending = false;
            if (request.hasError) return;

            var data = request.GetData<Vector4>();
            if (data.Length == 0) return;
            Vector4 hitData = data[0]; 
            
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

        private void ExecuteTool()
        {
            if (DynamicSDFManager.Instance == null) return;

            switch (mode)
            {
                case ToolMode.Paint:
                    SpawnDynamicSDF(0); // 0 = Union
                    break;

                case ToolMode.Erase:
                    SpawnDynamicSDF(1); // 1 = Subtract
                    break;

                case ToolMode.Delete:
                    RemoveClosestObject();
                    break;
            }
        }

        private void SpawnDynamicSDF(int operation)
        {
            float scaleValue = brushRadius * 2.0f;
            float boundPadding = blendSmoothness + 2.0f;
            float totalBoundRadius = brushRadius + boundPadding;

            SDFObject newObj = new SDFObject
            {
                position = _currentHitPoint,
                rotation = Quaternion.identity,
                scale = Vector3.one * scaleValue,
                
                boundsMin = _currentHitPoint - Vector3.one * totalBoundRadius,
                boundsMax = _currentHitPoint + Vector3.one * totalBoundRadius,
                
                type = brushShape,
                operation = operation,
                blendFactor = blendSmoothness,
                materialId = brushMaterial
            };

            DynamicSDFManager.Instance.RegisterObject(newObj);
        }

        private void RemoveClosestObject()
        {
            // Find object near cursor
            int index = DynamicSDFManager.Instance.FindClosestObject(_currentHitPoint, brushRadius * 1.5f);
            
            if (index != -1)
            {
                DynamicSDFManager.Instance.RemoveObjectAt(index);
                Debug.Log($"[Dynamic Editor] Deleted Object Index {index}");
            }
        }

        private void OnDrawGizmos()
        {
            if (_hasHit)
            {
                // Visual feedback changes based on mode
                if (mode == ToolMode.Delete)
                {
                    Gizmos.color = Color.red;
                    Gizmos.DrawWireSphere(_currentHitPoint, brushRadius * 1.5f);
                    Gizmos.DrawLine(_currentHitPoint - Vector3.right, _currentHitPoint + Vector3.right);
                    Gizmos.DrawLine(_currentHitPoint - Vector3.up, _currentHitPoint + Vector3.up);
                }
                else
                {
                    Gizmos.color = mode == ToolMode.Erase ? new Color(1, 0.5f, 0, 1) : Color.cyan;
                    
                    if (brushShape == 0)
                        Gizmos.DrawWireSphere(_currentHitPoint, brushRadius);
                    else
                        Gizmos.DrawWireCube(_currentHitPoint, Vector3.one * brushRadius * 2.0f);
                        
                    Gizmos.color = new Color(0, 1, 1, 0.3f);
                    Gizmos.DrawWireSphere(_currentHitPoint, brushRadius + blendSmoothness);
                }
            }
        }
    }
}