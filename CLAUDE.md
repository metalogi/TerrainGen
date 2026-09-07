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
- 42 EditMode tests: the surface layer (cube face basis, the 24-entry adjacency table re-derived geometrically in the test, neighbour round-trips, node addressing, argument guards), the noise and height function (determinism to the bit, precision at large coordinates, seam agreement, band limiting), the mesh layout (counts, winding, handedness), and a Burst compilation witness
- **One test only means anything inside the Editor.** `BurstCompilationTests` needs a real Burst backend; everything else is UnityEngine-free by design and also runs outside the Editor (see the `unity-verification-without-batchmode` note). If Burst compilation is switched off in the Editor, that test reports Ignore rather than failing — a deliberate state, not a defect.

### Editor Scripts
- Editor scripts go in `Assets/Editor/` (assembly `Sonoma.Editor`) — **not** in an `Editor/` folder under `Assets/Scripts/`. Since M0 the project uses assembly definitions, and under the `Sonoma.Core` asmdef that folder name loses its magic and would be compiled into the runtime assembly.
- Runtime code under `Assets/Scripts/` compiles to `Sonoma.Core`; EditMode tests to `Sonoma.Tests.EditMode`. `Assembly-CSharp` is effectively empty.

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

2. **Surface Space**: `double3` positions on the curved base surface
   - `SurfaceDef` describes the topology (cube-sphere, plane grid, cylinder); `RootQuad` is one root of it
   - `SurfaceMath.SurfaceFrame(surface, root, u, v, …)` evaluates position and normal analytically
   - Height is added along the surface normal; this is where the height function is sampled

3. **Local Render Space**: Camera-relative float coordinates
   - Used for actual Unity rendering to maintain precision
   - Origin rebases when camera moves beyond threshold (~1000 units)

4. **World Absolute Space**: Double-precision global coordinates
   - Tracks true position in large worlds
   - Prevents floating-point precision issues at scale

**Implementation Note:** Heights are sampled at **surface positions**, never in local quadtree coordinates — that is what makes two chunks agree wherever they touch. Chunk vertices are then stored relative to a per-chunk `double3 Anchor`, so their float magnitude never exceeds the node size, and `transform.position = (float3)(Anchor − WorldOrigin)` places them.

### Surface Parameterization (Critical for Curved Topologies)

Bilinear interpolation of Cartesian corner positions is only exact for flat planes. On a curved surface, interpolated corners drift off the true surface — the error grows toward the interior of a quad and compounds through subdivision, giving a faceted approximation rather than the true curve.

**Rule:** evaluate positions analytically from `(u, v)`, never by interpolating corner positions. `SurfaceMath` dispatches on a `SurfaceType` enum with a `switch` — never an interface or virtual call, because Burst cannot devirtualise those:

- **Cube-sphere** — `a = tan((2u−1)·π/4)`, likewise `b`, then `normalize(centre + a·right + b·up)` scaled by radius. The tangent adjustment cuts the worst-case cell spacing ratio from 2.733 to 1.621 (measured over 16×16 cells on a face) and leaves face boundaries untouched, since `tan(±π/4) = ±1`.
- **Plane grid** — `Origin + (u·TileSize, 0, v·TileSize)`; normal is always `+Y`.
- **Cylinder** — angle and z interpolated parametrically, then evaluated; normal points **inward**, for interior viewing.

This ensures child chunks at any subdivision depth conform to the true curved surface, not a flat facet of the root quad.

### Quadtree Spatial Hierarchy

- **Root nodes**: one per `RootQuad` — six on a cube-sphere, `Cols × Rows` on a plane grid or cylinder
- **Addressing**: integer `NodeId(Quad, Depth, X, Y)`, not float bounds. A node is addressable without walking the tree, and is usable as a dictionary key.
- **Child nodes**: each node subdivides into 4; each level is 50% of the parent's `(u, v)` extent
- **Typical depth**: 8–12 levels. Reaching 1 m vertex spacing on an Earth-sized cube-sphere needs depth 19 at resolution 33.
- **Status**: **subdivision is not implemented yet.** M2 generates root quads only; `LodSelector` arrives in M3.

### Generation Pipeline (Burst)

Chunks depend on nothing but their own `NodeId` — no parent, no neighbour — so they generate in any order, on any thread, and a cancelled one can simply be dropped.

**Stage 1 — Height sampling (worker thread).** `HeightSampleJob` (`IJobParallelFor`) over an `(R+2)²` grid: the chunk's own vertices plus a one-vertex border so normals are two-sided at the edge. `CoarseHeightSampleJob` does the even vertices at the *parent's* band limit, for M3's geomorph targets. Output: `double3` base points, `float3` base normals, `float` heights.

**Stage 2 — Mesh building (worker thread).** `ChunkMeshJob` (`IJob`) writes `TerrainVertex` straight into a `Mesh.MeshData` obtained on the main thread, in chunk-local floats around the node's `Anchor`, and adds skirts.

**Stage 3 — Upload (main thread).** `Mesh.ApplyAndDisposeWritableMeshData` with `DontRecalculateBounds`; bounds come from the job's measured min/max height. The index buffer is a shared per-`(resolution, winding)` constant.

