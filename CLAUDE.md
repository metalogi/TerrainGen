# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Sonoma is a Unity 6 (6000.4.4f1) project implementing a procedural terrain generation system using hierarchical heightmap-based terrain with quadtree LOD management. The system generates realistic natural landscapes from continent-scale down to third-person game scale with seamless LOD transitions.

**Current Status:** Milestones M0–M3 of `SonomaRevisedPlan.md` are done; that document supersedes the 5-phase roadmap below and is what to work against. The synchronous prototype is gone: generation runs as Burst jobs behind a priority scheduler, heights come from a pure double-precision function, and seam stitching was deleted rather than fixed. M3a added subdivision (`LodMath`, `LodSelector`); M3b added the vertex-shader geomorph across all four passes, so LOD transitions no longer pop and skirts are down to their intended role — the fallback for transient states where the tree is briefly more than one depth apart across an edge.

**Implemented systems:**
- `Core/Surface/`: `NodeId`, `Edge`, `RootQuad`, `SurfaceDef`, `EdgeLink`, `SurfaceMath` — topology and integer node addressing (cube-sphere, plane grid, cylinder)
- `Core/Generation/`: `LatticeNoise`, `TerrainHeightFunction`, `HeightParams`, `MacroSample`, `ChunkGrid`, `ChunkMeshLayout`, `ChunkMeshBuffers`, `HeightSampleJob`, `CoarseHeightSampleJob`, `ChunkMeshJob`
- `Core/Quadtree/`: `LodMath`, `LodSelector`, `GenerationScheduler`, `TerrainRoot` (MonoBehaviour, the scene entry point)
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
- 59 EditMode tests: the surface layer (cube face basis, the 24-entry adjacency table re-derived geometrically in the test, neighbour round-trips, node addressing, argument guards), the noise and height function (determinism to the bit, precision at large coordinates, seam agreement, band limiting), the mesh layout (counts, winding, handedness), the LOD thresholds (exact range nesting, the measured-size mutation, the `MorphStartFraction` bound and its inversion for terrain relief, hysteresis, child/parent vertex coincidence), the selector's policy (atomic swap, sibling-group eviction, the preload margin actually firing, zero per-frame allocation), the geomorph invariant (a fully morphed child is its parent, vertex for vertex and triangle for triangle) and a guard that the morph is applied in all four shader passes, LOD boundary agreement over a simulated selection, and a Burst compilation witness
- **Three test files only mean anything inside the Editor.** `BurstCompilationTests` needs a real Burst backend; `LodSelectorTests` needs `Mesh`, `GameObject` and the job system; `GeomorphTests` needs both. Everything else is UnityEngine-free by design and also runs outside the Editor (see the `unity-verification-without-batchmode` note) — 52 of the 59 do. If Burst compilation is switched off in the Editor, the witness test reports Ignore rather than failing — a deliberate state, not a defect.

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
- **Status**: implemented in M3a. `LodSelector` descends from each root every frame, keyed on the branch containing the camera, so the wanted set is O(MaxDepth) deep rather than O(4^MaxDepth) wide. At the shipped configuration (`MaxDepth` 8, `SplitFactor` 4, `PreloadFactor` 1.15) that wanted set is **2970 nodes worst case over a face, 2430 near a face centre** — which is why `MaxResidentChunks` defaults to 3200 and not the 500 M2 carried. The counts are scale-free: a 300 m sphere and an Earth-radius one give the same figures.

### Generation Pipeline (Burst)

Chunks depend on nothing but their own `NodeId` — no parent, no neighbour — so they generate in any order, on any thread, and a cancelled one can simply be dropped.

**Stage 1 — Height sampling (worker thread).** `HeightSampleJob` (`IJobParallelFor`) over an `(R+2)²` grid: the chunk's own vertices plus a one-vertex border so normals are two-sided at the edge. `CoarseHeightSampleJob` does the even vertices at the *parent's* band limit, for M3b's geomorph targets. Output: `double3` base points, `float3` base normals, `float` heights.

**Stage 2 — Mesh building (worker thread).** `ChunkMeshJob` (`IJob`) writes `TerrainVertex` straight into a `Mesh.MeshData` obtained on the main thread, in chunk-local floats around the node's `Anchor`, and adds skirts.

