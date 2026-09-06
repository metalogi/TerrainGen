# Sonoma — Design Review and Revised Implementation Plan

Review date: 2026-09-05. Reviewed against `SonomaOverview_Expanded.md`, `Phase1Plan.md`, `CLAUDE.md`, and every script under `Assets/Scripts` (≈1,600 lines, including uncommitted work).

---

## 1. Verdict

The overall goal — a quadtree-LOD heightmap terrain that works on planes, planets and ringworlds, streamed from a Burst pipeline, with floating-origin rendering — is feasible and well-trodden. The parametric-surface idea (store lat/lon or angle/z ranges, evaluate positions analytically) is correct and worth keeping.

The current code is a working synchronous prototype, not the foundation the design document describes. Four things in it are structural and will not survive the move to threaded generation or planetary scale; they should be replaced rather than extended:

1. The UV-sphere base mesh (degenerate at the poles).
2. Generation-time seam stitching (order-dependent; produces cracks whenever a neighbour changes LOD after this chunk was built).
3. Single-precision noise sampling at world coordinates (detail octaves collapse at large distances from the origin).
4. Fully synchronous, unprioritised, uncancellable generation on the main thread.

Everything else is either fine or a normal refactor. The recommended plan below keeps about a third of the code and rewrites the rest around three decisions: **cube-sphere topology**, **geomorphing instead of stitching**, and **a pure, double-precision height function evaluated in Burst jobs**.

Status correction: `CLAUDE.md` marks Phases 1 and 2 complete. Phase 1 is complete as a prototype. Phase 2 is not: streaming is not threaded, seam handling is skirts in practice, and the memory budget counts only visible chunks.

---

## 2. Major design flaws

### 2.1 UV sphere is the wrong base mesh for a planet

`BaseMeshFactory.CreateUVSphere` tiles latitude/longitude. The rows touching each pole are triangles (two corners coincide, as the factory comment admits). Consequences compound with depth:

- Quadtree children of polar quads are slivers; at the pole an unbounded number of nodes converge on one point.
- Vertex spacing in the u direction shrinks by `cos(lat)`, so the LOD metric, noise frequency per level and normal quality all vary with latitude and break entirely near the poles.
- 8×4 = 32 root quads instead of 6 multiplies the amount of cross-quad boundary.

Every production planetary renderer uses a cube-sphere: six root faces, each point on the face projected to the sphere by normalisation, with a tangent adjustment (`tan(s·π/4)`) that reduces the worst-case spacing ratio from roughly 3:1 to under 1.5:1. Face adjacency is a fixed 24-entry table.

The design's "arbitrary quad base mesh" should be narrowed to surfaces whose vertices are all 4-valent: plane grid, cube-sphere, cylinder (and, for free, torus). Extraordinary vertices make neighbour lookup and seam handling far harder and buy nothing the project needs.

### 2.2 Generation-time stitching is order-dependent and leaks cracks

`ApplySeamStitching` locks a chunk's edge heights to a coarser neighbour **at the moment the chunk is generated**, then caches those values. Neighbour LOD is transient, so the locked edge goes stale:

- A (depth 3) is generated next to B (depth 2); A's edge is set to a linear interpolation of B's 17 shared samples. B later subdivides; B's child (depth 3) sees a same-depth neighbour and takes raw noise. A's edge and the child's edge now disagree at every odd vertex. Crack.
- The reverse also cracks: A generated next to B's children (same depth, raw noise), then B collapses and shows its coarse edge again.

The skirts hide both cases, which is why the scene looks correct. The design document calls skirts a fallback, but in the current code they are the mechanism. The stitch-then-cache ordering also assumes neighbours are generated serially, which is incompatible with worker-thread generation.

The robust fix is to never bake neighbour state into a chunk. Make heights a pure function of surface position, so shared vertices agree by construction, and handle LOD differences at render time with per-vertex geomorphing (§4.4). The existing code's comment in `SampleEdgeBorders` already relies on this property for normals; the plan extends it to everything.

### 2.3 Noise precision collapses at scale

`HeightmapGenerator.SampleAt` computes the world position in doubles, then casts `(float)(w.x * freq)` per octave. Float has 24 bits of mantissa, so at coordinate magnitude 2^k the resolution is 2^(k−23). For an Earth-radius sphere (6.4e6 units) with base frequency 0.003, octave 8 has input magnitude ≈ 4.9e6 and resolution ≈ 0.5 noise-cells: two samples per lattice cell, i.e. garbage. On a 100 km plane, octave 12 is already visibly stair-stepped. This directly contradicts the "continent-scale to third-person" capability.