**Scheduler policy** (`GenerationScheduler`): priority queue keyed by `distance / NodeWorldSize` so near-and-coarse beats far-and-fine; in-flight jobs capped at `MaxInFlightJobs`; uploads bounded by `UploadBudgetMs` on a `Stopwatch`, **not** a chunk count, because upload cost scales with resolution.

### Height Function

Height is a **pure function of a surface position and a band limit** — same arguments, same bits, on any thread, in any order. That single property is what makes shared vertices agree without stitching, makes generation trivially parallel and cancellable, and makes the world reproducible from a seed.

There is deliberately **no parent→child heightmap chain**. Upsampling a parent and adding detail would force every chunk to depend on its whole ancestor chain, serialise generation top-down, forbid evicting ancestors, and *guarantee* that adjacent LODs disagree at shared vertices — which is what stitching then had to paper over. See `SonomaRevisedPlan.md` §2.5.

- **Double-precision lattice noise.** `LatticeNoise.Gradient3D(double3, uint)` takes the integer cell with a `double` floor and narrows to `float` exactly once, at `p − floor(p)`, which is always in `[0,1)`. Precision is then independent of distance from the origin.
- **Band-limited per depth.** `HeightParams.MaxOctave(d) = clamp(d + K0, 0, OctaveCount−1)`. A chunk carries only octaves its vertex spacing can represent, so a chunk and its parent evaluate deliberately *different* functions at the same point; M3's geomorph bridges them.
- **Zero-mean**, so the base surface is the mean rather than a floor.
- **Macro layer in, not up.** `MacroSample` is a placeholder holding zero until M5 adds the persistent per-root-quad maps. Non-local algorithms (erosion, rivers, tectonics) belong there, run once and coarsely — never at chunk time.

### Seam Handling

Stitching is **deleted, not fixed**. Because height is a pure function of position, two chunks that share a vertex compute the same value:

- **Within a root quad: bit-identical.** `ChunkGrid.VertexUV` is the only place a vertex index becomes a surface parameter, and `UMax(x)` and `UMin(x+1)` are the same bits.
- **Across a cube edge: bounded, not identical.** The two faces reach the shared edge by different parameterisations, so positions differ by 2.067e-16 relative (1.32 nm at Earth radius) and edge *normals* by up to 0.151° — the border sample continues its own face's parameterisation past the edge rather than crossing onto the neighbour's, so the central difference is not centred. Geometric rather than terrain-driven: still 0.1503° at `HeightScale` 0. A faint shading seam along the 12 cube edges, not a crack.
- **Skirts** remain as the fallback for transient states where the tree is more than one depth apart across an edge.

### LOD Transition Morphing (M3, not yet implemented)

Each fine vertex stores a morph target: the even vertex `(i & ~1, j & ~1)` evaluated at the parent's band limit. `ChunkMeshJob` already writes these into `TEXCOORD2`/`TEXCOORD3`; nothing reads them until M3 adds the vertex-shader morph. Fully morphed, a child mesh is triangle-for-triangle its parent — which is why the `(i00, i11, i10), (i00, i01, i11)` triangulation is load-bearing and must not be changed to a flipping scheme.

### Chunk Unloading

