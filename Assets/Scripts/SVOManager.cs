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
    private GraphicsBuffer _brickBuffer;
    private GraphicsBuffer _counterBuffer; 

    // Accessors
    public GraphicsBuffer NodeBuffer => _nodeBuffer;
    public GraphicsBuffer PayloadBuffer => _payloadBuffer;
    public GraphicsBuffer BrickBuffer => _brickBuffer;
    public GraphicsBuffer CounterBuffer => _counterBuffer;
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
        
        // 1. Initialize Dense Structure
        int kernelInit = svoCompute.FindKernel("InitDenseStructure");
        svoCompute.SetBuffer(kernelInit, "_NodeBuffer", _nodeBuffer);
        // Dispatch 4681 threads. 64 per group.
        // 4681 / 64 = 73.1 -> 74 groups
        svoCompute.Dispatch(kernelInit, 74, 1, 1);

        // 2. Build Bricks
        int kernelBuild = svoCompute.FindKernel("BuildBricks");

        svoCompute.SetBuffer(kernelBuild, "_NodeBuffer", _nodeBuffer);
        svoCompute.SetBuffer(kernelBuild, "_PayloadBuffer", _payloadBuffer);
        svoCompute.SetBuffer(kernelBuild, "_BrickBuffer", _brickBuffer);
        svoCompute.SetBuffer(kernelBuild, "_CounterBuffer", _counterBuffer);

        svoCompute.SetInt("_GridSize", resolution); 
        
        int numBricksPerAxis = Mathf.CeilToInt(resolution / 4.0f);
        int threadGroups = Mathf.CeilToInt(numBricksPerAxis / 8.0f);
        
        svoCompute.Dispatch(kernelBuild, threadGroups, threadGroups, threadGroups);
        
        Debug.Log($"SVO Generation Dispatched. Grid: {resolution}");
    }

    private void ReadbackCounters()
    {
        uint[] counters = new uint[3];
        _counterBuffer.GetData(counters);
        
        // Since InitDenseStructure doesn't use the atomic counter (it uses fixed indices),
        // the buffer count remains 0. We manually set the known dense node count here.
        nodeCount = 4681; 
        
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
