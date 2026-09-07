# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Sonoma is a Unity 6 (6000.4.4f1) project implementing a procedural terrain generation system using hierarchical heightmap-based terrain with quadtree LOD management. The system generates realistic natural landscapes from continent-scale down to third-person game scale with seamless LOD transitions.

**Current Status:** Milestones M0–M2 of `SonomaRevisedPlan.md` are done; that document supersedes the 5-phase roadmap below and is what to work against. The synchronous prototype is gone: generation runs as Burst jobs behind a priority scheduler, heights come from a pure double-precision function, and seam stitching was deleted rather than fixed. **M2 renders root quads only** — there is no subdivision until M3 adds `LodSelector`, so the scene is deliberately coarse.

**Implemented systems:**
- `Core/Surface/`: `NodeId`, `Edge`, `RootQuad`, `SurfaceDef`, `EdgeLink`, `SurfaceMath` — topology and integer node addressing (cube-sphere, plane grid, cylinder)
- `Core/Generation/`: `LatticeNoise`, `TerrainHeightFunction`, `HeightParams`, `MacroSample`, `ChunkGrid`, `ChunkMeshLayout`, `ChunkMeshBuffers`, `HeightSampleJob`, `CoarseHeightSampleJob`, `ChunkMeshJob`
- `Core/Quadtree/`: `GenerationScheduler`, `TerrainRoot` (MonoBehaviour, the scene entry point)
- `Core/Rendering/`: `TerrainChunk`, `ChunkPool`
- `Core/CoordinateSpace/`: `WorldOriginSystem` (all that remains of the old coordinate layer)
- `Systems/Configuration/`: `TerrainSettings` (ScriptableObject)
- `Tools/`: `TopologyFlyCamera`, `SurfaceDebugDrawer`

## Unity Development Commands

### Opening the Project
- Open Unity Hub and add this project directory
- Unity Editor Version: 6000.4.4f1
- The project uses Universal Render Pipeline (URP)

### Building
- Build through Unity Editor: File → Build Settings → Build
- Target Platform: Windows 64-bit (can be changed in Build Settings)
- No automated build scripts currently configured

### Testing
- Unity Test Runner: Window → General → Test Runner
- Run tests via Test Framework package (com.unity.test-framework@1.6.0)
- An EditMode test assembly exists at `Assets/Tests/EditMode/` (`Sonoma.Tests.EditMode`); run it from Window → General → Test Runner
- 21 EditMode tests cover the M1 surface layer: cube face basis, the 24-entry adjacency table (re-derived geometrically in the test), neighbour round-trips, node addressing, surface geometry and the argument guards

### Editor Scripts
- Editor scripts go in `Assets/Editor/` or `Assets/*/Editor/` folders
- Editor code compiles to Assembly-CSharp-Editor.csproj
- Runtime code compiles to Assembly-CSharp.csproj

### Important Unity Packages
- **URP** (17.3.0): Universal Render Pipeline - all rendering uses URP shaders
- **Input System** (1.17.0): Use new Input System, not legacy Input Manager
- **Burst Compiler**: Used for high-performance terrain generation jobs
- **Jobs System**: Multithreading for terrain generation pipeline
- **AI Navigation** (2.0.9): For NavMesh generation on procedural terrain

## Core Architecture

### Coordinate Systems (Critical Concept)

The system uses **four coordinate spaces** with transformations between them:

1. **Quadtree Space**: Local [0,1] normalized coordinates per quadtree node
   - All generation algorithms work in this simple Cartesian space
   - Z-axis represents height/elevation

2. **World Space**: Curved surfaces mapped from Quadtree Space
   - Supports planes, spheres, cylinders, or custom Base Mesh topologies
   - `BaseMeshQuad` stores a `SurfaceType` enum and parametric range, not just Cartesian corners
   - `CoordinateTransform` dispatches on `SurfaceType` and computes positions analytically

3. **Local Render Space**: Camera-relative float coordinates
   - Used for actual Unity rendering to maintain precision
   - Origin rebases when camera moves beyond threshold (~1000 units)

4. **World Absolute Space**: Double-precision global coordinates
   - Tracks true position in large worlds
   - Prevents floating-point precision issues at scale

**Implementation Note:** Always generate in Quadtree Space, transform to World Space, then offset to Local Render Space for rendering.

### Surface Parameterization (Critical for Curved Topologies)

Bilinear interpolation of Cartesian corner positions is only exact for flat planes. For spheres and cylinders, interpolating corners in 3D space produces points that drift off the true surface — the error grows toward the interior of a quad and compounds through subdivision levels, producing a faceted approximation rather than the true curve.