Fix: a lattice noise that takes `double3`, computes the integer cell with a double `floor`, hashes the cell as `int3`, and evaluates the fractional part in float. Precision is then independent of coordinate magnitude. Burst handles double math natively.

### 2.4 Synchronous, unprioritised generation with no cancellation

`ProcessGenerationQueue` dequeues three nodes per frame and runs noise (≈8,000 simplex evaluations per chunk at resolution 33, six octaves), mesh building with managed arrays, and `mesh.vertices =` on the main thread. At the design's 129×129 resolution that is ≈100,000 evaluations per chunk. The 2 ms frame budget is unreachable on this path.

The queue is FIFO and never revisits its contents. Concretely:

- Moving the camera quickly enqueues nodes that will be collapsed before they generate. `CollapseNode` disposes children but leaves them in the queue; `SpawnChunk` later builds a chunk for a node whose parent no longer references it, registers it in the spatial index, and leaves the inactive GameObject in `TerrainChunk.AllChunks` forever. This is a leak and a source of stale stitching targets today.
- No priority: the closest chunk waits behind the furthest.
- `EnforceMemoryBudget` counts `AllActive` (visible) chunks only; hidden parents, which every subdivided branch retains, are unbudgeted.

### 2.5 Hierarchical parent→child generation conflicts with the rest of the design

The design's Levels 1–N each "upsample the parent heightmap 2× and add detail". This makes every chunk depend on its whole ancestor chain, forbids evicting ancestor data, serialises generation top-down, and, because the child's height at a parent-vertex position is parent + detail, guarantees that adjacent LODs disagree at shared vertices — which is what the stitching was trying to paper over.

What hierarchy actually buys is the ability to run non-local algorithms (erosion, river networks, tectonics) once, coarsely. That belongs in one persistent **macro layer** per root quad (§4.6). Below it, height should be `macro(bicubic) + detail octaves`, a pure function of position and depth. Chunks then generate in any order, in parallel, with no ancestor dependency, and determinism is trivial.

### 2.6 Smaller flaws worth fixing in the rewrite

- **LOD metric.** `WorldDistanceToNode` measures to the node centre at height 0 and compares against a hand-typed `LodDistances` array. A large node whose edge is at the camera does not split. Use distance to the node's bounding sphere (including its actual min/max height from generation) and derive the split distance as `k × nodeWorldSize`, which works unchanged for any topology or scale.
- **Origin rebase accumulates float error.** Each rebase does `transform.position -= shift` in float. After many rebases positions drift. Store a `double3` anchor per chunk and recompute `position = (float3)(anchor − origin)`.
- **Plane is one quad.** The rebasing section claims "truly infinite worlds", but the plane topology has a single root. Roots should be a lazily-created grid keyed by integer tile.
- **Cylinder has no rows.** An O'Neill cylinder (32 km × 8 km diameter, 8 columns) gives 3 km × 32 km root quads; every descendant inherits the 10:1 aspect.
- **Float bounds as dictionary keys.** `QuadtreeBounds` uses `Vector2` min/max and `Mathf.Floor(probe / size)` snapping with epsilons to find neighbours. Dyadic values are exact in float, so it works by luck; integer `(quad, depth, x, y)` addressing is simpler and makes cross-quad neighbours (with edge rotation) tractable.
- **Height bias.** fBm sums `amp × (snoise·0.5 + 0.5)`, so the mean height is ≈ 1 × HeightScale everywhere; the whole surface floats above the base sphere.
- **Duplication and dead code.** `DemoTerrainSpawner.BuildMeshFromHeightmap` duplicates `QuadtreeManager.BuildMesh`; `FlyCamera` (legacy Input) duplicates `TopologyFlyCamera` (new Input System). `HeightmapGenerator.Generate` allocates a managed `float[,]` per chunk. `EdgeNormal*` caches and normal stitching become unnecessary with geomorphing.
- **No assembly definitions, no tests.** Burst and Collections are only transitive dependencies in `manifest.json`. The Test Runner has nothing to run, although the pure height function is the most testable thing in the project.

---

