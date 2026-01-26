using UnityEngine;
using VoxelEngine.Core.Memory;
using VoxelEngine.Core.Streaming;

namespace VoxelEngine.Core.Analysis
{
    /// <summary>
    /// Manages analysis buffers for structural integrity and floating voxel detection.
    /// Creates a parallel mask buffer mirroring the global voxel storage.
    /// </summary>
    public class VoxelStabilityManager : MonoSingleton<VoxelStabilityManager>
    {
        [Header("Configuration")]
        public ComputeShader stabilityAnalysisShader;
        
        /// <summary>
        /// The parallel stability buffer.
        /// Format: 1 bit per voxel packed into uints.
        /// 0 = Unstable/Unknown
        /// 1 = Stable (Connected to ground)
        /// Note: "Detected Floating" state is usually transient or handled via a secondary list/dispatch 
        /// depending on the specific compute implementation, but bit-masking is most efficient for storage.
        /// </summary>
        public GraphicsBuffer StabilityMaskBuffer { get; private set; }

        public bool IsReady => StabilityMaskBuffer != null && StabilityMaskBuffer.IsValid();

        // --- Constants for Compute Shader ---
        // If using a full uint (byte) per voxel for simplicity as option B:
        // 0: Unstable, 1: Stable, 2: Floating
        // But to save VRAM (540M voxels = 2GB), we default to 1-bit packing for the 'Stable' flag.
        // If complex states are needed (3 states), we might need 2 bits per voxel.
        // Assuming 2 bits per voxel for: 00 (Unstable), 01 (Stable), 10 (Floating), 11 (Reserved).
        private const int BITS_PER_VOXEL = 2; 
        private const int ELEMENTS_PER_UINT = 32 / BITS_PER_VOXEL; // 16 voxels per uint

        public const uint STATE_UNSTABLE = 0;
        public const uint STATE_STABLE = 1;
        public const uint STATE_FLOATING = 2;

        private void Start()
        {
            // Delay initialization to ensure Pool is ready
            if (VoxelVolumePool.Instance != null)
            {
                InitializeBuffers();
            }
        }

        private void OnDestroy()
        {
            ReleaseBuffers();
        }

        public void InitializeBuffers()
        {
            var pool = VoxelVolumePool.Instance;
            if (pool == null)
            {
                Debug.LogError("[VoxelStabilityManager] VoxelVolumePool instance not found.");
                return;
            }

            // 1. Calculate Size
            // We need to mirror GlobalBrickDataBuffer.
            // Total Voxels = PoolSize * MaxBricks * 216
            // We access the raw buffer size directly.
            int totalVoxels = pool.GlobalBrickDataBuffer.count;

            // 2. Allocate Packed Buffer
            // We pack 'ELEMENTS_PER_UINT' voxels into one uint.
            int bufferSize = Mathf.CeilToInt((float)totalVoxels / ELEMENTS_PER_UINT);
            
            // Safety check for empty pool
            if (bufferSize == 0) bufferSize = 1;

            if (StabilityMaskBuffer != null) StabilityMaskBuffer.Release();
            
            StabilityMaskBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, sizeof(uint));
            
            Debug.Log($"[VoxelStabilityManager] Allocated Stability Buffer: {bufferSize * 4 / 1024 / 1024} MB for {totalVoxels} voxels.");
        }

        private void ReleaseBuffers()
        {
            if (StabilityMaskBuffer != null)
            {
                StabilityMaskBuffer.Release();
                StabilityMaskBuffer = null;
            }
        }

        /// <summary>
        /// Clears the stability buffer to 0 (Unstable).
        /// Should be called before running a new stability analysis pass.
        /// </summary>
        public void ResetStability()
        {
            if (!IsReady) return;
            // A simple dispatch to clear would go here, or GraphicsBuffer.SetData with zeroed array (slow for large buffers)
            // For now, we assume the Compute Shader has a Clear kernel.
            if (stabilityAnalysisShader != null)
            {
                int kernel = stabilityAnalysisShader.FindKernel("ClearStabilityBuffer");
                stabilityAnalysisShader.SetBuffer(kernel, "_StabilityMaskBuffer", StabilityMaskBuffer);
                stabilityAnalysisShader.SetInt("_BufferSize", StabilityMaskBuffer.count);
                int groups = Mathf.CeilToInt(StabilityMaskBuffer.count / 256.0f);
                stabilityAnalysisShader.Dispatch(kernel, groups, 1, 1);
            }
        }
    }
}