**Rule:** `CoordinateTransform.ToWorldPosition` must use topology-aware interpolation:

- **Plane** — bilinear interpolation of the four corner positions is exact
- **Sphere** — interpolate lat/lon parametrically, then compute `SpherePoint(lat, lon, radius)` analytically. Do **not** bilinearly interpolate Cartesian corners.
- **Cylinder** — interpolate angle and z parametrically, then compute `CylPoint(angle, z, radius)` analytically.

**Consequence for `BaseMeshQuad`**: the struct must store the parametric range for its surface type (lat0/lat1/lon0/lon1 for sphere; angle0/angle1/z0/z1 for cylinder), not just the four corner positions. A `SurfaceType` enum field drives dispatch in `CoordinateTransform`. Corner positions may still be stored for plane quads where bilinear interpolation is used.

This ensures child chunks at any subdivision depth conform to the true curved surface, not a flat facet of the root quad.

### Quadtree Spatial Hierarchy

- **Root nodes**: One per Base Mesh quad (defines world topology)
- **Child nodes**: Each node subdivides into 4 children based on camera distance
- **Typical depth**: 8-12 levels (configurable)
- **Node size**: Each level is 50% of parent size
- **LOD transitions**: Distance-based with hysteresis to prevent thrashing

### Generation Pipeline (Multithreaded)

**Stage 1 - Height Generation (Worker Thread)**
- Input: Parent heightmap, biome data, noise parameters
- Process: Multi-octave noise, biome modulation
- Technology: Unity Jobs + Burst compilation
- Output: Float array heightmap

**Stage 2 - Mesh Generation (Worker Thread)**
- Input: Heightmap, neighbor info for seam stitching
- Process: Vertex positions, normals, UVs
- Technology: Unity Jobs + Burst compilation
- Output: Vertex/index buffers (NativeArray)

**Stage 3 - Mesh Upload (Main Thread)**
- Create Unity Mesh, upload to GPU
- Must happen on main thread (Unity API requirement)

**Stage 4 - Material Assignment (Main Thread)**
- Assign materials, textures, place vegetation
- Biome-driven material parameters

**Synchronization:** Job dependencies ensure parent levels complete before children. Budget max 3 chunks/frame for mesh uploads to maintain framerate.

### Hierarchical Generation Levels

**Level 0 - Continental (Base Resolution):** Large-scale noise/tectonic simulation generating low-res heightmap (~128×128 per Base Mesh quad), biome classification, climate data (temperature/rainfall), and major feature metadata (mountain ranges, coastlines).

**Exception to "generate in Quadtree Space":** Level 0 has no parent to inherit edge values from, so adjacent root quads would produce mismatching edges if noise is sampled in local [0,1] space. Level 0 generation must receive the world-space transform and sample noise in surface-space coordinates:
- **Sphere**: sample 3D noise at the actual point on the sphere surface — topologically adjacent points on different root quads are spatially adjacent in 3D noise space, so wrapping is implicit
- **Cylinder**: use cylindrical parameterization for the wrap axis (map to `sin/cos`) so the seam closes naturally
- **Plane**: local quadtree coordinates work fine; no wrapping required

Levels 1–N don't share this constraint — they inherit correct edge values from their parent heightmap and seam-stitch to neighbors, so continuity is guaranteed structurally.

**Levels 1–N - Progressive Refinement:** Each level:
1. Upsample parent heightmap 2×
2. Add detail noise at current frequency
3. Apply biome-specific modulation (flatten plains, sharpen mountains)
4. Derive slope/curvature maps for material blending and vegetation density

Typical 4–8 refinement levels depending on scale range.

### Seam Elimination Strategy

Uses **T-junction stitching** to prevent cracks between LOD levels:

1. **Edge Classification**: Interior (same LOD) / Transition (different LOD) / Boundary
2. **Shared Vertex Policy**: Higher-detail chunks sample coarser neighbor edge vertices
3. **Edge Locking**: Constrain boundary vertices to match neighbor heightmap exactly
4. **Skirt Fallback**: Thin vertical skirts under terrain edges as backup

**Critical:** When generating a chunk, always query neighbor chunks and lock edge vertices if neighbor is coarser LOD.

### LOD Transition Morphing

Vertex blend parameter for smooth transitions: `blend = saturate((distance - minDistance) / transitionRange)`. Morph vertices toward parent LOD positions as camera approaches transition distance. Hysteresis factor (e.g. 1.2×) prevents subdivision/collapse thrashing.

### Chunk Unloading