## 3. What to keep, rewrite, delete

| Keep (with edits) | Rewrite | Delete |
|---|---|---|
| `BaseMeshQuad` / `CoordinateTransform` parametric approach — add `CubeSphere` surface type, drop UV sphere | `QuadtreeManager` → split into `LodSelector`, `GenerationScheduler`, `ChunkPool` | `DemoTerrainSpawner` |
| `TerrainSettings` — replace `LodDistances` with `SplitFactor`, add job/budget fields | `QuadtreeNode` / `QuadtreeBounds` → integer `NodeId` addressing | `FlyCamera` (legacy Input) |
| `SonomaTerrainTriplanar.shader` — add geomorph to vertex stage | `HeightmapGenerator` → `TerrainHeightFunction` (Burst, double lattice noise) | `HeightmapGenerator.Generate` (managed) |
| `TopologyFlyCamera` — read topology from the new surface abstraction | `WorldOriginSystem` → per-chunk double anchors | `EdgeN/S/E/W`, `EdgeNormal*`, all `*Stitch*` methods |
| `TerrainChunk` — pooled, anchor-aware | `BaseMeshFactory` → `Surface` classes with adjacency tables | `Phase1_Step1_Checklist.md` (superseded) |

Commit the current uncommitted work first (shader, `TopologyFlyCamera`, border-normal sampling) as the end of the prototype so it stays in history.

---

## 4. Target architecture

### 4.1 Surfaces and node addressing

```csharp
public enum SurfaceType { PlaneGrid, CubeSphere, Cylinder }

// One root quad. u,v ∈ [0,1] across the quad.
public struct RootQuad
{
    public int         Index;
    public SurfaceType Type;
    public int         Face;          // cube face 0..5, or grid/cylinder column index
    public double4     Param;         // face: (a0,a1,b0,b1); cylinder: (angle0,angle1,z0,z1); plane: (x0,x1,z0,z1)
    public double      Radius;        // sphere / cylinder
}

// Every quadtree node, addressable without walking the tree.
public readonly struct NodeId : IEquatable<NodeId>
{
    public readonly int Quad, Depth, X, Y;   // X,Y ∈ [0, 2^Depth)
    public NodeId Parent => new(Quad, Depth - 1, X >> 1, Y >> 1);
    public NodeId Child(int i) => new(Quad, Depth + 1, X * 2 + (i & 1), Y * 2 + (i >> 1));
}

// Cross-quad adjacency: for each (quad, edge) → (neighbourQuad, neighbourEdge, reversed).
public struct EdgeLink { public int Quad; public int Edge; public bool Reversed; }
```

`Surface` exposes three pure functions, all Burst-compatible and taking doubles:

- `SurfacePoint(quad, u, v) → double3` and `SurfaceNormal(quad, u, v) → float3` (height is added along the normal, exactly as now).
- `Neighbour(NodeId, edge) → NodeId?` — same-quad neighbours by integer arithmetic; cross-quad by the `EdgeLink` table with coordinate reversal.
- `NodeWorldSize(NodeId) → double` — chord length of the node at the surface, used by the LOD metric.

Cube-sphere face mapping with tangent adjustment:

```
a' = tan(a·π/4),  b' = tan(b·π/4)          // a,b ∈ [-1,1] on the face
dir = normalize(faceCentre + a'·faceRight + b'·faceUp)
pos = Radius · dir,  normal = dir
```

Plane grid roots are created lazily for tiles within generation range of the camera (double-precision tile origin), which gives the infinite plane the design promises. Cylinder takes `cols × rows`, rows chosen so root quads are roughly square.

### 4.2 Height function (the contract everything depends on)

```csharp
[BurstCompile]
public static class TerrainHeightFunction
{
    // Height in world units above the base surface at a surface point.
    // maxOctave limits detail to what the calling LOD can represent (band-limiting),
    // so a depth-d chunk and its parent evaluate *different* functions at the same point,
    // and geomorphing (4.4) bridges them.
    public static float Height(in double3 surfacePos, in float3 surfaceNormal,
                               int maxOctave, in MacroSample macro, in NoiseParams p);
}
```

Rules:

