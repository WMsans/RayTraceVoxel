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
        
        Debug.Log($"[Test] Generating Sphere at World: {worldPos}, BrickCoord: {brickCoord}");

        // 3. Create Sphere Data (Material 1)
        uint[] voxelData = new uint[216];
        int size = 6;
        Vector3 center = new Vector3(2.5f, 2.5f, 2.5f);
        float radius = 1.5f;

        for (int z = 0; z < size; z++)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int index = z * size * size + y * size + x;
                    Vector3 pos = new Vector3(x, y, z);
                    
                    float dist = Vector3.Distance(pos, center);
                    float sdf = dist - radius;
                    
                    Vector3 normal = (pos - center).normalized;
                    if (normal == Vector3.zero) normal = Vector3.up;

                    voxelData[index] = PackVoxelData(sdf, normal, 1);
                }
            }
        }

        // 4. Register Edit
        VoxelEditManager.Instance.RegisterEdit(brickCoord, voxelData);

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
    uint PackVoxelData(float sdf, Vector3 normal, uint materialID)
    {
        // 1. Material (8 bits)
        uint mat = materialID & 0xFF;

        // 2. SDF (8 bits SNORM) -> Range +/- 4.0
        float normalizedSDF = Mathf.Clamp(sdf / 4.0f, -1.0f, 1.0f);
        uint sdfInt = (uint)((normalizedSDF * 0.5f + 0.5f) * 255.0f);

        // 3. Normal (16 bits)
        uint norm = PackNormalOct(normal);

        // Layout: [Normal 16] [SDF 8] [Mat 8]
        return mat | (sdfInt << 8) | (norm << 16);
    }

    uint PackNormalOct(Vector3 n)
    {
        float sum = Mathf.Abs(n.x) + Mathf.Abs(n.y) + Mathf.Abs(n.z);
        if (sum < 1e-5f) return 0;
        n /= sum;

        float x = n.x;
        float y = n.y;

        if (n.z < 0)
        {
            float tX = (1.0f - Mathf.Abs(y)) * Mathf.Sign(x);
            float tY = (1.0f - Mathf.Abs(x)) * Mathf.Sign(y);
            x = tX;
            y = tY;
        }

        uint packedX = (uint)(Mathf.Clamp01(x * 0.5f + 0.5f) * 255.0f);
        uint packedY = (uint)(Mathf.Clamp01(y * 0.5f + 0.5f) * 255.0f);

        return packedX | (packedY << 8);
    }
}
