using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using VoxelEngine.Core;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Rendering;

namespace VoxelEngine.Core.Editing
{
    public class TerrainEditorTool : MonoBehaviour
    {
        [Header("Configuration")]
        public ComputeShader voxelModifierShader;
        public float brushRadius = 2.0f;
        public int brushMaterial = 1;
        public float editRate = 0.1f; // Seconds between edits

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
            // 1. Update Mouse Position for Raytracer
            Vector2 mousePos = Mouse.current.position.ReadValue();
            VoxelRaytracerFeature.MousePosition = mousePos;

            // 2. Request Readback of Hit Data
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
                    ApplyBrush(BrushOp.Add); // Or Subtract based on modifier key
                    _lastEditTime = Time.time;
                }
            }
        }

        private void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            _readbackPending = false;
            if (request.hasError) return;

            var data = request.GetData<Vector4>();
            Vector4 hitData = data[0]; // [x, y, z, hitFlag]
            
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

        private void ApplyBrush(BrushOp op)
        {
            if (voxelModifierShader == null) return;

            // Define Brush
            VoxelBrush brush = new VoxelBrush
            {
                position = _currentHitPoint,
                radius = brushRadius,
                materialId = brushMaterial,
                shape = (int)BrushShape.Sphere,
                op = (int)op
            };
            brush.bounds = Vector3.one * brushRadius * 2;

            // Find Intersecting Volumes
            Bounds brushBounds = new Bounds(brush.position, brush.bounds);
            
            foreach (var volume in VoxelVolumeRegistry.Volumes)
            {
                if (!volume.gameObject.activeInHierarchy) continue;
                if (volume.WorldBounds.Intersects(brushBounds))
                {
                    // Convert World Brush to Local Volume Space if needed
                    // For now, VoxelModifier assumes World Space or matches Volume Space
                    // We pass the raw brush.
                    
                    VoxelModifier modifier = new VoxelModifier(voxelModifierShader, volume);
                    // VoxelModifier now handles the World-to-Local conversion internally.
                    modifier.Apply(brush, volume.Resolution);
                }
            }
        }

        private void OnDrawGizmos()
        {
            if (_hasHit)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireSphere(_currentHitPoint, brushRadius);
            }
        }
    }
}