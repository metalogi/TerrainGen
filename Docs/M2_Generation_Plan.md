# M2 — Height Function and Burst Generation: Exact Implementation Steps

**Audience:** a coding agent working in `G:\UnityProjects\TerrainGen` with no prior context on this project.
**Parent plan:** `SonomaRevisedPlan.md` (repo root), milestone M2.
**Prerequisite:** M1 is complete and merged. `git log --oneline -1` should show `ee2cd4d` or later, and `git status --short` should be empty. The 21 EditMode tests must pass before you start.

M1 built a tested surface layer that nothing calls. M2 makes it load-bearing: it replaces the prototype's
generation path with a pure, double-precision height function evaluated in Burst jobs, and it retires
`Core/CoordinateSpace/` in one deliberate step. It is the largest milestone in the plan.

Every number in this document was derived and numerically verified before it was written, against the
shipped M1 code (the derivation reproduces `SurfaceGeometryTests.TangentAdjustmentImprovesSpacing`'s
1.6210 / 2.7330 and `CubeAdjacencyTests.SharedEdgePointsCoincide`'s 2.067e-16 exactly). Do not
re-derive them by hand; port them, then let the tests confirm the port.

---

## Ground rules

1. **Split the work into two landable branches.** M2 is large enough that bundling pure maths with a
   large deletion makes review and bisection painful.
   - **M2a — `m2a-height-function`.** `LatticeNoise`, `TerrainHeightFunction`, `HeightParams`, and their
     tests. Purely additive, exactly like M1: it touches no scene, no rendering, and no existing script.
     `SampleScene` must still play unchanged when M2a lands.
   - **M2b — `m2b-burst-pipeline`.** The jobs, `ChunkPool`, `GenerationScheduler`, the `TerrainChunk`
     anchor, and the switchover that deletes the prototype path.

   Land M2a first and confirm its tests green before starting M2b.
2. **The height function is a contract, not an implementation detail.** Everything downstream — seam
   correctness, geomorphing, determinism, the macro layer — depends on it being *pure*. No statics that
   mutate, no time, no frame counter, no random state, no dependence on evaluation order or thread.
3. **Doubles for position, floats for value.** Surface positions and noise-space coordinates are
   `double`. Fractional lattice coordinates, gradients, heights and normals are `float`. This is the
   whole point of §2.3 and it is easy to lose by accident — see Task 1.
4. **No interfaces, no virtual dispatch, no managed allocation inside jobs.** Same rule as M1. Dispatch
   on the `SurfaceType` enum with a `switch`. All job types are `[BurstCompile]` structs over
   `NativeArray<T>`.
5. **No `Mathf`, no `System.Math`.** Use `Unity.Mathematics`. `math.floor`, `math.tan`, `math.length`
   all have double overloads.
6. The prototype's `SampleScene` is the acceptance surface. It must play at the end of M2b, showing six
   root chunks (see Expected regressions).

---

## Conventions (fix these first; everything depends on them)

### C1 — Noise space

Noise is evaluated in **noise cells**, not world metres. One cell is one lattice cube.

```
noiseCoord = surfacePos / OctaveWavelength0 * 2^k        // for octave k
```

`OctaveWavelength0` is the world-space wavelength of octave 0, in metres. A setting of `0` means
"auto": use the nominal root-quad size `S0` (below), which puts the largest feature at one root quad
across. For the reference Earth-scale cube-sphere that is **λ₀ = 10,403,799.43 m**.

### C2 — Nominal node size `S0` and `S_d`

`SurfaceMath.NodeWorldSize` returns the **actual** size of a node, and on a cube-sphere that varies
across a face. Measured, tangent adjustment on, `R = 6.371e6`:

| depth | nodes/face | min size (m) | max size (m) | max/min |
|---|---|---|---|---|
| 4 | 256 | 789,957.70 | 988,127.66 | 1.2509 |
| 6 | 4,096 | 193,062.84 | 253,259.28 | 1.3118 |
| 8 | 65,536 | 47,975.39 | 63,706.35 | 1.3279 |
| 10 | 1,048,576 | 11,975.54 | 15,951.07 | 1.3320 |

**Therefore the band limit must never be derived from `NodeWorldSize`.** Two same-depth neighbours can
straddle a `floor(log2(...))` boundary, pick different octave counts, evaluate different functions and
crack along their shared edge — the exact failure mode M2 exists to eliminate. Derive it from **depth
alone**:

```
S0  = NodeWorldSize of a depth-0 node        // one number per SurfaceDef, computed once at startup
S_d = S0 / 2^d                               // nominal, exact, identical for every node at depth d
s_d = S_d / (ChunkResolution - 1)            // nominal vertex spacing
```

`S0` is the same for all six cube faces and for every tile of a plane grid, so one value per
`SurfaceDef` is correct. Compute it in `HeightParams.FromSettings` and pass it into the jobs by value.

### C3 — Band limiting

Octave `k` has wavelength `λ_k = λ₀ / 2^k`. Include an octave only when it is representable at the
chunk's vertex spacing (Nyquist, `λ_k ≥ 2·s_d`):

```
maxOctave(d) = clamp( floor( log2( λ₀ · (R−1) · 2^d / (2 · S0) ) ), 0, OctaveCount − 1 )
             = clamp( d + K0, 0, OctaveCount − 1 )
K0           = floor( log2( λ₀ · (R−1) / (2 · S0) ) )
```

The `d + K0` form is not an approximation — the `2^d` factors out exactly. **Implement the `d + K0`
form**, computing `K0` once, so the per-chunk path contains no `log2` and cannot drift.

Reference config (`λ₀ = S0`, `R = 33`): **K0 = 4**. Verified table:

| depth d | S_d (m) | s_d (m) | maxOctave | finest λ (m) |
|---|---|---|---|---|
| 0 | 10,403,799.43 | 325,118.73 | 4 | 650,237.47 |
| 1 | 5,201,899.72 | 162,559.37 | 5 | 325,118.73 |
| 4 | 650,237.46 | 20,319.92 | 8 | 40,639.84 |
| 8 | 40,639.84 | 1,269.99 | 12 | 2,539.99 |
| 12 | 2,539.99 | 79.37 | 16 | 158.75 |
| 15 | 317.50 | 9.92 | 19 | 19.84 |
| 16 | 158.75 | 4.96 | 19 (clamped) | 19.84 |

Sizing note for later milestones: reaching **1 m vertex spacing** on an Earth-sized cube-sphere needs
depth 19 at `R = 33` (18 at `R = 65`, 17 at `R = 129`), and reaching **2 m features** needs
`OctaveCount = 24`. `TerrainSettings.MaxDepth` is currently `8`, which is nowhere near that; leave it
alone in M2 (LOD is M3) but do not mistake it for a considered value.

### C4 — Vertex parameter formula (bit-exactness rule)

Every vertex `(i, j)` of a chunk at node `n` uses **exactly** this expression, and nothing algebraically
equivalent:

```csharp
double u = math.lerp(n.UMin, n.UMax, i / (double)(R - 1));
double v = math.lerp(n.VMin, n.VMax, j / (double)(R - 1));
```

Two same-quad, same-depth neighbours share an edge parameter that is bit-identical
(`UMax(x) == UMin(x+1)`, verified: both evaluate `(double)(x+1) / (1 << d)`), so this formula makes
their shared vertices bit-identical and their heights bit-identical. Rewriting it as
`UMin + (UMax − UMin) * t` changes the last bits and breaks that guarantee silently. Use one helper and
call it from both jobs.

### C5 — Anchor and chunk-local space

Each chunk stores `double3 Anchor = SurfacePoint(s, q, (UMin+UMax)/2, (VMin+VMax)/2)` — its node's
surface centre at height 0. Mesh vertices are `(float3)(worldPos − Anchor)`, so their magnitude never
exceeds the node size regardless of world position. `transform.position = (float3)(Anchor − WorldOrigin)`.

### C6 — Vertex layout (fixed now, so M3 does not have to change it)

| attribute | format | bytes | used by |
|---|---|---|---|
| `POSITION` | `float3` | 12 | M2 |
| `NORMAL` | `float3` | 12 | M2 |
| `TEXCOORD0` | `float2` | 8 | M2 |
| `TEXCOORD1` | `float3` | 12 | M3 — morph target position |
| `TEXCOORD2` | `float3` | 12 | M3 — morph target normal |
| `TEXCOORD3` | `float` | 4 | M3 — node depth |
| | | **60** | |

M2 **computes and writes all six**, and the shader ignores `TEXCOORD1..3` until M3. Writing them now
means M3 is a shader change plus an LOD selector, not another pass over the mesh job. The coarse
sub-grid at `maxOctave(d−1)` is well-defined even at depth 0 (`K0 − 1`), so root chunks produce valid
morph targets that simply never get used.

### C7 — Buffer sizes (verified)

Grid of `R×R` vertices plus one skirt ring of `4(R−1)` duplicated border vertices:

| R | sampled `(R+2)²` | coarse `((R−1)/2+3)²` | verts | tris | indices | mesh bytes |
|---|---|---|---|---|---|---|
| 33 | 1,225 | 361 | 1,217 | 2,176 | 6,528 | 71.3 KB |
| 65 | 4,489 | 1,225 | 4,481 | 8,448 | 25,344 | 262.6 KB |
| 129 | 17,161 | 4,489 | 17,153 | 33,280 | 99,840 | 1005.5 KB |

`R` must be **odd** so `(R−1)/2` is exact; assert it in `HeightParams.FromSettings`.

Per-chunk native scratch at `R = 33`: heights `(R+2)²` float = 4.8 KB, coarse = 1.4 KB, positions
`(R+2)²` double3 = 28.7 KB. At `MaxInFlightJobs = 8` that is **~279 KB** of scratch — small enough that
recomputing positions in the mesh job to save it is not worth the complexity. Allocate positions.

**16-bit indices are valid up to R = 253**, not 181. The binding constraint is the index *format*
(distinct vertices ≤ 65,536; at `R = 253`, 65,017 verts), not the triangle count. `SonomaRevisedPlan.md`
§4.3 says "16-bit for R ≤ 181", which is where the *triangle* count reaches 65,520 — not a limit on
anything. Correct that line (Task 8).

---

## Task 1 — `LatticeNoise` (M2a)

Create `Assets/Scripts/Core/Generation/LatticeNoise.cs`, namespace `Sonoma.Core.Generation`.

### Hash

```csharp
// Chris Wellons' lowbias32 finaliser over a 3-way multiplicative mix.
// Pure integer arithmetic: identical on every platform and in every Burst target.
static uint Hash(int3 c, uint seed)
{
    uint h = (uint)c.x * 0x8DA6B343u
           ^ (uint)c.y * 0xD8163841u
           ^ (uint)c.z * 0xCB1AB31Fu
           ^ seed      * 0x9E3779B9u;
    h ^= h >> 16; h *= 0x7FEB352Du;
    h ^= h >> 15; h *= 0x846CA68Bu;
    h ^= h >> 16;
    return h;
}
```

The `(uint)` casts on negative coordinates wrap, which is defined and deterministic in C#. Do not
"fix" them with `math.abs` — that would fold the lattice about the origin and mirror the terrain.

### Gradients

Ken Perlin's improved-noise 16-entry table (12 cube-edge directions, 4 repeated so the index is a mask
rather than a modulo):

