using UnityEngine;
using System.Runtime.InteropServices;

public class SVOManager : MonoBehaviour
{
    public static SVOManager Instance { get; private set; }

    [Header("Settings")]
    public ComputeShader svoCompute;
    public int resolution = 64;
    public int maxNodes = 100000;
    public int maxBricks = 50000; 

    [Header("Debug")]
    public int nodeCount; 
    public int brickCount;

    // GPU Buffers
    private GraphicsBuffer _nodeBuffer;
    private GraphicsBuffer _payloadBuffer;
    private GraphicsBuffer _brickBuffer;         // Stores SDF (floats)
    private GraphicsBuffer _brickMaterialBuffer; // NEW: Stores Material IDs (uints)
    private GraphicsBuffer _counterBuffer; 

    public GraphicsBuffer NodeBuffer => _nodeBuffer;
    public GraphicsBuffer PayloadBuffer => _payloadBuffer;
    public GraphicsBuffer BrickBuffer => _brickBuffer;
    public GraphicsBuffer BrickMaterialBuffer => _brickMaterialBuffer; // Public Accessor
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
        
        int brickVoxels = SVONode.BRICK_VOXEL_COUNT; // 64
        
        // 1. SDF Buffer
        _brickBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxBricks * brickVoxels, sizeof(float));
        
        // 2. Material Buffer (NEW)
        _brickMaterialBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxBricks * brickVoxels, sizeof(uint));
        
        _counterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, sizeof(uint));
        _counterBuffer.SetData(new uint[] { 0, 0, 0 }); 
    }

    private void BuildSVO()
    {
        if (svoCompute == null) return;
        
        int kernelInit = svoCompute.FindKernel("InitDenseStructure");
        svoCompute.SetBuffer(kernelInit, "_NodeBuffer", _nodeBuffer);
        svoCompute.Dispatch(kernelInit, 74, 1, 1);

        int kernelBuild = svoCompute.FindKernel("BuildBricks");
        svoCompute.SetBuffer(kernelBuild, "_NodeBuffer", _nodeBuffer);
        svoCompute.SetBuffer(kernelBuild, "_PayloadBuffer", _payloadBuffer);
        svoCompute.SetBuffer(kernelBuild, "_BrickBuffer", _brickBuffer);
        svoCompute.SetBuffer(kernelBuild, "_BrickMaterialBuffer", _brickMaterialBuffer); // Bind New Buffer
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
        nodeCount = 4681; 
        brickCount = (int)counters[2] / 64; 
    }

    private void OnDestroy()
    {
        _nodeBuffer?.Release();
        _payloadBuffer?.Release();
        _brickBuffer?.Release();
        _brickMaterialBuffer?.Release();
        _counterBuffer?.Release();
    }
}