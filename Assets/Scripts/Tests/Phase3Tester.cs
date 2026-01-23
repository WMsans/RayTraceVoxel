using UnityEngine;
using VoxelEngine.Core.Editing;
using VoxelEngine.Core.Generators;
using VoxelEngine.Core.Data;
using VoxelEngine.Core.Streaming; // Assuming this is where WorldManager is

public class Phase3Tester : MonoBehaviour
{
    public float editDistance = 5.0f;
    public float holeSize = 2.0f; // Roughly matches 4 voxels if size is 0.5

    // Reference to your dynamic object prefab or manager
    public DynamicSDFManager sdfManager; 

    private InputSystem_Actions _input;

    private void Awake()
        {
            _input = new InputSystem_Actions();
        }

        private void OnEnable()
        {
            _input.Player.Attack.Enable();
        }

        private void OnDisable()
        {
            _input.Player.Attack.Disable();
        }

    void Update()
    {
        // TEST 1: DIG A HOLE (Verify Terrain -> Edit override)
        if (_input.Player.Attack.WasPressedThisFrame()) 
        {
            CreateAirBrick();
        }

        // TEST 2: PLACE DYNAMIC OBJECT (Verify Edit -> Dynamic blend)
        // if (Input.GetKeyDown(KeyCode.J))
        // {
        //     CreateDynamicSphereInHole();
        // }
    }

    void CreateAirBrick()
    {
        // 1. Calculate position in front of player
        Vector3 worldPos = transform.position;
        
        // 2. Get the Brick Coordinate (Using your Manager's helper)
        Vector3Int brickCoord = VoxelEditManager.Instance.GetBrickCoordinate(worldPos);
        
        Debug.Log($"[Test] Digging hole at World: {worldPos}, BrickCoord: {brickCoord}");

        // 3. Create "Air" Data
        // 216 uints (6x6x6). SDF > 0 means air. Material 0 means empty.
        uint[] airData = new uint[216];
        for (int i = 0; i < airData.Length; i++)
        {
            // PackVoxelData (From your logic: float sdf, float3 normal, uint mat)
            // SDF = 1.0 (Air), Normal = Up, Mat = 0
            // You'll need to replicate your packing logic here or expose a helper.
            // Assuming 16-bit float SDF, standard packing:
            airData[i] = PackAirVoxel(); 
        }

        // 4. Register Edit
        VoxelEditManager.Instance.RegisterEdit(brickCoord, airData);

        // 5. Force Re-generation (Quick & Dirty method: Clear chunks)
        // ideally you call WorldManager.Instance.ReloadChunk(chunkCoord);
        Debug.Log("[Test] Edit Registered. Move camera slightly to trigger chunk refresh.");
    }

    void CreateDynamicSphereInHole()
    {
        Vector3 worldPos = transform.position + transform.forward * editDistance;
        Debug.Log($"[Test] Spawning Sphere at {worldPos}");

        // Use your DynamicSDFManager to spawn a sphere
        // This assumes you have a method like RegisterObject or similar
        // sdfManager.AddSphere(worldPos, 1.5f); 
    }

    // --- Helper to replicate your shader packing ---
    uint PackAirVoxel()
    {
        // This must match VoxelStructures.hlsl PackVoxelData
        // Example approximation:
        float sdf = 1.0f; // Positive = Air
        uint material = 0; 
        
        // Assuming standard layout (this is pseudo-code based on common voxel packing)
        // You likely have a C# helper for this in VoxelData.cs
        // If not, just return a value you KNOW is air (e.g. 0xFFFFFFFF if that's empty)
        // But based on your shader: 
        // uint packedData = PackVoxelData(sdf / scale, voxelCtx.gradient, mat);
        
        // For testing, try to find a helper in your project, or use:
        return 0; // If 0 is treated as "Default Empty" by your Unpack logic
    }
}