```csharp
( 1, 1, 0) (-1, 1, 0) ( 1,-1, 0) (-1,-1, 0)
( 1, 0, 1) (-1, 0, 1) ( 1, 0,-1) (-1, 0,-1)
( 0, 1, 1) ( 0,-1, 1) ( 0, 1,-1) ( 0,-1,-1)
( 1, 1, 0) ( 0,-1, 1) (-1, 1, 0) ( 0,-1,-1)
```

In a Burst job this must not be a managed `static readonly float3[]`. Express it as a `switch` on
`h & 15` returning a `float3` — the same shape `SurfaceMath.FaceCentre` already uses for the cube face
basis. A managed array is the most likely cause of a Burst fallback; there is a test for it.

### Evaluation

```csharp
public static float Gradient3D(double3 p, uint seed)
{
    double3 fl = math.floor(p);
    int3    c  = (int3)fl;
    float3  f  = (float3)(p - fl);          // <- the only place doubles become floats
    float3  t  = f * f * f * (f * (f * 6f - 15f) + 10f);   // quintic fade
    // trilinear blend of dot(grad(corner), f - corner) over the 8 corners
}
```

`p − fl` is computed in **double** and only then cast. Computing `(float)p − (float)fl` instead is the
§2.3 bug reintroduced; there is a mutation-checked test for it.