When loaded chunk count exceeds budget: sort by camera distance, unload furthest first. Always preserve chunks that are parents of loaded children. Retain generation seed/parameters for fast regeneration; optionally cache heightmap data to skip Stage 1 on reload.

## Implementation Phases (5-Phase Roadmap)

### Phase 1: Foundation (Complete)
- [x] Base Mesh system and coordinate transformation matrices
- [x] Quadtree data structure (QuadtreeNode, bounds, children)
- [x] Camera distance queries for LOD decisions
- [x] Basic single-level heightmap generation
- [x] Origin rebasing system for large worlds

### Phase 2: LOD and Streaming (Complete)
- [x] Multi-level quadtree with distance-based subdivision/collapse
- [x] T-junction seam stitching implementation
- [x] Chunk loading/unloading based on camera distance
- [x] Memory budget tracking (`MaxActiveChunks`, default 500)

### Phase 3: Generation Pipeline (Current Target)
- [ ] Hierarchical generation with parent→child dependencies
- [ ] Multi-threaded generation using Jobs/Burst
- [ ] Biome system with feature placement
- [ ] Material and texture generation per chunk

### Phase 4: Polish and Optimization
- [ ] LOD transition morphing (vertex blending near transitions)
- [ ] Generation caching system for frequently visited areas
- [ ] Editor debug visualization (quadtree bounds, LOD levels)
- [ ] Performance profiling overlay

### Phase 5: Content and Assets
- [ ] Vegetation placement system
- [ ] Rock and detail object scattering
- [ ] Shader parameter generation (splat maps, normal maps)
- [ ] Biome definition library (ScriptableObjects)

## Code Organization

```
Assets/Scripts/
├── Core/
│   ├── Quadtree/           # QuadtreeBounds, QuadtreeNode, QuadtreeManager
│   ├── Generation/         # HeightmapGenerator (+ future MeshGenerator, BiomeSystem)
│   ├── Rendering/          # TerrainChunk (+ future ChunkMeshRenderer, MaterialGenerator)
│   └── CoordinateSpace/    # BaseMeshQuad, BaseMeshFactory, CoordinateTransform, WorldOriginSystem
├── Systems/
│   ├── Streaming/          # (Phase 2: ChunkLoader, MemoryManager)
│   ├── Performance/        # (Phase 4: ProfilingSystem, PerformanceMonitor)
│   └── Configuration/      # TerrainSettings ScriptableObject
└── Tools/                  # TopologyFlyCamera (not in Sonoma namespace)
```

Editor scripts live in `Assets/Editor/` (assembly `Sonoma.Editor`); EditMode tests live in `Assets/Tests/EditMode/` (assembly `Sonoma.Tests.EditMode`). There is deliberately no `Assets/Scripts/Editor/` — under the `Sonoma.Core` asmdef that folder name loses its magic and would be compiled into the runtime assembly.

All runtime systems use the `Sonoma.Core.*` or `Sonoma.Systems.*` namespace. Tool scripts in `Assets/Scripts/Tools/` are excluded from this convention.

## Technical Specifications

### Performance Targets
- **Frame Budget**: < 2ms per frame for all terrain updates
- **Generation Time**: Chunks ready before player arrival at max camera velocity
- **Memory Budget**: Configurable (typically 500-2000 active chunks)
- **Visible Triangles**: 50K-500K depending on LOD

### Mesh Specifications
- **Vertex Format**: Position, Normal, UV (optional: Tangent, Color)
- **Typical Resolutions**: 33x33, 65x65, 129x129, 257x257 vertices per chunk
- **Index Format**: 16-bit for small chunks, 32-bit for large chunks

### Data-Driven Configuration
All generation parameters should be ScriptableObjects:
- **Global Settings**: World scale, quadtree depth, generation distances
- **Per-Level Settings**: Chunk resolution, visibility distance, noise parameters
- **Biome Definitions**: Height ranges, slope constraints, vegetation density curves

## Important Design Decisions

1. **Heightmap-Based Only**: No voxels or overhangs (caves/overhangs are future work or handled with separate meshes)
2. **Deterministic Generation**: Same seed → same terrain (important for networking/saves)
3. **Collision Meshes**: Use simplified versions of visual mesh (separate LOD chain)
4. **Water Bodies**: Separate system, not integrated into heightmap
5. **Static Terrain**: No runtime deformation or destruction in initial implementation

## Non-Obvious Implementation Details