**Stage 3 — Upload (main thread).** `Mesh.ApplyAndDisposeWritableMeshData` with `DontRecalculateBounds`; bounds come from the job's measured min/max height. The index buffer is a shared per-`(resolution, winding)` constant.

**Scheduler policy** (`GenerationScheduler`): priority queue keyed by `distance / NodeWorldSize` so near-and-coarse beats far-and-fine; in-flight jobs capped at `MaxInFlightJobs`; uploads bounded by `UploadBudgetMs` on a `Stopwatch`, **not** a chunk count, because upload cost scales with resolution.

### Height Function

Height is a **pure function of a surface position and a band limit** — same arguments, same bits, on any thread, in any order. That single property is what makes shared vertices agree without stitching, makes generation trivially parallel and cancellable, and makes the world reproducible from a seed.

There is deliberately **no parent→child heightmap chain**. Upsampling a parent and adding detail would force every chunk to depend on its whole ancestor chain, serialise generation top-down, forbid evicting ancestors, and *guarantee* that adjacent LODs disagree at shared vertices — which is what stitching then had to paper over. See `SonomaRevisedPlan.md` §2.5.

- **Double-precision lattice noise.** `LatticeNoise.Gradient3D(double3, uint)` takes the integer cell with a `double` floor and narrows to `float` exactly once, at `p − floor(p)`, which is always in `[0,1)`. Precision is then independent of distance from the origin.
- **Band-limited per depth.** `HeightParams.MaxOctave(d) = clamp(d + K0, 0, OctaveCount−1)`. A chunk carries only octaves its vertex spacing can represent, so a chunk and its parent evaluate deliberately *different* functions at the same point; M3b's geomorph bridges them.
- **Zero-mean**, so the base surface is the mean rather than a floor.
- **Macro layer in, not up.** `MacroSample` is a placeholder holding zero until M5 adds the persistent per-root-quad maps. Non-local algorithms (erosion, rivers, tectonics) belong there, run once and coarsely — never at chunk time.

### Seam Handling

Stitching is **deleted, not fixed**. Because height is a pure function of position, two chunks that share a vertex compute the same value:

- **Within a root quad: bit-identical.** `ChunkGrid.VertexUV` is the only place a vertex index becomes a surface parameter, and `UMax(x)` and `UMin(x+1)` are the same bits.
- **Across a cube edge: bounded, not identical.** The two faces reach the shared edge by different parameterisations, so positions differ by 2.067e-16 relative (1.32 nm at Earth radius) and edge *normals* by up to 0.151° — the border sample continues its own face's parameterisation past the edge rather than crossing onto the neighbour's, so the central difference is not centred. Geometric rather than terrain-driven: still 0.1503° at `HeightScale` 0. A faint shading seam along the 12 cube edges, not a crack.
- **Skirts** remain as the fallback for transient states where the tree is more than one depth apart across an edge.

### LOD Transition Morphing

Each fine vertex stores a morph target: the even vertex `(i & ~1, j & ~1)` evaluated at the parent's band limit, written by `ChunkMeshJob` into `TEXCOORD2` (position + depth) and `TEXCOORD3` (normal + elevation). `SonomaTerrainTriplanar.shader` reads them in **all four passes** and lerps position, normal and elevation by `k = saturate((dist − start_d) / (end_d − start_d))`, with `(start_d, end_d)` coming from the `_SonomaMorphRanges` global array that `TerrainRoot.PushMorphRanges` sets each frame.

`end_d` is the split distance of depth **d−1**, not of depth d: a chunk finishes morphing where its *parent* takes over, not where it hands over to its own children. `SplitFactor` and `MorphStartFraction` are bound together by `LodMath.MaxMorphStartFraction`, which is why the defaults are 4 and 0.15 rather than the plan's 2 and 0.4.

Fully morphed, a child mesh is triangle-for-triangle its parent: three of every four child cells collapse to zero area and the fourth reproduces a parent cell with the same winding. That is why the `(i00, i11, i10), (i00, i01, i11)` triangulation is load-bearing and must not be changed to a flipping scheme. `GeomorphTests.ChildAtFullMorphEqualsParent` asserts both halves — that each morph target *is* the corresponding parent vertex, and that the surviving triangles are the parent's index for index.

### Chunk Unloading

