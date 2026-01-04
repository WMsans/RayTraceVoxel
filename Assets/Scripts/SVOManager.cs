using UnityEngine;
using System.Runtime.InteropServices;

public class SVOManager : MonoBehaviour
{
    public static SVOManager Instance { get; private set; }

    [Header("Settings")]
    public ComputeShader svoCompute;
    public int resolution = 64;
    public int maxNodes = 100000; 

    [Header("Debug")]
    public int nodeCount; 

    // GPU Buffers
    private GraphicsBuffer _nodeBuffer;
    private GraphicsBuffer _payloadBuffer;
    private GraphicsBuffer _counterBuffer; 

    // Public Accessors for RenderGraph
    public GraphicsBuffer NodeBuffer => _nodeBuffer;
    public GraphicsBuffer PayloadBuffer => _payloadBuffer;
    public bool IsReady => _nodeBuffer != null && _payloadBuffer != null;

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
        
        // Counter: [NodeCount, PayloadCount]
        _counterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, sizeof(uint));
        _counterBuffer.SetData(new uint[] { 0, 0 }); 
    }

    private void BuildSVO()
    {
        if (svoCompute == null) return;
        int kernel = svoCompute.FindKernel("BuildTestScene");

        svoCompute.SetBuffer(kernel, "_NodeBuffer", _nodeBuffer);
        svoCompute.SetBuffer(kernel, "_PayloadBuffer", _payloadBuffer);
        svoCompute.SetBuffer(kernel, "_CounterBuffer", _counterBuffer);

        svoCompute.SetInt("_GridSize", resolution); 
        svoCompute.SetFloat("_VoxelSize", 1.0f);

        int threadGroups = Mathf.CeilToInt(resolution / 8.0f);
        svoCompute.Dispatch(kernel, threadGroups, threadGroups, threadGroups);
        
        Debug.Log("SVO Generation Dispatched.");
    }

    private void ReadbackCounters()
    {
        // Keep asynchronous to avoid stalling Main Thread in real production
        uint[] counters = new uint[2];
        _counterBuffer.GetData(counters);
        nodeCount = (int)counters[0];
    }

    private void OnDestroy()
    {
        _nodeBuffer?.Release();
        _payloadBuffer?.Release();
        _counterBuffer?.Release();
    }
}