- **The topologies disagree on the handedness of `(u, v, normal)`, and it decides triangle winding.** Measured `dot(cross(du, dv), n)` is **+2.467e-2** on every cube face but **−1.0e-4** on a plane grid and **−7.854e-4** on a cylinder. One fixed vertex order therefore cannot face outward on all three: the prototype's triangulation was written for a plane, and a cube-sphere built with it renders **inside out**. `SurfaceMath.UvFrameIsRightHanded` classifies it and `ChunkMeshLayout.BuildIndices` takes it as `flipWinding`. Vertex normals do *not* use the flag — `ChunkMeshJob` orients them with `dot(n, baseNormal) < 0`, which is correct on any topology without one. Two tests guard this; the classification switch is re-derived numerically, as with the adjacency table.
- **The band limit must come from depth, never from measured node size.** `HeightParams.MaxOctave(depth)` is `clamp(depth + K0, 0, OctaveCount-1)`. Actual node size varies across a cube face by up to 1.33:1 at a fixed depth, which can straddle a `floor(log2(...))` boundary and give two same-depth neighbours different octave counts — different functions along a shared edge, i.e. a crack. `K0` is read off the IEEE exponent rather than via `log2`, because the reference config lands on exactly 16 and a library log returning 3.9999999999999996 would drop an octave worldwide.
- **`ChunkGrid.VertexUV` is the only place a vertex index becomes a surface parameter.** The exact `math.lerp(UMin, UMax, i / (double)(R-1))` is load-bearing: `UMax(x)` and `UMin(x+1)` are bit-identical, so both sides of a same-quad seam produce identical `double3`s and identical heights. Rewriting it as `UMin + (UMax - UMin) * t` is algebraically equal, changes the last bits, and silently turns an exact seam into an approximate one.
- **Configuration is validated once, in `HeightParams.Create`, and nowhere later.** Chunk resolution must be odd, at least 3, and no greater than `ChunkMeshLayout.MaxResolutionFor16BitIndices` (253). The upper bound matters as much as the others: without it an oversized resolution is accepted at setup and first fails inside `GenerationScheduler.Upload`, after the jobs have allocated their persistent buffers and on a path that has already dropped the `Pending` and so cannot free them.
- **`ChunkPool` owns every mesh it created, not just the released ones.** `Dispose` walks a `_all` list rather than the `_free` stack, because at teardown every live chunk is still checked out — a Dispose over the free stack alone frees nothing.
- **Seams are exact within a root quad and merely bounded across one — for normals as well as positions.** Across a cube edge the two faces reach the shared edge by different parameterisations, so the surface points differ by 2.067e-16 relative (1.32 nm at Earth radius). `SonomaRevisedPlan.md` §5 asks for bit-identical heights "including across a cube edge"; that half is unachievable. Skirts cover it. **Normals split the same way and by much more:** the `(R+2)` border sample continues *this* face's tangent-adjusted parameterisation past its own edge instead of crossing onto the neighbour's, so the central difference is not centred. Measured disagreement is **1.9e-6° within a root quad but 0.151° across a cube edge** — and it is geometric, not terrain-driven: at `HeightScale = 0`, on a perfect sphere, it is still 0.1503°. That is a faint shading discontinuity along the 12 cube edges, most likely to show in a specular highlight at a grazing angle; it is not a crack. `HeightFunctionTests.CrossFaceEdgeNormalsAgreeToABoundedAngle` pins both regimes. Do not describe edge normals as agreeing "by construction" without saying which side of a root quad boundary you mean.
- **The cube adjacency table is hand-entered but test-guarded.** `SurfaceMath.CubeEdgeLink` holds 24 entries, 8 of which reverse the along-edge parameter. `CubeAdjacencyTests` re-derives the whole table from the face basis and compares, so edit the basis or the table only with that test running.
- **Cube face basis for +Y and -Y looks wrong but isn't.** Faces 2 and 3 use `up = (0,0,-1)` and `(0,0,1)` respectively; those are what make `right x up == centre` hold for every face. "Tidying" them silently invalidates the adjacency table.
- **The topology class is `SurfaceMath`, not `Surface`.** A type may not share the name of its own namespace: from a sibling namespace such as `Sonoma.Core.Generation`, with usings above the namespace (the house style), the simple name `Surface` binds to the namespace `Sonoma.Core.Surface` and `Surface.SurfacePoint(...)` fails with CS0234. Renaming it back breaks every M2 caller.
- **`NodeId.Parent` throws on a root.** C# masks shift counts, so a `Depth` of -1 would make `Span` evaluate `1 << 31` == `int.MinValue` and the UV accessors return small negative values instead of failing. Use `IsRoot` or `TryGetParent` when ascending.
- **The surface layer refuses malformed arguments rather than absorbing them.** `FaceCentre`/`FaceRight`/`FaceUp` spell out face 5 and throw on anything outside 0..5; `CubeEdgeLink` validates face and edge *before* flattening them to `face * 4 + edge`, because an out-of-range edge otherwise aliases into the next face's row; `Neighbour` validates `NodeId.Quad`, `X`, `Y` and `Depth`. All the guard messages are compile-time constants so the throw sites stay Burst-compilable. This is deliberate and test-covered — the catch-all versions returned plausible-looking geometry for a bad quad index, which surfaces later as terrain in the wrong place instead of failing at the call site.
- **`RootQuad.Index` is the quad's position in the `BuildRoots` array**, for every topology (and, for a cube-sphere, also the face). `NodeWorldSize` takes both a `RootQuad` and a `NodeId` that already carries its quad, so it checks the two agree and throws if they do not — a mismatch would otherwise return a wrong-but-plausible size and quietly bias LOD.
- **`SurfaceDebugDrawer` bounds its own gizmo cost.** `OnDrawGizmos` runs per SceneView repaint, so `DrawDepth` is capped at 4 and `EdgeSegments` is scaled down to keep the total near `MaxGizmoLines`. Raising the depth costs curvature detail, not responsiveness; unbounded it issued ~393k `Gizmos.DrawLine` calls per repaint and locked the Editor.
- **`TerrainChunk` has two static sets**: `AllChunks` (every chunk, including `SetActive(false)` ones) and `AllActive` (only enabled). `WorldOriginSystem` iterates `AllChunks` so hidden chunks aren't missed during an origin rebase, and rebases each from its `double3 Anchor` rather than shifting by a delta — the prototype's `position -= shift` accumulated float error over a session.
- **`TEXCOORD1` is spoken for.** `SonomaTerrainTriplanar.shader` reads it as `(topoUp.xyz, elevation)`. The geomorph attributes M3 needs live at `TEXCOORD2` (morph position + depth) and `TEXCOORD3` (morph normal); `ChunkMeshJob` writes all of them now even though nothing reads them until M3, so the vertex layout is settled once.
- **Skirts are `4R` vertices, one row per edge, and `8(R-1)` triangles.** Two triangles per skirt quad, four edges — a `4(R-1)` triangle count is the easy mistake and sizes the index buffer at half what it needs. The four edges are walked anticlockwise as seen from outside (South +u, East +v, North −u, West −v) so one triangle order serves all four. The prototype's reversed edge was **North**, not West as an earlier note here claimed.
- **`ChunkMeshLayout` is UnityEngine-free on purpose** so its arithmetic runs in the out-of-Editor test harness; `ChunkMeshBuffers` holds everything that needs `UnityEngine.Rendering`. Keep the split — it is what catches buffer-sizing bugs as test failures rather than as memory corruption.
- **The scheduler checks "still wanted" twice**, before scheduling and again on completion, and disposes unwanted results instead of showing them. This is the fix for the prototype's orphan-chunk leak, where a collapsed node stayed queued and later produced a chunk nothing referenced.
- **Jobs are `[BurstCompile(CompileSynchronously = true)]`**; `TerrainHeightFunction` and `LatticeNoise` carry no attribute because plain static methods compile as part of the calling job. `LatticeNoise`'s gradient table is a `switch`, not a `static readonly float3[]` — Burst cannot read a managed static array from a job and silently falls back if you give it one.