### Measured properties (assert these, do not assume the textbook values)

- **Range is exactly [−1, 1], and the bound is attained.** Measured max |n| = 0.9266 over 200k uniform
  samples, 0.9959 over a further 400k, and hill-climbing converges to 1.000000. The frequently quoted
  bound of √3/2 ≈ 0.866 does **not** hold for this gradient table. **Do not normalise the output** —
  amplitude is `HeightScale`'s job, and multiplying by 2/√3 would clip.
- **Mean is zero.** Measured −1.6e-5 over 200k samples. This is what fixes the height bias of
  `SonomaRevisedPlan.md` §2.6 (the prototype's `snoise·0.5 + 0.5` floats the whole surface above the
  base sphere).
- **Value at every integer lattice point is exactly 0.** 200/200 exact in the reference implementation.

### fBm

```csharp
public static float Fbm(double3 p, int octaveCount, float persistence, float lacunarity, uint seed)
```

Octave `k` samples at `p * pow(lacunarity, k)` with amplitude `pow(persistence, k)`, seeded
`seed + (uint)k` so octaves are independent. `octaveCount` is `maxOctave + 1`. Sum, do not average;
the amplitude sum is a settings-level concern.

---

## Task 2 — `TerrainHeightFunction` and `HeightParams` (M2a)