- **Pure and deterministic.** Same `(surfacePos, maxOctave, seed)` → same bits, on any thread, in any order. This is what makes shared edge vertices agree without stitching, and what makes the whole pipeline trivially parallel.
- **Double-precision lattice noise.** `LatticeNoise.Gradient3D(double3 p, uint seed)`: `int3 cell = (int3)floor(p)` in double, `float3 f = (float3)(p − cell)`, gradient hash from `(cell, seed)`. Simplex is not required; 3D Perlin-style gradient noise with quintic fade is fine and easy to make exact.
- **Band-limited per depth.** Octave k has wavelength `λ0 / 2^k`. A chunk at depth d with vertex spacing `s_d` includes octaves with `λ_k ≥ 2·s_d`. `maxOctave(d)` is a settings-derived lookup.
- **Zero-mean.** Use `snoise`-style signed noise so the base surface is the mean.
- **Macro layer in, not up.** `MacroSample` is a bicubic read of the persistent Level-0 maps (§4.6); at first it is a constant zero and the function is plain fBm.

### 4.3 Generation pipeline (Burst)

Per chunk, one pending record holds `NodeId`, `JobHandle`, and the native buffers. Stages:

1. **`HeightSampleJob : IJobParallelFor`** over an `(R+2)×(R+2)` grid (one-vertex border for normals). Computes `SurfacePoint` in double, height at `maxOctave(d)`, and also height at `maxOctave(d−1)` on the coarse `((R−1)/2+3)²` sub-grid for morph targets. Writes `NativeArray<float>` heights and `NativeArray<double3>` positions (or recomputes positions in stage 2; measure).
2. **`ChunkMeshJob : IJob`** builds vertex streams in **chunk-local float space** around a `double3 Anchor` (the node's surface centre): position, morph-target position, normal, morph normal, uv, packed depth. Adds skirts. Writes straight into a `Mesh.MeshDataArray` obtained on the main thread via `Mesh.AllocateWritableMeshData`. Index buffer is a shared, per-resolution constant (16-bit for R ≤ 181).
3. **Main thread** polls `JobHandle.IsCompleted` (never `Complete()` early), then `Mesh.ApplyAndDisposeWritableMeshData`, sets bounds from the job's min/max, pulls a pooled `TerrainChunk`, and sets `transform.position = (float3)(Anchor − WorldOrigin)`.
4. **Collider** (only at the finest 1–2 depths): `Physics.BakeMesh` scheduled as a job on the same mesh.

Scheduler policy:

- Priority queue keyed by `(distance / nodeWorldSize)` so near-and-coarse beats far-and-fine.
- Cap in-flight jobs (default 8). Before scheduling, re-check the node is still `Wanted`; on completion, if the node was collapsed meanwhile, dispose the buffers and drop it. This removes the orphan-chunk leak.
- Upload budget is **time-based** (`Stopwatch`, default 1.5 ms), not count-based.
- `TerrainSettings` gains: `SplitFactor`, `MorphStartFraction`, `MaxInFlightJobs`, `UploadBudgetMs`, `PreloadFactor`, `MaxResidentChunks`, `ColliderMaxDepth`, `OctaveWavelength0`, `OctaveCount`, `Seed`.

### 4.4 LOD selection and geomorphing

Selection (per frame, cheap, allocation-free):

- Distance `D` from camera to the node's bounding sphere (surface centre, radius from chord size and stored min/max height).
- Split when `D < SplitFactor × NodeWorldSize`; collapse with hysteresis on the **decision** only.
- Show parent until all four children are resident (keep the existing atomic swap); begin generating children at `PreloadFactor × split distance` so they are usually ready before the camera crosses the split distance.
- Budget: `MaxResidentChunks` counts every resident chunk, hidden parents included; evict furthest complete sibling groups first, as now.

Geomorphing (per vertex, in the vertex shader):

- Each fine vertex `(x, y)` stores its **morph target**: the chunk-local position of the even vertex `(x & ~1, y & ~1)` evaluated with the parent's band limit `maxOctave(d−1)`. Even vertices morph in height only; odd vertices collapse onto an even neighbour. With the existing triangulation `(i00,i11,i10),(i00,i01,i11)` this makes a fully-morphed child mesh **identical, triangle for triangle, to the parent mesh** (the surviving triangles are the parent's, the rest are zero-area).
- Morph factor `k = saturate((dist − start_d) / (end_d − start_d))` per vertex, where `end_d` is the split distance of depth d and `start_d = end_d × (1 − MorphStartFraction)`. Ranges nest (`end_{d−1} = 2·end_d`), so at any boundary between depths d and d−1 the fine side is at `k = 1` and the coarse side at `k = 0`: no crack, no T-junction, no stitching.
- Both `position` and `normal` are lerped. The chunk's depth is packed into a vertex attribute and ranges come from a global `float4[]` so the SRP batcher stays enabled (no per-chunk `MaterialPropertyBlock`).
- Skirts remain as the fallback for the transient state where the tree is temporarily more than one depth apart across an edge (a parent still waiting for children). Optionally enforce 2:1 balance in selection later; not required.

