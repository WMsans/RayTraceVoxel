using System;
using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine.Core;
using VoxelEngine.Core.Buffers;
using VoxelEngine.Core.Generators;
using VoxelEngine.Core.Interfaces;

public class VoxelVolume : MonoBehaviour, IVoxelStorage
{
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

    private void OnEnable()
    {
        VoxelVolumeRegistry.Register(this);
    }

    private void OnDisable()
    {
        VoxelVolumeRegistry.Unregister(this);
    }

    private void Start()
    {
        InitializeBuffers();
        BuildSVO();
    }

    private void Update()
    {
        UpdateCounters();
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

    private void UpdateCounters()
    {
        if (_bufferManager == null || _bufferManager.CounterBuffer == null) return;

        AsyncGPUReadback.Request(_bufferManager.CounterBuffer, (request) =>
        {
            if (request.hasError) return;
            
            // Counters: [0]=AllocatedNodes (Atomic), [1]=AllocatedPayloads, [2]=AllocatedBricksPtr
            using (var data = request.GetData<uint>())
            {
                if (data.Length >= 3)
                {
                    // Note: nodeCount logic from original script was hardcoded 4681, 
                    // but usually you want the actual atomic count or the SVO structure size.
                    // For now, I will read the atomic counters.
                    // If the original logic was specific, we can adapt. 
                    // [2] is the pointer in floats. Divide by 64 to get bricks.
                    
                    brickCount = (int)data[2] / 64;
                    // nodeCount = (int)data[0]; // If we were counting nodes atomically
                }
            }
        });
    }

    private void OnDestroy()
    {
        _bufferManager?.Dispose();
    }
}