Create `Assets/Scripts/Core/Generation/HeightParams.cs`:

```csharp
public struct HeightParams          // blittable, copied into jobs by value
{
    public double S0;               // nominal depth-0 node size, from SurfaceMath.NodeWorldSize
    public double OctaveWavelength0;// world metres; 0 in settings means "= S0"
    public int    Resolution;       // odd
    public int    OctaveCount;
    public int    K0;               // C3
    public float  HeightScale, Persistence, Lacunarity;
    public uint   Seed;

    public int MaxOctave(int depth) => math.clamp(depth + K0, 0, OctaveCount - 1);

    public static HeightParams FromSettings(in SurfaceDef s, TerrainSettings t);  // computes S0, K0; asserts odd R
}
```

Create `Assets/Scripts/Core/Generation/TerrainHeightFunction.cs`:

```csharp
[BurstCompile]
public static class TerrainHeightFunction
{
    // Height in world metres above the base surface at a surface point.
    // macro is MacroSample.Zero until M5.
    public static float Height(in double3 surfacePos, int maxOctave,
                               in MacroSample macro, in HeightParams p)
    {
        double3 n = surfacePos / p.OctaveWavelength0;
        return p.HeightScale * LatticeNoise.Fbm(n, maxOctave + 1, p.Persistence, p.Lacunarity, p.Seed)
             + macro.BaseHeight;
    }
}
```

Add `MacroSample` in the same folder: a struct with a `BaseHeight` field and a `static MacroSample Zero`.
It is a placeholder for M5 and must appear in the signature now so M5 is not a signature change
rippling through the jobs.

**Do not add a `Height(u, v, RootQuad)` convenience overload.** The function takes a surface *position*
precisely so that two chunks reaching the same position by different parameterisations get the same
answer.

---

## Task 3 — Generation jobs (M2b)

Create `Assets/Scripts/Core/Generation/HeightSampleJob.cs` and `ChunkMeshJob.cs`.

### `HeightSampleJob : IJobParallelFor`

Runs over `(R+2)²` indices — the chunk grid plus a one-vertex border on all four sides, so the mesh job
can compute border normals by central difference without querying neighbours. Per index:

1. Map to `(i, j) ∈ [−1, R]`, then to `(u, v)` by **C4** (the border extends the same lerp beyond
   `[0,1]`, which is correct: it walks into the neighbouring node's parameter range, and on a cube face
   that stays on the same face's plane before projection, so the sampled point is still the true
   surface point).
2. `SurfaceMath.SurfaceFrame(s, q, u, v, out point, out normal)`.
3. `heights[idx] = TerrainHeightFunction.Height(point, maxOctave, MacroSample.Zero, p)`.
4. `positions[idx] = point`.

A second, smaller pass fills the coarse `((R−1)/2+3)²` grid at `maxOctave(d−1)` for the morph targets.

### `ChunkMeshJob : IJob`

Inputs: `heights`, `positions`, the coarse arrays, `Anchor`, `SkirtDepth`, `depth`.
Output: a `Mesh.MeshData` obtained on the main thread from `Mesh.AllocateWritableMeshData(1)` and
passed in, plus a `NativeArray<float>` of length 2 for min/max height (for bounds).

- Vertex `(i, j)` position is `(float3)(positions[..] + normal * height − Anchor)`.
- Normal by central difference over the border-extended height grid, in chunk-local space.
- UV is `(i, j) / (R − 1)`.
- Morph target: the fine vertex `(i, j)` targets the **even** vertex `(i & ~1, j & ~1)` evaluated from
  the coarse grid. Even vertices differ from their target in height only; odd vertices collapse onto an
  even neighbour. This is the invariant M3's "child at morph 1 == parent" test asserts.
