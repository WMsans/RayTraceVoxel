# Voxel Removal Particles Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add VFX Graph-based debris particles that spawn when the player subtracts voxels with the brush tool, textured with the removed voxel's material.

**Architecture:** Extend the existing raycast hit buffer to include material ID, then hook into TerrainEditorTool after brush subtraction to spawn particles via a new VoxelVFXManager. Particles use VFX Graph with a custom shader that samples the existing Texture2DArray.

**Tech Stack:** Unity VFX Graph, HLSL, Compute Shader (minor modification)

---

## Task 1: Extend Raycast Buffer with Material ID

**Files:**
- Modify: `Assets/Scripts/VoxelEngine/Rendering/Raytracing/Compute/VoxelRaytracer.compute:195`
- Modify: `Assets/Scripts/VoxelEngine/Editing/TerrainEditorTool.cs:72-91`

**Step 1: Update compute shader to write material ID**

In `VoxelRaytracer.compute`, find line 195:
```hlsl
_RaycastBuffer[1] = float4((float)primaryHit.chunkId, 0, 0, 0);
```

Replace with:
```hlsl
_RaycastBuffer[1] = float4((float)primaryHit.chunkId, (float)primaryHit.matId, 0, 0);
```

**Step 2: Update TerrainEditorTool to read material ID**

In `TerrainEditorTool.cs`, add a new field to store the material ID:

```csharp
private int _currentMaterialId;
```

In `OnReadbackComplete`, update to read the material ID from data[1].y:

```csharp
private void OnReadbackComplete(AsyncGPUReadbackRequest request)
{
    _readbackPending = false;
    if (request.hasError) return;

    var data = request.GetData<Vector4>();
    Vector4 hitPosData = data[0]; 
    
    if (hitPosData.w > 0.5f)
    {
        _currentHitPoint = new Vector3(hitPosData.x, hitPosData.y, hitPosData.z);
        _currentHitVolumeIndex = (int)data[1].x;
        _currentMaterialId = (int)data[1].y;
        _hasHit = true;
    }
    else
    {
        _hasHit = false;
        _currentHitVolumeIndex = -1;
        _currentMaterialId = 0;
    }
}
```

**Step 3: Test in Unity**

1. Open Unity and the main scene
2. Enter Play mode
3. Click on terrain to verify raycast still works
4. Check that `_currentMaterialId` is populated (add temporary Debug.Log if needed)

**Step 4: Commit**

```bash
git add Assets/Scripts/VoxelEngine/Rendering/Raytracing/Compute/VoxelRaytracer.compute
git add Assets/Scripts/VoxelEngine/Editing/TerrainEditorTool.cs
git commit -m "feat: add material ID to raycast hit buffer for VFX system"
```

---

## Task 2: Create VoxelVFXManager

**Files:**
- Create: `Assets/Scripts/VoxelEngine/Effects/VoxelVFXManager.cs`

**Step 1: Create the VFX manager class**

Create `Assets/Scripts/VoxelEngine/Effects/` folder if it doesn't exist, then create `VoxelVFXManager.cs`:

```csharp
using UnityEngine;
using UnityEngine.VFX;

namespace VoxelEngine.Core.Effects
{
    public class VoxelVFXManager : MonoBehaviour
    {
        public static VoxelVFXManager Instance { get; private set; }

        [Header("VFX Configuration")]
        [SerializeField] private VisualEffect debrisVFXPrefab;
        [SerializeField] private int poolSize = 3;

        private VisualEffect[] _vfxPool;
        private int _poolIndex;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            
            InitializePool();
        }

        private void InitializePool()
        {
            _vfxPool = new VisualEffect[poolSize];
            for (int i = 0; i < poolSize; i++)
            {
                _vfxPool[i] = Instantiate(debrisVFXPrefab, transform);
                _vfxPool[i].Stop();
            }
        }

        public void SpawnDebris(Vector3 position, float radius, int materialId)
        {
            VisualEffect vfx = _vfxPool[_poolIndex];
            _poolIndex = (_poolIndex + 1) % poolSize;

            vfx.transform.position = position;
            vfx.SetVector3("Position", position);
            vfx.SetFloat("Radius", radius);
            vfx.SetInt("MaterialID", materialId);
            
            vfx.Reinit();
            vfx.Play();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
```

