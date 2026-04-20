# Phase 1 — Step 1 Checklist

This checklist verifies the implementation and basic runtime behaviour for Phase 1 Step 1 (Base Mesh, Coordinate Transform, Quadtree skeleton, origin rebasing, basic heightmap and demo spawn).

How to use: follow each step in the Unity Editor and mark PASS / FAIL. Path references are workspace-relative.

- [ PASS] **Create Terrain Settings asset:** `Assets/Create/Sonoma/Terrain Settings (Default)` → file created at `Assets/Settings/TerrainSettings.asset`. PASS: asset exists and contains `ChunkResolution`, `LodDistances`.

- [PASS ] **Open sample scene:** Open `Assets/Scenes/SampleScene.unity` and add GameObjects:
  - Add `QuadtreeManager` component to an empty GameObject.
  - Add `DemoTerrainSpawner` component to an empty GameObject.
  - Assign the `TerrainSettings` asset to both components' `Settings` field (if present).
  PASS: components accept the asset without errors.

- [ PASS] **Assign a material for demo spawn:** Create or assign a simple material (URP/Lit or Standard) to `DemoTerrainSpawner.Material`. PASS: no missing shader warnings.

- [ PASS] **Play the scene and verify mesh spawn:** Press Play. A GameObject named `DemoTerrainChunk` should appear with a visible mesh.
  PASS: mesh renders, no console errors or exceptions.

- [PASS ] **Verify vertex displacement:** Inspect `DemoTerrainChunk` mesh in the Inspector — vertex Y values should vary (not a flat plane). PASS: height variation visible.

- [PASS] **Quadtree LOD reaction (smoke test):** With `QuadtreeManager` in scene, move camera closer to the demo chunk (Scene view or in-game) and observe `QuadtreeNode` subdivision state via Debug/Inspector (or add temporary logs).
  PASS: nodes subdivide when camera gets close and collapse when moved away; no infinite subdivide logs.

- [PASS ] **Origin rebase test:** In Play mode, move the camera (or player) beyond `WorldOriginSystem.RebaseThreshold` (~1000). PASS: camera snaps to origin, `DemoTerrainChunk` shifts to keep world appearance consistent, no visible pops.

- [ PASS] **No editor compile errors:** After these changes there should be zero compile errors in the Console. PASS: Console clean.

- [ PASS] **File locations to inspect:**
  - `Assets/Scripts/Core/CoordinateSpace/BaseMeshQuad.cs`
  - `Assets/Scripts/Core/CoordinateSpace/CoordinateTransform.cs`
  - `Assets/Scripts/Core/CoordinateSpace/BaseMeshFactory.cs`
  - `Assets/Scripts/Core/Quadtree/QuadtreeBounds.cs`
  - `Assets/Scripts/Core/Quadtree/QuadtreeNode.cs`
  - `Assets/Scripts/Core/Quadtree/QuadtreeManager.cs`
  - `Assets/Scripts/Core/Generation/HeightmapGenerator.cs`
  - `Assets/Scripts/Core/Rendering/TerrainChunk.cs`
  - `Assets/Scripts/Core/CoordinateSpace/WorldOriginSystem.cs`
  - `Assets/Scripts/Systems/Configuration/TerrainSettings.cs`
  - `Assets/Scripts/Tools/DemoTerrainSpawner.cs`

- [ ] **Notes / Known limitations:**
  - Heightmap generation currently uses a CPU Perlin-based generator (`HeightmapGenerator.Generate`). Future work: replace with Burst `IJob` implementation.
  - `QuadtreeManager` currently uses a single plane root produced by `BaseMeshFactory.CreatePlaneQuad`; sphere/cylinder factory methods are not yet implemented.
  - No seam-stitching or mesh uploader (jobs) implemented in this step — expect CPU-side mesh creation for demo.

If any step fails, capture the Console output and filename/line references and attach them in an Issue for triage.
