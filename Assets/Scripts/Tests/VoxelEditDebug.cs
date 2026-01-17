using UnityEngine;
using VoxelEngine.Core;
using VoxelEngine.Core.Editing;
using VoxelEngine.Core.Data;
using UnityEngine.InputSystem;

public class VoxelEditDebug : MonoBehaviour
{
    public float voxelSize = 1.0f;
    public int brickSize = 4;

    private void Update()
    {
        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            PerformDebugCheck();
        }
    }

    private void PerformDebugCheck()
    {
        // 1. Raycast to find a hit
        if (Camera.main == null) return;
        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
        
        // Simple plane raycast for demonstration if no physics
        Plane p = new Plane(Vector3.up, 0);
        if (p.Raycast(ray, out float enter))
        {
            Vector3 hitPoint = ray.GetPoint(enter);
            Debug.Log($"--- Debugging Edit at {hitPoint} ---");

            // 2. Simulate finding the chunk
            VoxelVolume hitVolume = null;
            foreach (var vol in VoxelVolumeRegistry.Volumes)
            {
                if (vol.WorldBounds.Contains(hitPoint))
                {
                    hitVolume = vol;
                    break;
                }
            }

            if (hitVolume == null)
            {
                Debug.Log("No volume found at hit point.");
                return;
            }

            // 3. Replicate VoxelModifier Logic (CORRECTED)
            float worldToVoxelScale = hitVolume.Resolution / hitVolume.WorldSize;

            // [FIX] VoxelModifier converts to LOCAL space first
            Vector3 localHitPoint = hitPoint - hitVolume.WorldOrigin;
            Vector3 brushPosVoxelLocal = localHitPoint * worldToVoxelScale;
            float brickVoxelSize = brickSize;

            // This is the Local Brick Index (passed to shader in VoxelModifier)
            Vector3Int localBrickIndex = Vector3Int.FloorToInt(brushPosVoxelLocal / brickVoxelSize);

            // This is the chunk's global offset in bricks
            Vector3Int volOriginBrick = GetGlobalBrickIndex(hitVolume.WorldOrigin);

            // The logic combines Origin (Global) + Local Index => Global Key
            Vector3Int logicGlobalKey = volOriginBrick + localBrickIndex;

            // This is the expected Global Key calculated directly from world position
            Vector3Int expectedGlobalKey = GetGlobalBrickIndex(hitPoint);

            Debug.Log($"Chunk Origin: {hitVolume.WorldOrigin}");
            Debug.Log($"Chunk Origin Brick Index: {volOriginBrick}");
            Debug.Log($"Local Brick Index (Shader): {localBrickIndex}");
            Debug.Log($"<color=green>Logic Generated Key (Origin + Local): {logicGlobalKey}</color>");
            Debug.Log($"<color=green>Expected Global Key: {expectedGlobalKey}</color>");

            if (logicGlobalKey != expectedGlobalKey)
            {
                Debug.LogError($"COORDINATE MISMATCH: Logic {logicGlobalKey} != Expected {expectedGlobalKey}");
            }
            else
            {
                Debug.Log("SUCCESS: Logic matches expected global coordinate.");
            }
        }
    }

    private Vector3Int GetGlobalBrickIndex(Vector3 worldPos)
    {
        float brickWorldSize = brickSize * voxelSize;
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / brickWorldSize),
            Mathf.FloorToInt(worldPos.y / brickWorldSize),
            Mathf.FloorToInt(worldPos.z / brickWorldSize)
        );
    }
}