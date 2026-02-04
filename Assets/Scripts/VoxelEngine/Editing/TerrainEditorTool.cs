using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using VoxelEngine.Core;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Rendering;
using VoxelEngine.Core.Streaming;

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
        private int _currentHitVolumeIndex = -1;
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
            Vector4 hitPosData = data[0]; 
            
            if (hitPosData.w > 0.5f)
            {
                _currentHitPoint = new Vector3(hitPosData.x, hitPosData.y, hitPosData.z);
                _currentHitVolumeIndex = (int)data[1].x;
                _hasHit = true;
            }
            else
            {
                _hasHit = false;
                _currentHitVolumeIndex = -1;
            }
        }

        private void ApplyBrush(BrushOp op)
        {
            if (voxelModifierShader == null || _currentHitVolumeIndex < 0) return;
            if (VoxelEditManager.Instance == null)
            {
                Debug.LogWarning("VoxelEditManager is missing. Edits will not be saved.");
            }

            if (VoxelVolumePool.Instance == null || _currentHitVolumeIndex >= VoxelVolumePool.Instance.VisibleVolumes.Count) return;

            VoxelVolume targetVolume = VoxelVolumePool.Instance.VisibleVolumes[_currentHitVolumeIndex];

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
            
            VoxelModifier modifier = new VoxelModifier(voxelModifierShader, targetVolume);
            modifier.Apply(brush, targetVolume.Resolution);

            // Phase 3 & 4: Recursive Fracturing Pipeline & Sleep Thresholds
            if (op == BrushOp.Subtract && structuralAnalyzer != null)
            {
                if (targetVolume.IsTransient)
                {
                    // Phase 4: Sleep Thresholds
                    // Only run recursive analysis if the debris is "Awake" (active in physics)
                    Rigidbody rb = targetVolume.GetComponent<Rigidbody>();
                    bool isAwake = rb == null || !rb.IsSleeping();
                    
                    // Also consider "significant" edits (large brush) to wake it up if needed
                    bool significantEdit = brushRadius > 1.0f;

                    if (isAwake || significantEdit)
                    {
                        if (rb != null && rb.IsSleeping()) rb.WakeUp();
                        structuralAnalyzer.AnalyzeVolume(targetVolume, brushBounds);
                    }
                }
                else
                {
                    // Standard analysis for world terrain (always active)
                    structuralAnalyzer.AnalyzeWorld(brushBounds);
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