When resident chunk count exceeds budget: evict furthest first, in **complete sibling groups of four** — evicting a single node would leave its region uncovered. `ChunkPool` recycles the GameObject and its Mesh rather than destroying them; meshes are never destroyed during play. (The eviction policy itself lands with M3's `LodSelector`; M2 has only root chunks, so nothing is ever evicted.)

## Milestones (M0–M7)

The authoritative plan is `SonomaRevisedPlan.md` §5. The original 5-phase roadmap it replaced is gone; `Phase1Plan.md` and the `Docs/M*_Plan.md` files remain as historical records of what was decided at the time, and describe code that in several cases no longer exists.

- **M0 — Housekeeping.** Done. Assembly definitions, explicit Burst/Collections/Mathematics dependencies, dead code removed.
- **M1 — Surfaces and addressing.** Done. `RootQuad`, `NodeId`, `EdgeLink`, `SurfaceMath` with cube-sphere, plane grid and cylinder; the 24-entry cross-face adjacency table.
- **M2 — Height function and Burst generation.** Done. Double-precision lattice noise, the pure height function, the two sample jobs and the mesh job, `GenerationScheduler`, `ChunkPool`, and deletion of the whole prototype path. **Root quads only.**
- **M3 — LOD selection and geomorphing.** Next. `LodSelector` with bounding-sphere distance and `SplitFactor`, the vertex-shader morph, parent-until-children-ready swap, resident-chunk budget. Removes the last need for skirts.
- **M4 — Floating origin at scale.** Per-chunk double anchors exist already; M4 adds the camera/rig handling and the Earth-radius stress test.
- **M5 — Macro layer and biomes.** Fills in `MacroSample`: persistent per-root-quad maps, biome modulation, splat weights.
- **M6 — Collision, tooling, polish.** Colliders at the finest depths, debug overlay, heightmap cache, profiling against the 2 ms budget.
- **M7 — Content.** Vegetation and rock scattering, shader parameter generation, biome library.

## Code Organization

```
Assets/Scripts/
├── Core/
│   ├── Surface/            # NodeId, Edge, RootQuad, SurfaceDef, EdgeLink, SurfaceMath
│   ├── Generation/         # LatticeNoise, TerrainHeightFunction, HeightParams, MacroSample,
│   │                       #   ChunkGrid, ChunkMeshLayout, ChunkMeshBuffers,
│   │                       #   HeightSampleJob, CoarseHeightSampleJob, ChunkMeshJob,
│   │                       #   BurstWitnessJob
│   ├── Quadtree/           # GenerationScheduler, TerrainRoot (+ M3: LodSelector)
│   ├── Rendering/          # TerrainChunk, ChunkPool
│   └── CoordinateSpace/    # WorldOriginSystem (all that remains of the old coordinate layer)
├── Systems/
│   └── Configuration/      # TerrainSettings ScriptableObject (+ M5: BiomeDefinition)
└── Tools/                  # TopologyFlyCamera, SurfaceDebugDrawer (not in Sonoma namespace)
```

`TerrainRoot` is the scene entry point: it owns the `SurfaceDef`, the `ChunkPool` and the `GenerationScheduler`, and asks for the chunks that should exist.

Editor scripts live in `Assets/Editor/` (assembly `Sonoma.Editor`); EditMode tests live in `Assets/Tests/EditMode/` (assembly `Sonoma.Tests.EditMode`). There is deliberately no `Assets/Scripts/Editor/` — under the `Sonoma.Core` asmdef that folder name loses its magic and would be compiled into the runtime assembly.

All runtime systems use the `Sonoma.Core.*` or `Sonoma.Systems.*` namespace. Tool scripts in `Assets/Scripts/Tools/` are excluded from this convention.

## Technical Specifications

### Performance Targets
- **Frame Budget**: < 2ms per frame for all terrain updates
- **Generation Time**: Chunks ready before player arrival at max camera velocity
- **Memory Budget**: Configurable (typically 500-2000 active chunks)
- **Visible Triangles**: 50K-500K depending on LOD

### Mesh Specifications
- **Vertex Format** (`TerrainVertex`, 76 bytes): `POSITION` float3, `NORMAL` float3, `TEXCOORD0` float2 uv, `TEXCOORD1` float4 `(topoUp.xyz, elevation)`, `TEXCOORD2` float4 `(morph position.xyz, depth)`, `TEXCOORD3` float3 morph normal
- **Typical Resolutions**: 33, 65, 129 vertices per chunk edge. Must be **odd** — the geomorph target sub-grid is the even-indexed vertices, and an even resolution leaves it half a vertex out of step. Enforced in `HeightParams.Create`.
- **Index Format**: 16-bit, valid up to resolution **253** — the constraint is distinct vertices (`R² + 4R ≤ 65,536`), not triangle count. 32-bit is not implemented; `HeightParams.Create` refuses anything larger rather than letting it fail later inside the scheduler.
- **Counts**: `R² + 4R` vertices (grid plus one skirt row per edge), `2(R−1)² + 8(R−1)` triangles. At R=33 that is 1,221 vertices and 2,304 triangles.

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

- **Jobs System**: `IJobParallelFor` for height sampling, `IJob` for mesh building
- **Burst Compilation**: Mark generation jobs `[BurstCompile(CompileSynchronously = true)]`. Plain static helpers they call need no attribute — they compile as part of the calling job; the attribute only matters for function pointers.
- **NativeCollections**: Use `NativeArray<T>` for passing data to/from jobs
- **Mesh writing**: `Mesh.AllocateWritableMeshData` on the main thread, fill it in the job, then `Mesh.ApplyAndDisposeWritableMeshData` with `MeshUpdateFlags.DontRecalculateBounds`. Set buffer parameters before scheduling.
- **ScriptableObjects**: All configuration data (biomes, generation profiles, global settings)
- **Custom Inspectors**: Create custom editors for complex configuration data
- **Gizmos**: Draw quadtree bounds, LOD levels, camera ranges for debugging

## Key References

- **Design Review & Revised Plan (2026-09-05)**: `SonomaRevisedPlan.md` — **adopted and authoritative.** Flags the four structural flaws in the prototype (UV-sphere poles, generation-time stitching, float noise precision, synchronous generation) and lays out M0–M7 built on cube-sphere topology, geomorphing, and a pure double-precision height function in Burst. Its per-milestone plans are in `Docs/M*_Plan.md`; each of those ends with an "Amendments from implementing…" section recording where building the thing proved the plan wrong, which is usually the most useful part to read.
- **Design Document**: `SonomaOverview_Expanded.md` — **predates the review and is not current.** It still describes the UV sphere, a per-quad transformation matrix, and the parent→child heightmap chain, all of which were rejected. Read it for intent, not for design.
- **Unity Jobs Documentation**: https://docs.unity3d.com/Manual/JobSystem.html
- **Burst Compiler**: https://docs.unity3d.com/Packages/com.unity.burst@latest
- **URP Shader Graph**: For terrain material generation
