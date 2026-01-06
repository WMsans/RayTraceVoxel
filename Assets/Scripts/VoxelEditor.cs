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

    private const int MAX_AFFECTED_NODES = 1024; // Arbitrary limit for a single brush stroke

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

        // For debug/testing, trigger on click (assuming InputSystem is available or legacy)
        // Using Legacy Input for simplicity in Editor script unless InputSystem is strictly required for this part.
        // But PlayerController uses InputSystem. Let's assume mouse click.
        if (Mouse.current.leftButton.wasPressedThisFrame && isHitting)
        {
            IdentifyAffectedBricks();
        }
    }

    private void PerformRaycast()
    {
        if (mainCamera == null) return;

        Ray ray = mainCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0)); // Center of screen
        RaycastHit hit;

        // LayerMask? For now, collide with everything (Default)
        if (Physics.Raycast(ray, out hit, 100f))
        {
            isHitting = true;
            lastHitPoint = hit.point;
        }
        else
        {
            isHitting = false;
        }
    }

    public void IdentifyAffectedBricks()
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

        // 3. Setup Compute Shader
        int kernel = svoEditorCompute.FindKernel("FindAffectedNodes");
        
        // Reset Counter
        _affectedNodeBuffer.SetCounterValue(0);

        svoEditorCompute.SetBuffer(kernel, "_NodeBuffer", svoManager.NodeBuffer);
        svoEditorCompute.SetBuffer(kernel, "_AffectedNodeBuffer", _affectedNodeBuffer);
        
        svoEditorCompute.SetInts("_MinBrickIndex", new int[] { minBrickId.x, minBrickId.y, minBrickId.z });
        svoEditorCompute.SetInts("_MaxBrickIndex", new int[] { maxBrickId.x, maxBrickId.y, maxBrickId.z });
        
        // Dispatch threads covering the range
        // Threads = (RangeX, RangeY, RangeZ)
        // We can do 1 thread per brick.
        int rangeX = Mathf.Max(1, maxBrickId.x - minBrickId.x + 1);
        int rangeY = Mathf.Max(1, maxBrickId.y - minBrickId.y + 1);
        int rangeZ = Mathf.Max(1, maxBrickId.z - minBrickId.z + 1);

        // Dispatch (groups of 1 is fine for small brushes, or we optimize groups)
        // Let's use [numthreads(8,8,8)]
        int groupsX = Mathf.CeilToInt(rangeX / 8.0f);
        int groupsY = Mathf.CeilToInt(rangeY / 8.0f);
        int groupsZ = Mathf.CeilToInt(rangeZ / 8.0f);

        svoEditorCompute.Dispatch(kernel, groupsX, groupsY, groupsZ);

        Debug.Log($"Dispatched IdentifyAffectedBricks. Range: {rangeX}x{rangeY}x{rangeZ}");
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
