using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using VoxelEngine.Core;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Rendering;
using VoxelEngine.Core.Streaming;
using VoxelEngine.Core.Effects;
using System.Collections.Generic;

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
        private int _currentMaterialId;
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
            VoxelRaytraceFeature.MousePosition = mousePos;

            // Request Readback of Hit Data from Raytracer
            if (!_readbackPending && VoxelRaytraceFeature.RaycastHitBuffer != null)
            {
                _readbackRequest = AsyncGPUReadback.Request(VoxelRaytraceFeature.RaycastHitBuffer, OnReadbackComplete);
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
                _currentMaterialId = (int)data[1].y;
                _hasHit = true;
            }
            else
            {
                _hasHit = false;
                _currentHitVolumeIndex = -1;
                _currentMaterialId = 0;
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

            // Identify the Primary Hit Volume to determine Context (World vs. Debris)
            VoxelVolume hitVolume = VoxelVolumePool.Instance.VisibleVolumes[_currentHitVolumeIndex];

            // Step 1: Define World-Space Brush Bounds
            Bounds brushBounds = new Bounds(_currentHitPoint, Vector3.one * brushRadius * 2.0f);

            // Step 2 & 3: Broad Phase - Query and Filter Intersecting Volumes
            List<VoxelVolume> volumesToEdit = new List<VoxelVolume>();

            if (hitVolume.IsTransient)
            {
                // Context: Debris
                // If we hit a dynamic object, we ONLY want to edit that object. 
                // We shouldn't accidentally sculpt the terrain behind it or other rocks nearby.
                volumesToEdit.Add(hitVolume);
            }
            else
            {
                // Context: World Terrain
                // We want seamless editing across chunk boundaries.
                // Iterate ALL active volumes (not just visible ones, to ensure boundary correctness)
                foreach (var vol in VoxelVolumeRegistry.Volumes)
                {
                    // Filter: Must be active, ready, and NOT a transient debris object
                    if (vol.gameObject.activeInHierarchy && vol.IsReady && !vol.IsTransient)
                    {
                        if (vol.WorldBounds.Intersects(brushBounds))
                        {
                            volumesToEdit.Add(vol);
                        }
                    }
                }
            }

            // Step 4: Iterative Application (Narrow Phase)
            VoxelBrush brush = new VoxelBrush
            {
                position = _currentHitPoint,
                radius = brushRadius,
                materialId = brushMaterial,
                shape = (int)BrushShape.Sphere,
                op = (int)op,
                bounds = Vector3.one * brushRadius * 2
            };

            foreach (var vol in volumesToEdit)
            {
                // The VoxelModifier handles transforming the world-space brush into local volume space
                VoxelModifier modifier = new VoxelModifier(voxelModifierShader, vol);
                modifier.Apply(brush, vol.Resolution);
            }

            // Spawn debris particles on subtract
            if (op == BrushOp.Subtract && VoxelVFXManager.Instance != null)
            {
                VoxelVFXManager.Instance.SpawnDebris(_currentHitPoint, brushRadius, _currentMaterialId);
            }

            // Phase 3 & 4: Recursive Fracturing Pipeline & Sleep Thresholds
            if (op == BrushOp.Subtract && structuralAnalyzer != null)
            {
                if (hitVolume.IsTransient)
                {
                    // Phase 4: Sleep Thresholds
                    // Only run recursive analysis if the debris is "Awake" (active in physics)
                    Rigidbody rb = hitVolume.GetComponent<Rigidbody>();
                    bool isAwake = rb == null || !rb.IsSleeping();
                    
                    // Also consider "significant" edits (large brush) to wake it up if needed
                    bool significantEdit = brushRadius > 1.0f;

                    if (isAwake || significantEdit)
                    {
                        if (rb != null && rb.IsSleeping()) rb.WakeUp();
                        structuralAnalyzer.AnalyzeVolume(hitVolume, brushBounds);
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