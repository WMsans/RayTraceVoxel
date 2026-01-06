using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Editing;
using VoxelEngine.Core.Interfaces;

public class VoxelEditor : MonoBehaviour
{
    [Header("Brush Settings")]
    public BrushShape brushShape = BrushShape.Sphere;
    public BrushOp brushOp = BrushOp.Subtract;
    public float brushRadius = 5.0f;
    public Vector3 brushBounds = new Vector3(8, 8, 8); // For Cube/Plane
    public int selectedMaterialId = 1;

    [Header("References")]
    public ComputeShader svoEditorCompute;
    public VoxelVolume targetVolume; // Was SVOManager
    public Camera mainCamera;

    [Header("Debug")]
    public bool isHitting;
    public Vector3 lastHitPoint;
    public Vector3Int minBrickId;
    public Vector3Int maxBrickId;

    // Buffers
    private GraphicsBuffer _affectedNodeBuffer; 
    private GraphicsBuffer _countBuffer; 
    private InputSystem_Actions playerControls;
    private VoxelModifier _modifier;

    private const int MAX_AFFECTED_NODES = 1024; 

    private void Awake()
    {
        playerControls = new InputSystem_Actions();
        playerControls.Player.Attack.performed += _ => OnAttack();
    }

    private void OnEnable()
    {
        playerControls.Player.Enable();
    }

    private void OnDisable()
    {
        playerControls.Player.Disable();
    }

    private void Start()
    {
        if (mainCamera == null) mainCamera = Camera.main;
        
        // Auto-find if not assigned (Support single volume for now)
        if (targetVolume == null) targetVolume = FindFirstObjectByType<VoxelVolume>();

        _affectedNodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.Append, MAX_AFFECTED_NODES, sizeof(uint));
        _countBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments, 1, sizeof(uint));
        
        if (targetVolume != null)
        {
            _modifier = new VoxelModifier(svoEditorCompute, targetVolume);
        }
    }

    private void OnDestroy()
    {
        _affectedNodeBuffer?.Release();
        _countBuffer?.Release();
    }

    private void Update()
    {
        if (targetVolume == null && _modifier == null)
        {
             targetVolume = FindFirstObjectByType<VoxelVolume>();
             if (targetVolume != null) _modifier = new VoxelModifier(svoEditorCompute, targetVolume);
        }

        PerformRaycast();
    }

    private void OnAttack()
    {
        if (isHitting)
        {
            ApplyEdit();
        }
    }

    private void PerformRaycast()
    {
        if (mainCamera == null) return;

        // Use GPU Result from VoxelRaytracer
        var buffer = VoxelRaytracerFeature.RaycastHitBuffer;
        if (buffer != null && buffer.IsValid())
        {
            // ASYNC READBACK
            AsyncGPUReadback.Request(buffer, (request) =>
            {
                if (request.hasError) return;

                using (var data = request.GetData<Vector4>())
                {
                    if (data.Length > 0)
                    {
                        Vector4 result = data[0];
                         // w component holds hit flag (1.0 = hit, 0.0 = miss)
                        if (result.w > 0.5f)
                        {
                            isHitting = true;
                            lastHitPoint = (Vector3)result;
                        }
                        else
                        {
                            isHitting = false;
                        }
                    }
                }
            });
        }
        else
        {
            isHitting = false;
        }
    }

    public void ApplyEdit()
    {
        if (targetVolume == null || !targetVolume.IsReady) return;
        if (_modifier == null) _modifier = new VoxelModifier(svoEditorCompute, targetVolume);

        VoxelBrush brush = new VoxelBrush();
        brush.position = lastHitPoint;
        brush.bounds = brushBounds;
        brush.radius = brushRadius;
        brush.materialId = selectedMaterialId;
        brush.shape = (int)brushShape;
        brush.op = (int)brushOp;

        _modifier.Apply(brush, targetVolume.Resolution);
    }

    private Bounds GetBrushAABB(Vector3 center)
    {
        Bounds b = new Bounds(center, Vector3.zero);
        if (brushShape == BrushShape.Sphere)
        {
            b.extents = new Vector3(brushRadius, brushRadius, brushRadius);
        }
        else // Cube/Plane
        {
            b.extents = brushBounds * 0.5f;
        }
        return b;
    }

    private void OnDrawGizmos()
    {
        if (isHitting)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(lastHitPoint, 0.5f);

            Gizmos.color = new Color(1, 0, 0, 0.3f);
            Bounds b = GetBrushAABB(lastHitPoint);
            Gizmos.DrawWireCube(b.center, b.size);
        }
    }
}