## Unity-Specific Best Practices for This Project

- **Jobs System**: Use `IJobParallelFor` for heightmap generation, `IJob` for mesh building
- **Burst Compilation**: Mark all generation jobs with `[BurstCompile]` attribute
- **NativeCollections**: Use `NativeArray<T>` for passing data to/from jobs
- **Mesh.SetVertexBufferData**: Use for efficient mesh uploads from native arrays
- **ScriptableObjects**: All configuration data (biomes, generation profiles, global settings)
- **Custom Inspectors**: Create custom editors for complex configuration data
- **Gizmos**: Draw quadtree bounds, LOD levels, camera ranges for debugging

## Key References

- **Design Review & Revised Plan (2026-09-05)**: See `SonomaRevisedPlan.md` — flags structural flaws in the prototype (UV-sphere poles, generation-time stitching, float noise precision, synchronous generation) and lays out milestones M0–M7 built on cube-sphere topology, geomorphing, and a pure double-precision height function in Burst. Supersedes the phase status above until adopted or rejected.
- **Design Document**: See `SonomaOverview_Expanded.md` for complete technical specification
- **Unity Jobs Documentation**: https://docs.unity3d.com/Manual/JobSystem.html
- **Burst Compiler**: https://docs.unity3d.com/Packages/com.unity.burst@latest
- **URP Shader Graph**: For terrain material generation