**Step 2: Create Effects folder in Unity**

In Unity:
1. Right-click `Assets/Scripts/VoxelEngine/` → Create → Folder → Name it "Effects"
2. Create the script via the menu or drag the file

**Step 3: Commit**

```bash
git add Assets/Scripts/VoxelEngine/Effects/VoxelVFXManager.cs
git commit -m "feat: add VoxelVFXManager for debris particle spawning"
```

---

## Task 3: Create VFX Graph Asset

**Files:**
- Create: `Assets/VFX/VoxelDebris.vfx` (via Unity Editor)

**Step 1: Create VFX folder**

In Unity:
1. Right-click `Assets/` → Create → Folder → Name it "VFX"

**Step 2: Create VFX Graph**

In Unity:
1. Right-click `Assets/VFX/` → Create → Visual Effects → Visual Effect Graph
2. Name it "VoxelDebris"

**Step 3: Configure VFX Graph properties**

Open the VFX Graph and add these exposed properties:

| Property | Type | Default |
|----------|------|---------|
| Position | Vector3 | (0, 0, 0) |
| Radius | float | 2.0 |
| MaterialID | int | 0 |

**Step 4: Create Spawn System**

1. Add a Spawn System
2. Add a "Spawn on Event" block, set Event Name to "OnSpawn"
3. Add a "Set Spawn Rate" block, set to 0
4. Add a "Spawn Burst" block, set Count to ~30-50 particles

**Step 5: Create Initialize Context**

Connect Initialize block with:
- **Position**: `Position + (Random.value * Radius * Random.insideUnitSphere)`
- **Velocity**: `(normalize(Position - center) + Vector3.up * 0.5) * Random.Range(1, 3)`
- **Lifetime**: `Random.Range(1.0, 2.0)`
- **Size**: `Random.Range(0.05, 0.15)`

**Step 6: Create Update Context**

Add blocks:
- **Gravity**: Add "Gravity" block, set to Vector3(0, -9.8, 0)
- **Drag**: Add "Drag" block, set to 0.5
- **Color over Lifetime**: Add gradient that fades alpha from 1 to 0

**Step 7: Create Output Context**

1. Add Output: Quad
2. Set orientation to "Face Camera Position"
3. This will be updated in Task 4 to use custom shader

**Step 8: Save the VFX asset**

Ctrl+S in VFX Graph window

**Step 9: Commit**

```bash
git add Assets/VFX/
git commit -m "feat: add VoxelDebris VFX Graph asset"
```

---

## Task 4: Create Custom VFX Shader for Texture Array

**Files:**
- Create: `Assets/VFX/Shaders/VoxelDebrisShader.shader`
- Modify: `Assets/VFX/VoxelDebris.vfx` (assign shader)

**Step 1: Create Shaders folder**

In Unity:
1. Right-click `Assets/VFX/` → Create → Folder → Name it "Shaders"

**Step 2: Create the shader**

Create `Assets/VFX/Shaders/VoxelDebrisShader.shader`:

```shader
Shader "VFX/VoxelDebris"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _AlbedoArray ("Albedo Array", 2DArray) = "white" {}
        _MaterialID ("Material ID", Int) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.visualeffectgraph/Editor/Shaders/VFXCommon.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D_ARRAY(_AlbedoArray);
            SAMPLER(sampler_AlbedoArray);

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(int, _MaterialID)
            UNITY_INSTANCING_BUFFER_END(Props)

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = input.uv;
                output.color = input.color;
                
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                
                int matId = UNITY_ACCESS_INSTANCED_PROP(Props, _MaterialID);
                half4 texColor = SAMPLE_TEXTURE2D_ARRAY(_AlbedoArray, sampler_AlbedoArray, input.uv, matId);
                
                return texColor * input.color;
            }
            ENDHLSL
        }
    }
}
```

**Step 3: Assign shader to VFX Graph**

In Unity:
1. Open `VoxelDebris.vfx`
2. In the Output context, click the shader dropdown
3. Select "VFX/VoxelDebris"
4. Save the VFX Graph

**Step 4: Configure material ID in VFX**

In VFX Graph Output context:
1. Add a "Set Material ID" block (or use Property binding)
2. Connect the MaterialID property to the shader's _MaterialID