When resident chunk count exceeds budget: evict furthest first, in **complete sibling groups of four** — evicting a single node would leave its region uncovered. `ChunkPool` recycles the GameObject and its Mesh rather than destroying them; meshes are never destroyed during play. Implemented in M3a as `LodSelector.EvictToBudget`, which collapses a group into its already-resident parent. The collapsible groups are gathered in one pass and then drained furthest-first from a cached per-node distance; a group that becomes collapsible only because its own children have just gone waits for the next frame.

## Milestones (M0–M7)

The authoritative plan is `SonomaRevisedPlan.md` §5. The original 5-phase roadmap it replaced is gone; `Phase1Plan.md` and the `Docs/M*_Plan.md` files remain as historical records of what was decided at the time, and describe code that in several cases no longer exists.

- **M0 — Housekeeping.** Done. Assembly definitions, explicit Burst/Collections/Mathematics dependencies, dead code removed.
- **M1 — Surfaces and addressing.** Done. `RootQuad`, `NodeId`, `EdgeLink`, `SurfaceMath` with cube-sphere, plane grid and cylinder; the 24-entry cross-face adjacency table.
- **M2 — Height function and Burst generation.** Done. Double-precision lattice noise, the pure height function, the two sample jobs and the mesh job, `GenerationScheduler`, `ChunkPool`, and deletion of the whole prototype path. **Root quads only.**
- **M3a — LOD selection.** Done. `LodMath` (depth-only thresholds, validated `MorphStartFraction`), `LodSelector` with bounding-sphere distance, preload margin, the parent-until-children-ready swap and sibling-group eviction against `MaxResidentChunks`.
- **M3b — Geomorphing.** Done. The vertex-shader morph across all four passes, fed by the `_SonomaMorphRanges` global array; the child-at-full-morph-equals-parent invariant test. Skirts stay in the codebase as the transient-state fallback; `TerrainSettings.SkirtsEnabled` turns them off to prove the morph.
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
│   ├── Quadtree/           # LodMath, LodMathSettings, LodSelector,
│   │                       #   GenerationScheduler, TerrainRoot
│   ├── Rendering/          # TerrainChunk, ChunkPool
│   └── CoordinateSpace/    # WorldOriginSystem (all that remains of the old coordinate layer)
├── Systems/
│   └── Configuration/      # TerrainSettings ScriptableObject (+ M5: BiomeDefinition)
└── Tools/                  # TopologyFlyCamera, SurfaceDebugDrawer (not in Sonoma namespace)
```

`TerrainRoot` is the scene entry point: it owns the `SurfaceDef`, the `ChunkPool`, the `GenerationScheduler` and the `LodSelector`, and drives them once per frame — selector first, then scheduler.

Editor scripts live in `Assets/Editor/` (assembly `Sonoma.Editor`); EditMode tests live in `Assets/Tests/EditMode/` (assembly `Sonoma.Tests.EditMode`). There is deliberately no `Assets/Scripts/Editor/` — under the `Sonoma.Core` asmdef that folder name loses its magic and would be compiled into the runtime assembly.

All runtime systems use the `Sonoma.Core.*` or `Sonoma.Systems.*` namespace. Tool scripts in `Assets/Scripts/Tools/` are excluded from this convention.

## Technical Specifications

### Performance Targets
- **Frame Budget**: < 2ms per frame for all terrain updates
- **Generation Time**: Chunks ready before player arrival at max camera velocity
- **Memory Budget**: Configurable (typically 500-2000 active chunks)
- **Visible Triangles**: 50K-500K depending on LOD

### Mesh Specifications
- **Vertex Format** (`TerrainVertex`, 80 bytes): `POSITION` float3, `NORMAL` float3, `TEXCOORD0` float2 uv, `TEXCOORD1` float4 `(topoUp.xyz, elevation)`, `TEXCOORD2` float4 `(morph position.xyz, depth)`, `TEXCOORD3` float4 `(morph normal.xyz, morph elevation)`
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
- **The LOD thresholds must come from depth too, and for the same reason.** `LodMath.NominalSize(d)` is `S0 / 2^d`, never `SurfaceMath.NodeWorldSize`. Measured size varies across one cube face at a fixed depth — 1.1793:1 at depth 3, 1.2908 at depth 5, 1.3279 at depth 8 — so measured thresholds do not nest by exactly 2, and two same-depth nodes on one face end up at different points in the morph: at depth 8 the smallest is at `k = 1.000` where the largest is still at `k = 0.383`. That 0.617 disagreement along a shared edge is a gaping crack, and no amount of shader work recovers from it. Only the *threshold* is depth-only; the distance `LodSelector` compares against it still uses the node's real bounding sphere. `SonomaRevisedPlan.md` §4.4 originally said measured; it was corrected during M3a. Note the plan's phrasing "two same-depth neighbours at a shared edge" for the 0.383 figure: immediately adjacent nodes differ far too little to show it (worst measured disagreement 0.0000), and the number is the face's extremes. `LodMathTests.MeasuredNodeSizeWouldBreakTheBoundary` reproduces it.
- **`LodMath.NominalSize` divides by a shifted power of two, not by `math.pow`.** `MorphRangesNestExactly` asserts `end_{d-1} / end_d == 2.0` with a delta of zero, and it can only do that because `S0 / (double)(1L << d)` is exact in IEEE. This is the same species of decision as `HeightParams.FloorLog2` reading the exponent field rather than calling `log2`.
- **A node's bounding sphere is MOVED by the terrain, not inflated by it — and the difference is the whole LOD hierarchy.** `LodSelector.NodeDistance` originally padded the radius with `max(|MinHeight|, |MaxHeight|)`, the *absolute* elevation. That bounds the geometry correctly but grows without limit relative to the node, because elevation is fixed while the node halves each level: measured on `SampleScene` (a 300 m cube-sphere with `HeightScale` 50) the pad reached **10.5× the node size at depth 7 and 21× at depth 8**, so every node within ~40 m of the camera returned distance 0 and the tree split to `MaxDepth` everywhere regardless of where the camera was. Centring the sphere on the node's own mid-elevation and widening it by *half its relief* keeps it proportional: the same measurement gives **0.30 of the node size, flat across depth** and independent of radius and `HeightScale`.
- **Terrain roughness spends the same boundary budget as node size spread, but it is checked at runtime, not by `LodMath.Create`.** The half-relief pad enters the boundary condition exactly where `SizeSpread` does, so `LodMath.MaxHalfRelief(depth)` inverts `MaxMorphStartFraction` for it. It is deliberately *not* a `Create`-time throw: fbm relief at a given scale is a property of the noise, not the configuration. Measured, `halfRelief / nodeSize` is flat across depth and independent of radius and `HeightScale`, but it is **1.83 at persistence 0.4, 3.15 at 0.5, 7.74 at 0.6 and 21.2 at 0.7** (in units of `HeightScale/S0`), and an analytic worst case covering all of them runs ~4× the truth — tight enough to refuse worlds that are fine, `SampleScene` among them. So `LodSelector.OnChunkReady` warns once, naming the measured relief, the allowance and the levers. `SplitFactor` 4 clears that warning for `SampleScene` with 23% to spare; at 3 it fired from depth 3 down. `LodMathTests.MaxHalfReliefInvertsTheMorphStartFractionBound` pins the inversion, and `LodBoundaryTests` pads its simulated selection by exactly `MaxHalfRelief` — so if the inversion were wrong in the unsafe direction, boundaries would open at precisely the relief it permits and the boundary test would fail.
- **`MorphStartFraction` above 0.5 cracks every LOD boundary in the world.** The fine side reaches `k = 1` at `end_d`; the coarse side stays at `k = 0` until `start_{d-1} = 2·end_d(1 - frac)`, so `frac <= 0.5` follows directly. `LodMath.Create` throws rather than letting it ship, and 0.5 exactly is accepted — an off-by-one there would cost a usable configuration.
- **`LodMath.cs` imports no `UnityEngine`; the `TerrainSettings` overload of `Create` lives in `LodMathSettings.cs`.** `TerrainSettings` is a `ScriptableObject`, so a single-file `LodMath` would drag `UnityEngine` in and lose the out-of-Editor harness. Same split, and same reason, as `ChunkMeshLayout` / `ChunkMeshBuffers`.
- **The per-frame order is selector, then scheduler, and chunks arrive hidden.** `LodSelector.OnChunkReady` deactivates the chunk it is handed and leaves it to the next `Run` to show. Landing a chunk visible would put a freshly generated child on screen in the same frame as the parent it replaces, which is exactly the overlap the atomic swap exists to prevent. The cost is one frame of latency on a new chunk; the parent is still drawing, so there is no hole.
- **The selector's split decision is `Descend`'s return value, not a stored flag.** A node goes into `_frameSplit` only when all four children returned covered, recursively; the visibility pass then descends only through `_frameSplit`. `NodeState.HasChildren` is a separate thing — last frame's split *decision*, kept solely to feed hysteresis. Conflating the two gives a node that is marked split before its children exist, and the visibility pass walks into a hole.
- **`ChunkPool.Dispose` destroys the GameObjects as well as the meshes, and uses `DestroyImmediate` outside play mode.** `Object.Destroy` defers to the end of the frame, which never arrives in an EditMode test — it throws there instead. `LodSelectorTests` drives the real pool, so the teardown path has to work in both.
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
- **A chunk finishes morphing where its PARENT takes over, not at its own split distance.** `LodMath.MorphRange(d).end` is `SplitDistance(d−1)` — written as `2 × SplitDistance(d)` so depth 0 needs no special case. A node's own split distance is where it hands over to its *children*; a chunk fully morphed there draws its parent's geometry while its coarser neighbour, also fully morphed, draws its grandparent's. `SonomaRevisedPlan.md` §4.4 and `Docs/M3_Geomorphing_Plan.md` both say "the split distance of depth d", and M3b shipped that first: measured against a simulated selection it put **100% of cross-depth boundary samples exactly 1.0 effective LOD apart**, which is a seam along every boundary and a full level of pop at every swap. `LodBoundaryTests` reproduces both the fix and the break.
- **The one number that says whether the terrain cracks is `depth − k`.** A chunk at depth d with morph factor k draws the geometry of depth `d − k`, so two chunks meeting at a surface point must agree on that value. Same-depth neighbours agree for free, because k is a function of the vertex's own distance. Cross-depth neighbours agree only when the fine side has reached `k = 1` and the coarse side has not left `k = 0`. Everything in `LodMathTests` checks the ranges against each other; only `LodBoundaryTests` checks them against an actual selected tree, which is why the off-by-one survived the first pass.
- **`SplitFactor = 2` cannot be used with the geomorph, and that is geometry, not tuning.** Selection uses a per-node bounding-sphere distance; the morph uses a per-vertex distance; a coarse leaf's patch reaches past its own sphere distance by up to `SurfaceMath.MaxNodeSizeSpread` nominal node sizes. That gives `MorphStartFraction ≤ 1 − (Hysteresis + SizeSpread / SplitFactor) / 2`, and at `SplitFactor 2` with any hysteresis on a cube sphere the ceiling is under 0.01 — a morph window under 1% wide, which is a pop by another name. `LodMath.Create` throws rather than let it ship. Cost scales with `SplitFactor²`: worst case over a face at `MaxDepth` 8 and `PreloadFactor` 1.15 the working set is 1878 chunks at 3 and **2970 at the shipped 4**. What the extra buys is headroom for rough terrain — `SplitFactor` raises the `MorphStartFraction` ceiling *and* `LodMath.MaxHalfRelief` together, and 4 is what lets `SampleScene`'s 300 m sphere carry a 50 m `HeightScale` with no depth exceeding its relief allowance (at 3, six of the nine depths overran it by up to 2.8×).
- **The preload test is on the parent's distance, not the child's, and the difference is the whole feature.** `LodSelector.Preload` runs only on a node that did *not* split, so its distance is already at or beyond `SplitDistance(depth)`; a child's bounding sphere sits inside its parent's, so `NodeDistance(child) ≥ NodeDistance(node)`. M3a compared each child's distance against `PreloadDistance(depth + 1)`, which works out to `PreloadFactor / 2` of `SplitDistance(depth)` — 0.75 at the default 1.5, i.e. a threshold the parent had already exceeded. Measured out of Editor, that form preloaded **exactly zero nodes at every camera placement tried**, so every subdivision waited on four cold chunks while the atomic swap held the parent at coarse LOD. Nothing caught it because the selector is correct without preload — the tree converges, the swap stays atomic, coverage stays complete; the only symptom is latency. `LodSelectorTests.ChildrenAreRequestedBeforeTheParentSplits` is the assertion that was missing. Preload is also the most expensive knob here: worst case over a face the working set is 2382 chunks at `PreloadFactor` 1.0, 2970 at 1.15 and 4326 at 1.5, and 1.15 was chosen to stay under the 3200 budget while still buying a margin of 15% of the split distance before the deepest split.
- **`SurfaceMath.MaxNodeSizeSpread` is a hand-entered table, and each entry must be an upper bound.** π/2 for a tangent-adjusted cube sphere, √3 without the adjustment, exactly 1 for a plane grid, 1.02 for a cylinder (measured 1.01259). Too small an entry is a seam rather than a rounding error, because `LodMath` uses it to bound `MorphStartFraction`. `SurfaceGeometryTests.NodeSizeSpreadMatchesTheTable` re-derives all four numerically, as with the adjacency table and the handedness switch.
- **The morph runs in all four passes, and that is not optional.** `ForwardLit`, `ShadowCaster`, `DepthOnly` and `DepthNormals` each declare their own `Attributes`, so each needs `TEXCOORD2` (and `TEXCOORD3` where it uses a normal). Morphing only the forward pass leaves shadows and the depth prepass on the unmorphed geometry, which presents as shadow acne and depth-test dropouts along LOD boundaries — symptoms that look nothing like the cause. `GeomorphTests.ShaderMorphsInEveryPass` reads the shader source and checks, because the failure is silent.
- **The morph factor is computed from the UNMORPHED position.** `SonomaMorphFactor` takes `positionOS` before any lerp. That is what makes every pass agree on `k` for the same vertex, and what makes two chunks sharing a vertex agree: they compute the same world position for it, so the same distance, so the same `k`. Feeding it the morphed position would also be circular.
- **The morph measures distance from `_SonomaViewPosition`, never `_WorldSpaceCameraPos`, and that is not a style choice.** URP restores `_WorldSpaceCameraPos` for the main-light shadow pass explicitly — `MainLightShadowCasterPass` calls `ShadowUtils.SetCameraPosition`, commented "not set for passes executed before normal rendering" — and `AdditionalLightsShadowCasterPass` does not. So in a scene with a shadow-casting point or spot light and **no** shadowed directional light, the ShadowCaster pass reads whatever was last bound (a stale frame, or another camera) and casts from geometry at a morph state `ForwardLit` never drew: shadow acne along LOD boundaries, which is the same failure the "morph in every pass" rule exists to prevent, reached from the other side. `TerrainRoot.PushShaderGlobals` pushes the position once a frame, in render space, and `Update` hands *the same* `Vector3` to `LodSelector.Run` after adding the origin back — so selection and the morph cannot drift apart within a frame either. `GeomorphTests.ShaderMorphsInEveryPass` asserts it on `SonomaMorphFactor`'s body rather than the whole file, because the comment above the function names the builtin it is avoiding.
- **`LodMath.Create` bounds `MaxDepth` above as well as below, and both failures past it are silent.** Past `NodeId.MaxAddressableDepth` (30), `Span` evaluates `1 << 31 == int.MinValue` and the UV accessors return small negative numbers, so a node lands somewhere plausible but wrong — the same trap `NodeId.Parent` guards at the other end. Past 31 the shader's 32-entry `_SonomaMorphRanges` runs out; its `clamp` bounds the read but does **not** degrade gracefully, because slot 31's range is centimetres wide, so the chunk is pinned at `k = 1` and drawn permanently morphed onto its parent rather than unmorphed. The constant lives on `NodeId` because that is what sets it, and is enforced in `LodMath.Create` because that is where configuration is validated — not in the `NodeId` constructor, which runs inside Burst jobs and on every `Child()` call.
- **`_SonomaMorphRanges` is a global array, not a `MaterialPropertyBlock`.** A per-chunk property block would break SRP batching, which is the entire reason the chunk's depth travels in `TEXCOORD2.w` as a vertex attribute rather than as a material property. It is declared outside `CBUFFER_START(UnityPerMaterial)`, alongside `_SonomaWorldOrigin`, and `TerrainRoot.PushMorphRanges` re-pushes it every frame — shader globals are process-wide, so anything else setting that name would otherwise leave the terrain morphing against someone else's distances.
- **Elevation morphs too, on the same `k`.** `TEXCOORD3.w` carries the geomorph target's elevation and the shader blends `TEXCOORD1.w` towards it. Not decoration: the elevation drives the band blend, so leaving it on the fine value while the geometry moves to the coarse one shifts the colour bands at exactly the moment the parent takes over — a colour seam where there is no geometric one. It costs 4 bytes a vertex, taking `TerrainVertex` to 80. `SonomaMorph` returns the factor it used so the caller blends the elevation without recomputing it.
- **Skirts are turned off by setting `SkirtDepth` to zero, not by removing the geometry.** `TerrainSettings.SkirtsEnabled` feeds `skirtDepth = enabled ? SkirtDepth : 0f` into the mesh job, so the `4R` skirt vertices stay in the buffer and collapse flat against the edge. The vertex count, index buffer and layout are therefore identical either way, which is what lets the toggle be a runtime setting rather than a rebuild.
- **`TEXCOORD1` is spoken for.** `SonomaTerrainTriplanar.shader` reads it as `(topoUp.xyz, elevation)`. The geomorph attributes live at `TEXCOORD2` (morph position + depth) and `TEXCOORD3` (morph normal + morph elevation); `ChunkMeshJob` has written the first three since M2, before anything read them. `TEXCOORD3.w` was added afterwards, when the elevation turned out to need morphing too - the one place the M2 layout guess came up short.
- **Skirts are `4R` vertices, one row per edge, and `8(R-1)` triangles.** Two triangles per skirt quad, four edges — a `4(R-1)` triangle count is the easy mistake and sizes the index buffer at half what it needs. The four edges are walked anticlockwise as seen from outside (South +u, East +v, North −u, West −v) so one triangle order serves all four. The prototype's reversed edge was **North**, not West as an earlier note here claimed.
- **`ChunkMeshLayout` is UnityEngine-free on purpose** so its arithmetic runs in the out-of-Editor test harness; `ChunkMeshBuffers` holds everything that needs `UnityEngine.Rendering`. Keep the split — it is what catches buffer-sizing bugs as test failures rather than as memory corruption.
- **The scheduler checks "still wanted" twice**, before scheduling and again on completion, and disposes unwanted results instead of showing them. This is the fix for the prototype's orphan-chunk leak, where a collapsed node stayed queued and later produced a chunk nothing referenced.
- **`GenerationScheduler.Enqueue` re-prices a node already in the queue, and the heap is indexed so it can.** Priority is `distance / NominalSize` and the camera moves, so the number a node was first sighted with is stale by the next frame. Returning early for an already-queued node — which is how M3b first shipped — left every preloaded chunk carrying the priority it was requested at, `PreloadFactor × SplitFactor` node sizes out; by the time the camera arrived and the tree was waiting on that node to split, it generated *after* every entry whose own stale priority happened to be smaller, including nodes the camera had since flown away from. That is the four-chunks-of-latency-per-subdivision symptom preload exists to remove, and it hid the same way the M3a preload defect did: the tree still converges, the swap stays atomic, coverage stays complete. `LodSelector.Want` therefore calls `Enqueue` every frame while a node's `Chunk` is null and stops once it is resident — which is also what keeps the steady-state frame free of heap traffic and `SelectorDoesNotAllocatePerFrame` passing. `_heapAt` maps node → slot and **every swap must maintain it**; all four of its assignments are load-bearing (deleting any one is caught by fuzzing the heap against a reference model). Pushing a duplicate entry and discarding stale pops is the obvious alternative and the wrong one: it grows the heap by the whole wanted set every frame. `LodSelectorTests.RequeueingAQueuedNodeRepricesIt` enqueues sixteen nodes in exactly the reverse of the order they should generate in, re-prices them, and asserts the arrival order.
- **The resident-budget warning tests the size of the wanted set, not what eviction managed to do.** `_nodes.Count > MaxResidentChunks` — `ReleaseUnwanted` has already dropped everything else, so `_nodes` *is* the set this frame wants resident. The first form asked "over budget **and** nothing could be collapsed", which cannot fire in the case it was written for: a tree deep enough to overrun its budget always has a collapsible sibling group somewhere, so the collapse succeeds, the count comes back under budget, and both halves go false while the build/evict/rebuild churn runs on in silence at a full Burst job and mesh upload per chunk per frame. Only a budget below the six root quads ever reached it. `LodSelectorTests.EvictionTakesCompleteSiblingGroups` drives 600 frames of exactly that churn, which is why the gap survived review of the code that produced it.
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
