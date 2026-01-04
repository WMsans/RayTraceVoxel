using UnityEngine;
using System.Runtime.InteropServices;

public class SVOManager : MonoBehaviour
{
    public static SVOManager Instance { get; private set; }

    [Header("Settings")]
    public ComputeShader svoCompute;
    public int resolution = 64; // Grid size
    public int maxNodes = 100000;
    
    // Bricks: (Resolution / 4)^3 potential bricks. 
    // We allocate space for a reasonable sparse amount (e.g., 1/4th filled).
    public int maxBricks = 50000; 

    [Header("Debug")]
    public int nodeCount; 
    public int brickCount;

    // GPU Buffers
    private GraphicsBuffer _nodeBuffer;
    private GraphicsBuffer _payloadBuffer;
    private GraphicsBuffer _brickBuffer; // New: Stores raw SDF floats (Bricks)
    private GraphicsBuffer _counterBuffer; 

    // Accessors
    public GraphicsBuffer NodeBuffer => _nodeBuffer;
    public GraphicsBuffer PayloadBuffer => _payloadBuffer;
    public GraphicsBuffer BrickBuffer => _brickBuffer;
    public bool IsReady => _nodeBuffer != null && _brickBuffer != null;

    private void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        else Instance = this;
    }

    private void Start()
    {
        InitializeBuffers();
        BuildSVO();
        ReadbackCounters(); 
    }

    private void InitializeBuffers()
    {
        _nodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxNodes, Marshal.SizeOf<SVONode>());
        _payloadBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxNodes, Marshal.SizeOf<VoxelPayload>());
        
        // Brick Buffer: Each brick is 64 floats (4x4x4)
        int brickSizeInFloats = SVONode.BRICK_VOXEL_COUNT;
        _brickBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxBricks * brickSizeInFloats, sizeof(float));
        
        // Counter: [NodeCount, PayloadCount, BrickFloatIndex]
        _counterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, sizeof(uint));
        _counterBuffer.SetData(new uint[] { 0, 0, 0 }); 
    }

    private void BuildSVO()
    {
        if (svoCompute == null) return;
        int kernel = svoCompute.FindKernel("BuildBricks"); // Renamed kernel

        svoCompute.SetBuffer(kernel, "_NodeBuffer", _nodeBuffer);
        svoCompute.SetBuffer(kernel, "_PayloadBuffer", _payloadBuffer);
        svoCompute.SetBuffer(kernel, "_BrickBuffer", _brickBuffer);
        svoCompute.SetBuffer(kernel, "_CounterBuffer", _counterBuffer);

        svoCompute.SetInt("_GridSize", resolution); 
        
        // Dispatch one thread per 4x4x4 Block
        // Resolution 64 -> 16 blocks wide. 
        // 16 / 8 threads = 2 groups.
        int numBricksPerAxis = Mathf.CeilToInt(resolution / 4.0f);
        int threadGroups = Mathf.CeilToInt(numBricksPerAxis / 8.0f);
        
        svoCompute.Dispatch(kernel, threadGroups, threadGroups, threadGroups);
        
        Debug.Log($"SVO Generation Dispatched. Grid: {resolution}, BricksAxis: {numBricksPerAxis}");
    }

    private void ReadbackCounters()
    {
        uint[] counters = new uint[3];
        _counterBuffer.GetData(counters);
        nodeCount = (int)counters[0];
        brickCount = (int)counters[2] / 64; // Convert float count back to bricks
    }

    private void OnDestroy()
    {
        _nodeBuffer?.Release();
        _payloadBuffer?.Release();
        _brickBuffer?.Release();
        _counterBuffer?.Release();
    }
}