This replaces both "T-junction stitching" (Phase 2) and "LOD transition morphing" (Phase 4) in the original roadmap with one mechanism, and it is the only approach that stays correct when chunks are generated asynchronously and out of order.

### 4.5 Floating origin

- Every chunk keeps `double3 Anchor`. Mesh vertices are chunk-local, so their float magnitude never exceeds the chunk size regardless of world position.
- `WorldOriginSystem` on rebase sets `position = (float3)(Anchor − WorldOrigin)` for every resident chunk from the double anchor (no accumulation), moves the camera and any tracked rigs, and pushes the origin to the shader as now.
- Triplanar sampling continues to use `positionWS + _SonomaWorldOrigin`; at planetary scale this float sum is imprecise, so switch the shader to sample texture coordinates from the chunk-local position plus a per-chunk low-precision offset, or accept slight texture swimming far from the origin. Decide when textures are real.

### 4.6 Macro layer and biomes (the real "Level 0")

- Per root quad, a small set of persistent maps (e.g. 129×129 or 257×257): base height, biome id, moisture, temperature, later erosion output and river masks. Generated once per seed by a Burst job at startup (or baked to an asset), stored in a shared read-only `NativeArray`, sampled bicubically by the height function.
- Biome modulation is a per-vertex function of the macro sample: amplitude and roughness multipliers per octave band, plus flatten/sharpen curves. Splat weights are computed in the same job and written as a vertex colour for the shader.
- This is where non-local algorithms (hydraulic erosion, river networks, tectonic plates) go later. They never run at chunk time.

### 4.7 Project structure

```
Assets/Scripts/
├── Sonoma.Core.asmdef              (references Unity.Burst, Unity.Collections, Unity.Mathematics)
├── Core/Surface/                   RootQuad, Surface (PlaneGrid, CubeSphere, Cylinder), NodeId, EdgeLink
├── Core/Generation/                TerrainHeightFunction, LatticeNoise, HeightSampleJob, ChunkMeshJob, MacroLayer
├── Core/Quadtree/                  QuadtreeNode, LodSelector, GenerationScheduler
├── Core/Rendering/                 TerrainChunk, ChunkPool, MeshUploader
├── Core/CoordinateSpace/           WorldOriginSystem
├── Systems/Configuration/          TerrainSettings, BiomeDefinition
├── Tools/                          TopologyFlyCamera, TerrainDebugOverlay
├── Editor/  (Sonoma.Editor.asmdef) settings creator, gizmos, noise preview
└── Tests/   (Sonoma.Tests.asmdef, EditMode)
```

---

## 5. Revised milestones

Each milestone ends with something runnable and a test that would catch regressions. Milestone numbering restarts to avoid confusion with the original phases.

### M0 — Housekeeping (small)
- Commit the current uncommitted prototype work.
- Add `com.unity.burst` and `com.unity.collections` explicitly to `manifest.json`; add the three asmdefs; enable Burst in Player settings.
- Delete `FlyCamera`, `DemoTerrainSpawner`, `Docs/Phase1_Step1_Checklist.md`.
- Update `CLAUDE.md` status and point it at this document.

### M1 — Surfaces and addressing (medium)
- `RootQuad`, `NodeId`, `EdgeLink`, `Surface` with `PlaneGrid`, `CubeSphere` (tangent-adjusted), `Cylinder(cols, rows)`.
- Cube-face adjacency table with edge reversal.
- Gizmo drawing of root quads and nodes.
- **Tests:** for every cube edge, 16 sample points along the shared edge from both faces coincide to 1e-9; `Neighbour(Neighbour(n, e), opposite(e)) == n` across all faces and edges at depths 0–4; plane-grid and cylinder wrap behave the same way.