- Skirts: duplicate the border ring, displace by `−normal * SkirtDepth`, and emit two triangles per
  border segment. **Wind all four sides consistently.** `CLAUDE.md` records that the prototype's West
  skirt appears reverse-wound; do not port that. There is a test.
- Triangulation is `(i00, i11, i10), (i00, i01, i11)` — unchanged from the prototype, and load-bearing
  for M3's morph invariant. Do not "improve" it to a diagonal-flipping scheme.

### Index buffer

Per-resolution `NativeArray<ushort>` built once, cached in a static dictionary keyed by `R`, and
`memcpy`'d into each `MeshData`. A GPU index buffer cannot be shared between meshes, but the source
array can. Dispose the cache on domain reload.

---

## Task 4 — `ChunkPool`, `TerrainChunk` anchor, upload (M2b)

- `TerrainChunk` gains `public double3 Anchor` and a `Rebase(double3 origin)` that sets
  `transform.position = (float3)(Anchor − origin)`. Keep the existing `AllChunks` / `AllActive` static
  sets; `WorldOriginSystem` switches from `position -= shift` to calling `Rebase`. This removes the
  accumulating-float-error flaw of §2.6 as a side effect; M4 keeps the stress test and the camera/rig
  handling.
- `ChunkPool` in `Core/Rendering/`: pre-allocates `GameObject` + `MeshFilter` + `MeshRenderer` +
  `Mesh` (with `MarkDynamic`), hands them out and takes them back. Never destroys a `Mesh` during play.
- Upload on the main thread: `Mesh.ApplyAndDisposeWritableMeshData(dataArray, mesh,
  MeshUpdateFlags.DontRecalculateBounds)`, then set `mesh.bounds` from the job's min/max height and the
  node's chord size.

## Task 5 — `GenerationScheduler` (M2b)

Create `Assets/Scripts/Core/Quadtree/GenerationScheduler.cs`.

- A `Dictionary<NodeId, Pending>` of in-flight work and a binary-heap priority queue keyed by
  `distance / NodeWorldSize` (near-and-coarse first). `NodeId`'s equality and hashing are already tested
  for this use (`NodeIdTests.EqualityAndHashing`).
- `Request(NodeId)` / `Cancel(NodeId)`. Before scheduling, re-check the node is still wanted; on
  completion, if it was cancelled meanwhile, dispose the buffers and drop the result without touching
  the pool. **This is the fix for the orphan-chunk leak of §2.4** — the prototype's `CollapseNode`
  leaves disposed nodes in the queue and `SpawnChunk` later builds chunks for them.
- Cap concurrent jobs at `MaxInFlightJobs` (default 8).
- Poll `JobHandle.IsCompleted`; never call `Complete()` on a handle in the same frame it was scheduled.
- Upload loop is bounded by a `Stopwatch` against `UploadBudgetMs` (default 1.5 ms), not a chunk count.

`TerrainSettings` changes: add `MaxInFlightJobs`, `UploadBudgetMs`, `OctaveWavelength0`, `OctaveCount`,
`Lacunarity`; remove `MaxUploadsPerFrame`, `BaseFrequency`, `Octaves`. Leave `LodDistances` and
`MaxDepth` in place — M3 replaces them with `SplitFactor`.

## Task 6 — The switchover (M2b)

Delete, per `SonomaRevisedPlan.md` §3:

- `Core/CoordinateSpace/BaseMeshQuad.cs`, `BaseMeshFactory.cs`, `CoordinateTransform.cs` (and with them
  the old `Sonoma.Core.CoordinateSpace.SurfaceType`, resolving the CS0104 hazard `CLAUDE.md` warns about).
- `Core/Generation/HeightmapGenerator.cs`.
- `Core/Quadtree/QuadtreeManager.cs`, `QuadtreeNode.cs`, `QuadtreeBounds.cs` — including
  `EdgeDirection`, every `*Stitch*` method, and the `EdgeN/S/E/W` and `EdgeNormal*` caches. Integer
  `NodeId` addressing replaces the float-bounds spatial index.

Keep `WorldOriginSystem` (updated per Task 4) and `TerrainChunk`. Rewire `TopologyFlyCamera` to read
topology from `SurfaceDef` instead of `BaseMeshQuad`.

