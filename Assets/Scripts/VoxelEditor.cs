using UnityEngine;
using UnityEngine.InputSystem;

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
    public SVOManager svoManager;
    public Camera mainCamera;

    [Header("Debug")]
    public bool isHitting;
    public Vector3 lastHitPoint;
    public Vector3Int minBrickId;
    public Vector3Int maxBrickId;

    // Buffers
    private GraphicsBuffer _affectedNodeBuffer; // Stores indices of nodes to update
    private GraphicsBuffer _argBuffer; // For DrawProcedural/Indirect arguments if needed (not needed for simple append readback, but good practice)
    
    // We need a buffer to count how many nodes we found
    private GraphicsBuffer _countBuffer; 
    private InputSystem_Actions playerControls;

    private const int MAX_AFFECTED_NODES = 1024; // Arbitrary limit for a single brush stroke

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
        if (svoManager == null) svoManager = SVOManager.Instance;

        // Append Buffer for results
        _affectedNodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.Append, MAX_AFFECTED_NODES, sizeof(uint));
        _countBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments, 1, sizeof(uint));
    }

    private void OnDestroy()
    {
        _affectedNodeBuffer?.Release();
        _countBuffer?.Release();
    }

    private void Update()
    {
        PerformRaycast();
    }

    private void OnAttack()
    {
        Debug.Log(isHitting);
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
            Vector4[] result = new Vector4[1];
            buffer.GetData(result);
            
            // w component holds hit flag (1.0 = hit, 0.0 = miss)
            if (result[0].w > 0.5f)
            {
                isHitting = true;
                lastHitPoint = (Vector3)result[0];
            }
            else
            {
                isHitting = false;
            }
        }
        else
        {
            isHitting = false;
        }
    }

    public void ApplyEdit()
    {
        if (svoManager == null || !svoManager.IsReady) return;

        // 1. Calculate AABB of Brush in Grid Space
        Bounds aabb = GetBrushAABB(lastHitPoint);

        // 2. Determine Grid Indices (Bricks are 4x4x4)
        int brickSize = 4; // Constant from SVOTypes
        
        Vector3 min = aabb.min;
        Vector3 max = aabb.max;

        // Clamp to Grid size
        float gridSize = svoManager.resolution;
        min = Vector3.Max(min, Vector3.zero);
        max = Vector3.Min(max, new Vector3(gridSize, gridSize, gridSize));

        minBrickId = Vector3Int.FloorToInt(min / brickSize);
        maxBrickId = Vector3Int.FloorToInt(max / brickSize);

        // Calculate Range
        int rangeX = Mathf.Max(1, maxBrickId.x - minBrickId.x + 1);
        int rangeY = Mathf.Max(1, maxBrickId.y - minBrickId.y + 1);
        int rangeZ = Mathf.Max(1, maxBrickId.z - minBrickId.z + 1);

        // 3. Setup Compute Shader kernels
        int kernelAlloc = svoEditorCompute.FindKernel("AllocateNodes");
        int kernelEdit = svoEditorCompute.FindKernel("EditVoxels");

        // Uniforms
        svoEditorCompute.SetInts("_MinBrickIndex", new int[] { minBrickId.x, minBrickId.y, minBrickId.z });
        svoEditorCompute.SetInts("_MaxBrickIndex", new int[] { maxBrickId.x, maxBrickId.y, maxBrickId.z });
        svoEditorCompute.SetFloat("_GridSize", gridSize);
        svoEditorCompute.SetInt("_MaxBricks", svoManager.maxBricks); // Ensure this is public in SVOManager

        // Set Brush Struct
        // HLSL: float3 position, float3 bounds, float radius, int materialId, int shape, int op
        // We can use SetFloats / SetInts or a simple buffer. 
        // Since it's a struct uniform "VoxelBrush _Brush", simpler to pass fields individually if shader allows?
        // No, Unity doesn't auto-unwrap structs for SetFloats unless we define property block.
        // It's easier to just passing arrays matching the alignment or use SetVector.
        // Or simpler: Just change the shader to use individual uniforms if struct packing is annoying.
        // BUT, let's try to set it via SetVector since it's small.
        // Struct Layout: 
        // float3 position (12) + 4 padding? -> float4
        // float3 bounds (12) + 4 padding? -> float4
        // float radius, int material, int shape, int op (16) -> float4
        // Unity "SetFloats" can set a float array to a struct uniform if we know the offsets.
        // Safer: Use SetValues via a trivial ComputeBuffer or define uniforms separately.
        // Let's assume standard packing and try setting floats.
        
        // Actually, let's just use separate uniforms in C# to avoid struct alignment headache in one shot
        // But wait, the shader defines `VoxelBrush _Brush;`.
        // I'll assume I can set `_Brush.position` etc via `svoEditorCompute.SetVector("_Brush.position", ...)`?
        // No, Unity ComputeShader doesn't support dot notation for SetVector easily on structs.
        // I will change the shader to use separate uniforms in the next step if this fails, 
        // but for now let's try setting the buffer method which is robust.
        // OR: Modify shader to not use a struct for uniforms.
        // Given I just wrote the shader, I can re-write it to use `float3 _BrushPos` etc.
        // But let's try `SetVector` with specific names if Unity supports it (it usually does for properties).
        
        svoEditorCompute.SetVector("_BrushPosition", lastHitPoint);
        svoEditorCompute.SetVector("_BrushBounds", brushBounds);
        svoEditorCompute.SetFloat("_BrushRadius", brushRadius);
        svoEditorCompute.SetInt("_BrushMaterialId", selectedMaterialId);
        svoEditorCompute.SetInt("_BrushShape", (int)brushShape);
        svoEditorCompute.SetInt("_BrushOp", (int)brushOp);
        svoEditorCompute.SetFloat("_Smoothness", 1.0f); // Default smoothness

        // Buffers - Allocate
        svoEditorCompute.SetBuffer(kernelAlloc, "_NodeBuffer", svoManager.NodeBuffer);
        svoEditorCompute.SetBuffer(kernelAlloc, "_CounterBuffer", svoManager.CounterBuffer); // Need public accessor
        svoEditorCompute.SetBuffer(kernelAlloc, "_PayloadBuffer", svoManager.PayloadBuffer);
        svoEditorCompute.SetBuffer(kernelAlloc, "_BrickBuffer", svoManager.BrickBuffer);
        
        // Buffers - Edit
        svoEditorCompute.SetBuffer(kernelEdit, "_NodeBuffer", svoManager.NodeBuffer);
        svoEditorCompute.SetBuffer(kernelEdit, "_PayloadBuffer", svoManager.PayloadBuffer);
        svoEditorCompute.SetBuffer(kernelEdit, "_BrickBuffer", svoManager.BrickBuffer);

        // 4. Dispatch AllocateNodes (8x8x8 threads per group -> 1 brick per thread)
        // We want 1 thread per brick.
        // Threads per group = 512 (8*8*8).
        // If range is e.g. 10x10x10 = 1000 bricks. We need 2 groups.
        int totalBricksX = rangeX;
        int totalBricksY = rangeY;
        int totalBricksZ = rangeZ;
        
        // Dispatch (groupsX, groupsY, groupsZ) where TotalThreads = Groups * 8
        int groupsAllocX = Mathf.CeilToInt(totalBricksX / 8.0f);
        int groupsAllocY = Mathf.CeilToInt(totalBricksY / 8.0f);
        int groupsAllocZ = Mathf.CeilToInt(totalBricksZ / 8.0f);
        
        svoEditorCompute.Dispatch(kernelAlloc, groupsAllocX, groupsAllocY, groupsAllocZ);

        // 5. Dispatch EditVoxels (4x4x4 threads per group -> 1 brick per GROUP)
        // GroupID maps to Brick Index.
        // So we need RangeX * RangeY * RangeZ groups.
        svoEditorCompute.Dispatch(kernelEdit, rangeX, rangeY, rangeZ);

        Debug.Log($"Applied Edit. Range: {rangeX}x{rangeY}x{rangeZ}");
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
