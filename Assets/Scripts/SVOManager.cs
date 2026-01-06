using UnityEngine;
using VoxelEngine.Core.Buffers;
using VoxelEngine.Core.Generators;
using VoxelEngine.Core.Interfaces;

public class SVOManager : MonoBehaviour, IVoxelStorage
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

    private SVOBufferManager _bufferManager;

    // IVoxelStorage Implementation
    public GraphicsBuffer NodeBuffer => _bufferManager?.NodeBuffer;
    public GraphicsBuffer PayloadBuffer => _bufferManager?.PayloadBuffer;
    public GraphicsBuffer BrickBuffer => _bufferManager?.BrickBuffer;
    public GraphicsBuffer BrickMaterialBuffer => _bufferManager?.BrickMaterialBuffer;
    public GraphicsBuffer CounterBuffer => _bufferManager?.CounterBuffer;
    
    public int Resolution => resolution;
    public int MaxNodes => maxNodes;
    public int MaxBricks => maxBricks;
    public bool IsReady => _bufferManager != null && _bufferManager.NodeBuffer != null;

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
        _bufferManager = new SVOBufferManager(maxNodes, maxBricks);
    }

    private void BuildSVO()
    {
        if (svoCompute == null) return;
        SVOGenerator.Build(svoCompute, _bufferManager, resolution);
    }

    private void ReadbackCounters()
    {
        if (_bufferManager == null) return;
        
        uint[] counters = new uint[3];
        _bufferManager.CounterBuffer.GetData(counters);
        nodeCount = 4681; 
        brickCount = (int)counters[2] / 64; 
    }

    private void OnDestroy()
    {
        _bufferManager?.Dispose();
    }
}