Add a small `TerrainRoot` MonoBehaviour that owns the `SurfaceDef`, builds roots via
`SurfaceMath.BuildRoots`, and requests one chunk per root from the scheduler. That is the entire driver
for M2.

### Expected regressions (state these in the commit message)

M2 renders **root chunks only** — six on a cube-sphere, `Cols × Rows` on a plane grid. There is no
subdivision, because `LodSelector` is M3. The scene will look dramatically coarser than the prototype
until M3 lands. This is the plan working as intended, not a bug; validating the new pipeline in
isolation is the whole reason M2 stops here.

---

## Task 7 — Tests (the actual deliverable)

New files under `Assets/Tests/EditMode/`. The 21 M1 tests must keep passing untouched.

### `LatticeNoiseTests.cs` (M2a)

1. **`NoiseIsZeroAtLatticePoints`** — 200 random integer points across ±1e6: exactly `0f`.
2. **`NoiseRangeIsWithinUnitInterval`** — 200k seeded samples, assert `|n| <= 1.0`. Also assert
   `max > 0.9` so a change that quietly shrinks the gradient table fails. (Measured 0.9266 at 200k,
   0.9959 at 600k, supremum 1.000000 by hill-climbing.)
3. **`NoiseIsZeroMean`** — 200k seeded samples, `|mean| < 0.01`. (Measured 1.6e-5.)
4. **`NoiseIsDeterministicAcrossThreads`** — evaluate the same 10k points on the main thread and from
   four `Task`s; assert **bit-identical** results (compare `math.asuint`, not `Assert.AreEqual` with a
   delta). This is the property everything else rests on.
5. **`PrecisionDoesNotCollapseAtLargeCoordinates`** — the §2.3 regression test. For
   `|p| ∈ {1e3, 1e6, 1e7, 1e9}` noise cells, assert `|n(p + 1e-3) − n(p)| > 1e-6` — i.e. a 1e-3 cell
   step still moves the value. Measured deltas: 5.4e-4, 1.4e-4, 1.1e-3, 1.3e-3 — flat across nine
   orders of magnitude. **Mutation-check this test:** changing `(float3)(p − fl)` to
   `(float3)p − (float3)fl` must make it fail at 1e7 and 1e9. If it still passes, the test is not
   testing what it claims.
   For context, float32 lattice coordinates are **5.37e8× coarser than double at every magnitude**
   (2^29, independent of `|p|`); at `|p| = 1e7` cells that is a 1-cell ULP — two samples per lattice
   cell, exactly the failure §2.3 predicted for octave 8 of the prototype at Earth radius.

### `HeightFunctionTests.cs` (M2a)

6. **`BandLimitIsDepthOnly`** — assert `MaxOctave(d) == clamp(d + K0, 0, OctaveCount-1)` and that it
   never consults `NodeWorldSize`. Assert `K0 == 4` for the reference config (`λ₀ = S0`, `R = 33`,
   Earth radius) so a change to the derivation is visible.
7. **`SameQuadSharedVerticesAreBitIdentical`** — the seam test that actually can be exact. Two
   same-depth neighbours within one root quad, at depths 0–4: every one of the `R` shared edge vertices
   must produce a **bit-identical** `double3` position and a bit-identical height from both sides.
   Rewriting the C4 lerp must break this.
8. **`CrossQuadSharedVerticesAgreeToDoublePrecision`** — for all 24 cube edges: positions agree to
   **1e-9 × Radius** and heights to `HeightScale × 1e-9`. They are *not* bit-identical, and cannot be:
   the two faces reach the shared edge by different parameterisations, and the measured worst-case
   position discrepancy is **2.067e-16 relative (1.32 nm at Earth radius)** — the same figure M1's
   `SharedEdgePointsCoincide` measures. See "Corrections to the parent plan".
9. **`HeightIsZeroMeanOverASphere`** — 50k points over the cube-sphere, `|mean| < 0.02 × HeightScale`.
   Catches a reintroduced `·0.5 + 0.5` bias.

### `GenerationJobTests.cs` (M2b)

10. **`JobsCompileWithoutManagedFallback`** — mark every job `[BurstCompile(CompileSynchronously = true)]`
    and assert no fallback. A managed `static readonly float3[]` gradient table is the likely offender.
11. **`MeshVertexCountsMatchTable`** — assert the C7 counts for `R ∈ {33, 65, 129}`.
12. **`SkirtWindingIsConsistent`** — all skirt triangles on all four edges face outward from the chunk
    (dot of the triangle normal with the outward edge direction has the same sign on all four sides).
    Guards against porting the prototype's reversed West skirt.
