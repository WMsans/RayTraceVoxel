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
        public float editRate = 0.1f; 
        public BrushOp editMode = BrushOp.Add;
        
        public StructuralIntegrityAnalyzer structuralAnalyzer;

        private InputSystem_Actions _input;
        private Vector3 _currentHitPoint;
        private bool _hasHit;
        private float _lastEditTime;

        // Async Request for Raycast Hit
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
            // Sync Mouse Position for Raytracer
            Vector2 mousePos = Mouse.current.position.ReadValue();
            VoxelRaytracerFeature.MousePosition = mousePos;

            // Request Readback of Hit Data from Raytracer
            if (!_readbackPending && VoxelRaytracerFeature.RaycastHitBuffer != null)
            {
                _readbackRequest = AsyncGPUReadback.Request(VoxelRaytracerFeature.RaycastHitBuffer, OnReadbackComplete);
                _readbackPending = true;
            }

            // Handle Input
            if (_input.Player.Attack.IsPressed())
            {
                if (Time.time - _lastEditTime > editRate && _hasHit)
                {
                    ApplyBrush(editMode);
                    _lastEditTime = Time.time;
                }
            }
        }

        private void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            _readbackPending = false;
            if (request.hasError) return;

            var data = request.GetData<Vector4>();
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

        private void ApplyBrush(BrushOp op)
        {
            if (voxelModifierShader == null) return;
            if (VoxelEditManager.Instance == null)
            {
                Debug.LogWarning("VoxelEditManager is missing. Edits will not be saved.");
            }

            VoxelBrush brush = new VoxelBrush
            {
                position = _currentHitPoint,
                radius = brushRadius,
                materialId = brushMaterial,
                shape = (int)BrushShape.Sphere,
                op = (int)op
            };
            brush.bounds = Vector3.one * brushRadius * 2;
            Bounds brushBounds = new Bounds(brush.position, brush.bounds);
            
            foreach (var volume in VoxelVolumeRegistry.Volumes)
            {
                if (!volume.gameObject.activeInHierarchy) continue;
                if (volume.WorldBounds.Intersects(brushBounds))
                {
                    VoxelModifier modifier = new VoxelModifier(voxelModifierShader, volume);
                    // This call now triggers the GPU edit AND the async readback
                    modifier.Apply(brush, volume.Resolution);

                    // Phase 1: Structural Integrity Analysis
                    if (op == BrushOp.Subtract && structuralAnalyzer != null)
                    {
                        structuralAnalyzer.AnalyzeWorld(brushBounds);
                    }
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