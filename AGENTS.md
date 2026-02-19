# RayTraceVoxel Agent Guide

Purpose: orient agentic coders to this Unity repo, with CLI-first build/test guidance
and project-specific coding conventions. Keep this file short and practical.

## Repository Facts
- Unity project rooted at `C:\Unity\RayTraceVoxel`
- Unity Editor version: 6000.3.2f1
- Solution: `RayTraceVoxel.slnx` / `RayTraceVoxel.sln`
- Primary code: `Assets/Scripts/`

## Build / Lint / Test (CLI-first)

Note: This repo does not include a custom build pipeline or lint config. Use Unity
batchmode for tests/builds and rely on IDE analyzers + Unity warnings for lint.

### Run Unity EditMode tests
```
"C:\Program Files\Unity\Hub\Editor\6000.3.2f1\Editor\Unity.exe" -batchmode -nographics -quit \
  -projectPath "C:\Unity\RayTraceVoxel" \
  -runTests -testPlatform editmode \
  -testResults "C:\Unity\RayTraceVoxel\Logs\editmode-results.xml"
```

### Run Unity PlayMode tests
```
"C:\Program Files\Unity\Hub\Editor\6000.3.2f1\Editor\Unity.exe" -batchmode -nographics -quit \
  -projectPath "C:\Unity\RayTraceVoxel" \
  -runTests -testPlatform playmode \
  -testResults "C:\Unity\RayTraceVoxel\Logs\playmode-results.xml"
```

### Run a single test (EditMode or PlayMode)
Use `-testFilter` with the full test name (namespace.class.method).
```
"C:\Program Files\Unity\Hub\Editor\6000.3.2f1\Editor\Unity.exe" -batchmode -nographics -quit \
  -projectPath "C:\Unity\RayTraceVoxel" \
  -runTests -testPlatform editmode \
  -testFilter "Namespace.ClassName.TestMethod" \
  -testResults "C:\Unity\RayTraceVoxel\Logs\editmode-single.xml"
```

### Build (generic Unity CLI)
No build script found in repo. If you add one, call it with `-executeMethod`.
```
"C:\Program Files\Unity\Hub\Editor\6000.3.2f1\Editor\Unity.exe" -batchmode -nographics -quit \
  -projectPath "C:\Unity\RayTraceVoxel" \
  -executeMethod BuildScript.BuildPlayer
```

### Editor UI fallback
- Tests: Window -> General -> Test Runner (EditMode / PlayMode tabs)
- Build: File -> Build Settings -> Build

## Code Style Guide

### Imports
- Order: `System` -> `UnityEngine` -> `Unity.*` -> project namespaces
- Group with blank lines between import groups
- Avoid unused usings; keep file headers minimal

### Formatting
- 4 spaces indentation, braces on new lines
- Keep methods small and cohesive; prefer early returns
- Avoid trailing whitespace; keep line lengths reasonable

### Types
- Prefer explicit types for public/serialized fields and Unity API surfaces
- `var` is OK for obvious locals (e.g., `var node = ...`)
- Use `SerializeField` for private fields exposed in the Inspector

### Naming
- Types / methods / properties: PascalCase
- Locals and fields: camelCase
- Private fields: `_camelCase` when used
- Constants: PascalCase
- Unity message methods must use the Unity signature (`Awake`, `Start`, `Update`)

### Error Handling and Guards
- Use guard clauses for null singletons/components (Unity patterns)
- Log with context: `Debug.LogWarning("...", this)` or include object refs
- Avoid swallowing exceptions in editor tools or build scripts
- Prefer predictable failure to silent no-ops

### Unity/Engine Conventions
- Treat `MonoBehaviour` lifecycle carefully; no heavy work in constructors
- Dispose native containers in `OnDestroy` or `OnDisable` as appropriate
- Avoid allocations in `Update` hot paths when possible
- Use `RequireComponent` for hard dependencies

## Tests and Scenes
- Tests and debug scripts live under `Assets/Scripts/Tests/`
- Test scenes live under `Assets/Scenes/Tests/`
- When adding tests, keep them isolated and deterministic
- Prefer EditMode tests for pure logic; PlayMode only when necessary

## Common Pitfalls
- Unity batchmode requires a full Editor path (example on Windows):
  `C:\Program Files\Unity\Hub\Editor\6000.3.2f1\Editor\Unity.exe`
- Scene-dependent tests should load scenes explicitly if needed
- Large assets and generated folders should not be committed

## Suggested Review Checklist for Agents
- Tests updated/added if behavior changes
- No native container leaks (dispose paths covered)
- No new allocations in per-frame paths
- Inspector-facing fields are clear and documented with tooltips if needed

## Files to Know
- `Assets/Scripts/VoxelEngine/Streaming/WorldManager.cs`
- `Assets/Scripts/VoxelEngine/Memory/`
- `Assets/Scripts/VoxelEngine/Rendering/`
- `ProjectSettings/ProjectVersion.txt`