13. **`CancelledNodeLeaksNothing`** — request N nodes, cancel them mid-flight, assert every native
    buffer is disposed and no chunk was taken from the pool. This is the §2.4 leak, as a test.
14. **`AdjacentChunkMeshesShareEdgeVertices`** — build two same-depth neighbour chunks through the real
    job path, transform both to world space via their anchors, and assert the shared edge vertices
    coincide to within `1e-6` m (chunk-local floats, so exactness is lost at the anchor subtraction —
    this bound is the float32 ULP at chunk scale, not a fudge).

**Determinism note:** seed every randomised test with a fixed constant so failures reproduce.

---

## Task 8 — Documentation and commit

1. **Correct `SonomaRevisedPlan.md`** (three items, below).
2. **Update `CLAUDE.md`:** move `Core/Surface/` from "additive, nothing calls it" to the live path;
   delete the `CoordinateSpace` entries and the two Non-Obvious notes about the duplicate `SurfaceType`
   and `EdgeDirection` enums (both old enums are gone); replace the "`BuildMesh` is synchronous CPU
   code", "spatial index lifecycle" and "seam stitching only stitches to coarser neighbours" notes with
   the new pipeline's own footguns — the depth-only band limit (C2), the C4 bit-exactness rule, and the
   same-quad-exact / cross-quad-bounded seam distinction.