**Step 5: Commit**

```bash
git add Assets/VFX/Shaders/VoxelDebrisShader.shader
git add Assets/VFX/VoxelDebris.vfx
git commit -m "feat: add custom VFX shader for texture array sampling"
```

---

## Task 5: Create VFX Prefab

**Files:**
- Create: `Assets/Prefabs/VoxelDebrisVFX.prefab`

**Step 1: Create GameObject**

In Unity:
1. Create empty GameObject in scene, name it "VoxelDebrisVFX"
2. Add Visual Effect component
3. Assign `VoxelDebris.vfx` to the Visual Effect asset field
4. Assign `VoxelDefinitionManager.Instance.albedoTextureArray` to the shader's _AlbedoArray property

**Step 2: Create prefab**

1. Drag the GameObject from Hierarchy to `Assets/Prefabs/`
2. Delete the scene instance

**Step 3: Commit**

```bash
git add Assets/Prefabs/VoxelDebrisVFX.prefab
git commit -m "feat: add VoxelDebrisVFX prefab"
```

---

## Task 6: Wire VFX Manager into Scene

**Files:**
- Modify: Scene (create manager GameObject)

**Step 1: Add VFX Manager to scene**

In Unity:
1. Create empty GameObject, name it "VoxelVFXManager"
2. Add `VoxelVFXManager` component
3. Assign `VoxelDebrisVFX` prefab to the `debrisVFXPrefab` field

**Step 2: Save scene**

Ctrl+S to save the scene

**Step 3: Commit**

```bash
git add Assets/Scenes/
git commit -m "feat: add VoxelVFXManager to scene"
```

---

## Task 7: Hook TerrainEditorTool to Spawn VFX

**Files:**
- Modify: `Assets/Scripts/VoxelEngine/Editing/TerrainEditorTool.cs`

**Step 1: Add VFX spawn call after brush subtraction**

In `TerrainEditorTool.cs`, find the `ApplyBrush` method. After line 153 (after the foreach loop that applies the brush to volumes), add:

```csharp
// Spawn debris particles on subtract
if (op == BrushOp.Subtract && VoxelVFXManager.Instance != null)
{
    VoxelVFXManager.Instance.SpawnDebris(_currentHitPoint, brushRadius, _currentMaterialId);
}
```

The insertion should be after the brush application loop and before the structural analyzer check.

**Step 2: Add using statement**

At the top of the file, add:

```csharp
using VoxelEngine.Core.Effects;
```

**Step 3: Test in Unity**

1. Enter Play mode
2. Click and drag to subtract terrain
3. Verify particles spawn at brush location
4. Verify particles have correct material texture

**Step 4: Commit**

```bash
git add Assets/Scripts/VoxelEngine/Editing/TerrainEditorTool.cs
git commit -m "feat: hook VFX spawning into brush subtraction"
```

---

## Task 8: Polish and Tuning

**Files:**
- Modify: `Assets/VFX/VoxelDebris.vfx`

**Step 1: Tune particle parameters**

Test and adjust:
- Burst count (currently 30-50)
- Particle size range (currently 0.05-0.15)
- Lifetime range (currently 1.0-2.0)
- Gravity strength (currently -9.8)
- Drag (currently 0.5)
- Initial velocity magnitude

**Step 2: Verify material textures display correctly**

Test with different voxel materials:
- Grass (ID 0)
- Ice (ID 1)
- Trunk (ID 5)
- Leaf (ID 6)

**Step 3: Final commit**

```bash
git add Assets/VFX/VoxelDebris.vfx
git commit -m "polish: tune debris particle parameters"
```

---

## Summary

| Task | Description | Estimated Time |
|------|-------------|----------------|
| 1 | Extend raycast buffer with material ID | 15 min |
| 2 | Create VoxelVFXManager | 15 min |
| 3 | Create VFX Graph asset | 20 min |
| 4 | Create custom VFX shader | 20 min |
| 5 | Create VFX prefab | 10 min |
| 6 | Wire VFX Manager into scene | 5 min |
| 7 | Hook TerrainEditorTool to spawn VFX | 10 min |
| 8 | Polish and tuning | 15 min |
| **Total** | | **~1.5 hours** |