### M2 — Height function and Burst generation (large)
- `LatticeNoise.Gradient3D(double3)`, `TerrainHeightFunction` with band-limiting and zero mean.
- `HeightSampleJob`, `ChunkMeshJob` (positions, normals, morph targets, morph normals, skirts, chunk-local floats), `MeshDataArray` upload, `ChunkPool`.
- `GenerationScheduler` with priority queue, in-flight cap, cancellation, time-based upload budget.
- Root chunks only (no subdivision yet) to validate the pipeline in isolation.
- **Tests:** determinism (two evaluations, two threads, identical bits); precision (at `|p| = 1e7`, `Height(p) − Height(p + 1e-3)` is bounded by the analytic slope, no plateaus); edge agreement (two adjacent chunks at the same depth produce bit-identical heights at shared vertices, including across a cube edge); no managed allocations inside jobs (Burst compiles with `[BurstCompile(CompileSynchronously = true)]` and no fallback warnings).

### M3 — LOD selection and geomorphing (medium)
- `LodSelector` with bounding-sphere distance, `SplitFactor`, preload margin, parent-until-children-ready swap, resident-chunk budget.
- Vertex-shader morph in `SonomaTerrainTriplanar.shader`, depth attribute, global range array.
- Remove every remnant of edge caching and stitching.
- **Tests:** for a random node, build the child at morph 1 and the parent, and assert the surviving child triangles equal the parent's triangles vertex-for-vertex; play-mode fly-through on cube-sphere with skirts disabled shows no cracks in the steady state.

### M4 — Floating origin at scale (small)
- Per-chunk double anchors; rebase recomputes from anchors; camera and rig handling.
- Stress scene: Earth-radius cube-sphere, fly from orbit to ground with `TopologyFlyCamera`; verify no jitter and no texture swimming worse than accepted.
- **Test:** after 1,000 simulated rebases in random directions, every chunk's rendered position equals `(float3)(Anchor − Origin)` exactly.

### M5 — Macro layer and biomes (large)
- Per-root-quad macro maps in a Burst job, shared read-only `NativeArray`, bicubic sampling inside the height function.
- `BiomeDefinition` ScriptableObjects; per-octave amplitude/roughness modulation; splat weights in vertex colour; shader reads them.
- Optional in this milestone: a simple thermal/hydraulic erosion pass on the macro maps as proof that non-local algorithms fit here.

### M6 — Collision, tooling, polish (medium)
- Colliders at the finest depths via `Physics.BakeMesh` jobs; NavMesh surface hook.
- Debug overlay: resident/in-flight counts, upload time, per-depth node counts, LOD freeze, jump-to-coordinate, force-regenerate.
- Heightmap cache for revisited nodes; profiling pass against the 2 ms budget.

### M7 — Content (original Phase 5, unchanged in scope)
- Vegetation and rock scattering from the same deterministic per-node RNG, driven by biome density curves and slope.
- Shader parameter generation, biome library.

---

## 6. Risks and how the plan contains them

| Risk | Mitigation |
|---|---|
| Geomorph invariant broken by a future change to triangulation or band-limiting | M3 test asserts child-at-morph-1 == parent per triangle; keep it in CI for every change to `ChunkMeshJob` or `TerrainHeightFunction` |
| Burst double-precision noise slower than expected | Only the lattice `floor` and the surface point need doubles; the gradient evaluation is float. Measure in M2 before optimising |
| Cube-sphere cross-face neighbour bugs | M1 adjacency round-trip tests at several depths, before any generation code depends on it |
| Transient 2-depth differences across an edge show cracks | Skirts remain; preload margin makes the state rare; 2:1 balancing is an optional M6 refinement |
| Texture swimming at planetary distance from the origin | Decide in M4 with real textures; fall back to chunk-local texture coordinates |
| Scope creep in the macro layer (erosion, rivers) | M5 ships with plain macro noise; erosion is explicitly optional |

---

## 7. Things the original design gets right and the plan preserves

- Parametric surface evaluation rather than a per-quad transformation matrix (the overview still describes the matrix; update the text).
- Sampling Level-0 noise in surface space so root-quad boundaries agree. The plan generalises this to every level.
- Atomic parent/children swap to avoid overlap frames.
- Evicting complete sibling groups rather than single leaves.
- Data-driven `TerrainSettings`, skirts as a fallback, deterministic seed, heightmap-only terrain, separate water and collision.