3. **Commit.** M2a first, then M2b, ending every message with
   `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

---

## Corrections to the parent plan

Verified during derivation; fold into `SonomaRevisedPlan.md` as part of Task 8.

1. **§4.3, "16-bit for R ≤ 181".** The binding constraint is distinct vertices ≤ 65,536, which allows
   **R ≤ 253**. 181 is where the *triangle* count reaches 65,520, which constrains nothing.
2. **§5 M2 test spec, "bit-identical heights at shared vertices, including across a cube edge".** The
   cross-cube-edge half is unachievable and should not be attempted. Bit-identical holds *within* a root
   quad (test 7). Across a cube edge the two faces reach the shared edge by different parameterisations
   and the surface points themselves differ by 2.067e-16 relative. Split the requirement in two, as
   tests 7 and 8 do.
3. **Noise normalisation.** Nothing in the plan says to normalise, but the textbook √3/2 ≈ 0.866 bound
   for 3D Perlin is wrong for the 16-entry improved-noise gradient table: the range is [−1, 1] and the
   bound is attained. Recorded here so nobody "fixes" the amplitude later.

---

## Verification

### Agent-side, without the Editor

```bash
git diff --stat main..m2a-height-function -- Assets/Scripts/Core/Quadtree Assets/Scripts/Core/CoordinateSpace Assets/Scripts/Core/Rendering Assets/Shaders
```

That must print nothing — M2a is additive. Then, after M2b:

```bash
grep -rn "Stitch\|EdgeDirection\|BaseMeshQuad" Assets/Scripts
```

That must print nothing. Also confirm `CoordinateTransform.cs` and `QuadtreeManager.cs` are gone, and
that every `.cs` under `Assets/Scripts` and `Assets/Tests` has a `.meta` alongside it.

### Human-side, in the Editor

1. Console free of compile errors and of Burst fallback warnings.
2. Test Runner → EditMode: the 21 M1 tests plus the 14 new ones all pass.
3. `SampleScene` plays and shows six root chunks on a cube-sphere with no gaps at face boundaries.
4. Profiler: terrain work on the main thread is upload-only and stays under `UploadBudgetMs`; the noise
   and mesh work appears on worker threads.
5. Fly to Earth radius with `TopologyFlyCamera` and confirm fine detail is still present — the visible
   proof of §2.3 being fixed. On the prototype the same view is smooth mush.

---

## Definition of done

- [ ] `LatticeNoise.Gradient3D(double3, uint)` and `Fbm`, measured range [−1, 1], zero mean, exact at lattice points.
- [ ] `TerrainHeightFunction.Height` is pure, Burst-compiled, band-limited from depth alone, with `MacroSample` in the signature.
- [ ] `HeightSampleJob` and `ChunkMeshJob` produce positions, normals, UVs, morph targets, morph normals and skirts into a `MeshDataArray`, with no managed allocation.
- [ ] `GenerationScheduler` prioritises, caps in-flight jobs, cancels cleanly, and bounds uploads by time.
- [ ] `ChunkPool` recycles chunks; `TerrainChunk` carries a `double3 Anchor` and rebases from it.
- [ ] `CoordinateSpace`, `HeightmapGenerator`, `QuadtreeManager` and every stitching remnant are deleted.
- [ ] 14 new EditMode tests pass; the 21 M1 tests still pass; test 5 fails under the deliberate float mutation.
- [ ] `SampleScene` plays, showing root chunks only, with terrain generation off the main thread.
- [ ] `SonomaRevisedPlan.md` corrected on all three points; `CLAUDE.md` updated.

## Amendments from implementing M2a

The plan above is the plan as written. Building M2a changed seven things; they are recorded here
rather than edited into the tasks, so the plan still reads as what was decided up front.

1. **`HeightParams.FromSettings` is deferred to M2b.** Task 2 put it in M2a, but it needs
   `OctaveWavelength0`, `OctaveCount` and `Lacunarity` on `TerrainSettings`, which Task 5 adds in M2b —
   and ground rule 1 says M2a touches no existing script. M2a ships `HeightParams.Create(...)` taking
   the values explicitly; M2b adds `FromSettings` on top of it. Ground rule 1 wins over Task 2 here.
2. **`ChunkGrid` exists.** The C4 rule needed one concrete home that both jobs and the seam test call.
   `Core/Generation/ChunkGrid.VertexUV` is it. The plan described the rule but named no owner.
3. **The band-limit hazard does not reproduce at the reference config.** C2 argues `MaxOctave` must
   take a depth because node size varies 1.33:1 within a depth. That is true, and the rule stands — but
   at `λ₀ = S0` exactly, the spread happens to sit inside one power-of-two band at *every* depth 0–10,
   so a size-derived limit would not actually differ there. It differs for **13 of 32** sampled `λ₀`
   multipliers; `BandLimitIsDepthOnly` uses `λ₀ = 1.25 · S0`, where the split appears at depth 3, so the
   test demonstrates a real hazard rather than a hypothetical one.
4. **Nominal `S_d` runs about 0.65 of an octave past strict Nyquist on the largest cells.** `S_d = S0/2^d`
   halves exactly; actual node size does not, and `max actual / nominal` converges to 1.568 (measured
   1.5196 at depth 4, 1.5676 at depth 8). So the largest cells on a face admit octaves slightly finer
   than Nyquist would allow. This is accepted, not overlooked: it is under one octave, and the lever if
   aliasing ever shows is `K0 − 1`, which stays depth-only. Do not "fix" it by reintroducing a
   size-derived limit.
5. **Test 9 was statistically unsound and is replaced.** `HeightIsZeroMeanOverASphere` at `λ₀ = S0` puts
   only a handful of independent octave-0 cells on the whole planet, so the sphere mean is dominated by
   low-frequency structure and a 0.02 threshold would fail for reasons unrelated to bias.
   `HeightHasNoConstantBias` uses `λ₀ = Radius/1000` so 50k samples span millions of cells.
6. **The cross-quad height tolerance is `HeightScale × 1e-5`, not `× 1e-9`.** Positions agree to
   2.067e-16 relative, but the noise accumulates in float, so inputs differing in their last double bits
   round differently at ~1e-7 relative per octave. 1e-9 is below float rounding and would fail for
   reasons that have nothing to do with the seam. 1e-5 of `HeightScale` is 2 mm at the reference
   settings.
7. **No `[BurstCompile]` attribute on `TerrainHeightFunction`.** Task 2's listing shows one. Plain static
   methods are compiled as part of whichever job calls them; the attribute would only matter for a
   function pointer, so it is noise that implies a guarantee it does not provide. The M2b test
   `JobsCompileWithoutManagedFallback` is what actually checks this.

## Out of scope for M2

Do not start these:

- **LOD selection, subdivision, collapse, hysteresis, preload margins.** M3. M2 renders root chunks only.
- **The vertex-shader morph.** M3. M2 writes the morph *attributes* and the shader ignores them.
- **Removing skirts.** They stay until M3 proves geomorphing works.
- **The macro layer, biomes, erosion.** M5. `MacroSample` is a zero-filled placeholder.
- **Colliders, NavMesh, the debug overlay, the heightmap cache.** M6.
- **The floating-origin stress test and texture-swimming decision.** M4. M2 adds the anchor field; M4 audits it.
- **Lazy infinite plane-grid roots.** Still M3, as M1 deferred it.
