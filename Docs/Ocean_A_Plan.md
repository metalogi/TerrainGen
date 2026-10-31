# Ocean-a — A Flat Sea on the Terrain's Nodes: Implementation Plan

**Audience:** a coding agent working in `G:\UnityProjects\TerrainGen` on the `ocean-a` branch, with the
parent plan to hand.
**Parent plan:** `Docs/Ocean_Plan.md`. Its conventions C1–C11 and questions Q1–Q10 are referred to by
number here and are not repeated. This document turns its Tasks 1, 2, 5, 6 and 7, as far as they belong
to `ocean-a`, into ordered steps against the code as it stands after M5c-2 (main at 0da8c16).
**Prerequisite:** main at 0da8c16 or later, all EditMode tests green. `Assets/Materials/TerrainBiomes.mat`
has an uncommitted change that is the user's; leave it alone, as with `ProjectSettings/` and
`Assets/Settings/`'s URP assets and `Build Profiles/`.

`ocean-a` delivers an opaque, flat sea at each stress scene's sea level, drawn on the terrain's own
quadtree nodes (C2) and morphed with them (C3), from orbit to the ground, with the fly camera held above
it (C10). No waves (`ocean-b`), and every node still samples terrain (`ocean-c`).

---

## What changed since the parent plan was written

`Docs/Ocean_Plan.md` was drafted before the continents, erosion and the gully term. Four things are
different now, and the first decision below follows from them.

1. **Sea level is already chosen, and already in the code.** The continent field's zero is the coast,
   so the four continent scenes have sea level 0. Erosion's `ErosionConfig.BaseLevel` is each scene's
   sea level (M5c-1's D1): 0 on Earth, the small planet, the ring and the narrow ring, and +68 m in the
   terrarium (`Assets/Settings/*Erosion.asset`). Q2 is settled; `SeaLevel` only has to agree with it.
2. **C8's bound has terms the parent plan did not price.** `Between(d)` is a Lipschitz bound on the
   band-`d` height between vertices. Today's height function multiplies each octave by band gains that
   vary across the surface (biomes, and ruggedness on the mid band), so the bound needs the gains'
   own slope. Above the gully start each octave is divided by `1 + a|g|²`, whose derivative brings in
   the coarser octaves' second derivatives and the wetness map's slope. And `Residual(d)`'s macro term
   must bound filtered levels (continents, and erosion's correction), not truncated ones. Each term is
   derivable, but each is a derivation, a measured constant and a mutation test.
3. **The height function may change again.** M5c-2's D2 (steered gullies, G2) is still open. A bound
   derived now would be derived again if G2 goes in.
4. **`TopologyFlyCamera`'s readout already says Land or Ocean,** by `_groundHeight > 0.0`. That is right
   by accident in four scenes and wrong in the terrarium.

---

## Decisions to confirm before building

Each has a recommendation, which the steps below assume. If one is answered the other way, the steps
it touches are marked.

### A1 — In `ocean-a`, a chunk's kind comes from its own samples, not from its parent's bound

**Recommended.** C8 exists so a node's kind is known **before** its job runs. Two things need that:
sizing the mesh buffers, and, in `ocean-c`, skipping the height sampling of Ocean nodes. In `ocean-a`
nothing skips sampling, so the job knows its own heights exactly, and what a chunk draws is exactly its
vertices: the fine grid, the coarse morph targets, and the straight lines between them. So:

- **Drawn range.** `lo` and `hi` are the min and max over the R × R fine heights *and* the coarse
  heights at the morph-target vertices. A morphing vertex lies between its fine and coarse height, and
  a triangle between its vertices, so every frame of every morph state lies in `[lo, hi]`. Skirts are
  excluded, since they hang below the surface and show only through a crack.
- **Kind.** Land if `lo > SeaLevel + MaxWaveAmplitude`; Ocean if `hi < SeaLevel − OceanTerrainOverlap`;
  Coast otherwise. `MaxWaveAmplitude` is 0 until `ocean-b`.

This is exact, so it cannot hide an island or drain a lake, and it needs no bound, no measured noise
constant and no test 4. What it needs is a way to size the buffers after the heights are known: step 0's
spike.

C8 then moves to `ocean-c`, and shrinks. Pruning only has to predict "Ocean, sample nothing", which
needs `U` alone; Land and Coast stay exact. So `ocean-c` derives one bound, after M6 and after D2,
against the height function that will ship.

**The alternative** is the parent plan as written: `OceanBounds` with `Between` and `Residual` for
today's function, test 4 with its mutations, test 5, and a child taking its kind from its parent's
`OnChunkReady`. Children are often requested before their parent's chunk exists (`LodSelector.Descend`
wants all four on a split, and `Preload` does too), so those nodes would have to default to Coast. If
A1 is answered this way, Step 3 gains `OceanBounds` and tests 4–5 of the parent plan, Step 4 gains the
kind on `GenerationScheduler.Enqueue`, and step 0's spike is unnecessary.

### A2 — Ocean chunks draw water only, already in `ocean-a`

**Recommended, and it follows from A1.** The parent plan's C2 keeps the terrain mesh on Ocean nodes
until `ocean-c`. A chunk whose drawn range is wholly under the sea draws terrain that opaque water hides
and that cannot shade anything visible (C5's argument for no ShadowCaster pass applies). So an Ocean
chunk is the water grid alone, at the size of a terrain chunk, and vertex memory rises only by the Coast
share: the factor is `1 + (Coast share)`, not `1 + (Coast + Ocean share)`. On Earth's worst-case working
set of 8,222 chunks at 97.7 KB, that is about 0.80 GB today plus 97.7 KB per Coast chunk.

The chunks are still sampled, still report their real height range to the selector, and so the working
sets, the relief warning and every LOD figure are unchanged. What stays for `ocean-c` is skipping the
sampling and the flat bounding sphere.

### A3 — `TerrainSettings.SeaLevel` is the sea level, and must equal erosion's base level

**Recommended.** Sea level is a property of the world, so `SeaLevel` lives in `TerrainSettings` whether
or not `Ocean` is on. `ErosionConfig.BaseLevel` stays where it is (moving it touches every erosion test
for no gain), and `TerrainRoot` refuses a scene where both are set and differ, before the macro build.
`StressSceneAssetTests` holds each asset pair equal. One number in two homes is how two figures drift;
the check is what keeps them one.

### A4 — Two shader includes, not one

**Recommended.** C5 names one file, `SonomaCommon.hlsl`. The morph needs only `Core.hlsl`, but the sky
code takes URP's `Light` and so needs `Lighting.hlsl`, which the ForwardLit passes include and the depth
passes need not. So:

- `Assets/Shaders/SonomaCommon.hlsl`: the four per-frame globals (`_SonomaWorldOrigin`,
  `_SonomaMorphRanges` with `SONOMA_MAX_MORPH_DEPTH`, `_SonomaViewPosition`, `_SonomaLodVertical`) and
  `SonomaMorphFactor`, `SonomaMorph`, `SonomaMorphPosition`, comments included. Included from each
  shader's `HLSLINCLUDE`, after `Core.hlsl` and after the shader's own `UnityPerMaterial` block.
- `Assets/Shaders/SonomaSky.hlsl`: under `#if defined(_SONOMA_SKY)`, the sky globals, `SonomaAltitude`,
  `SonomaOpticalDepth`, `SonomaHazeLight`, and a new `SonomaApplyHaze(color, positionWS, topoUp, ml)`
  lifted from the two lines in the terrain's `Frag` that compose the haze, so both shaders composite it
  identically. Included from each ForwardLit after `Lighting.hlsl`.

`_SonomaBiomeColors` stays in the terrain shader. Record the split in the parent plan's amendments.

### A5 — Colour by water depth comes forward from `ocean-b`

**Recommended.** The floor height is written into every water vertex in `ocean-a` anyway (C3,
`TEXCOORD3.w`), and the shading is a lerp between two colours. Without it the coastline is judged
against a uniform blue, and the shallows, which are where the coast's quality shows, are invisible.
The swap pop Q6 describes comes with it; leave the fix (a morphed floor) to `ocean-b`.

### A6 — Which scenes, and Q7

- **`NarrowRingStress` gets an ocean** (Q9: yes). It has continents and sea level 0 like the others.
- **Q7, climate from sea level, is deferred.** It changes macro bits only where `SeaLevel` is nonzero,
  which is the terrarium's 68 m, and moves its temperatures by 68 m of lapse. Not worth a re-pin
  of the biome tests in this branch.

---

## Conventions for `ocean-a`

These refine C1–C4 for the code as it is. Where they differ from the parent plan, the difference is
named.

### A7 — Kinds, vertices, indices, submeshes and materials

| kind | vertices | index buffer | submeshes | `sharedMaterials` |
|---|---|---|---|---|
| Land | terrain, `[0, n)` | single | 1 | `[terrain]` |
| Coast | terrain `[0, n)`, then water `[n, 2n)` | double | 2: `(0, I)`, `(I, I)` | `[terrain, ocean]` |
| Ocean | water, `[0, n)` | single | 1 | `[ocean]` |

`n = ChunkMeshLayout.VertexCount(R)` (`R² + 4R`, 1,221 at R = 33) and `I = IndexCount(R)`. The single
index buffer is today's. The double one is today's followed by today's offset by `n`, so the water
winds as the terrain does on every topology. An Ocean chunk uses the single buffer, since its water sits
at `[0, n)`. So there are two shared index arrays per `(R, winding)`, not three.

A scene with `Ocean` off writes only Land chunks, by the same code it runs today, so `SampleScene`'s
meshes, submesh count and material are unchanged (ground rule 1).

The 16-bit limit with an ocean is R ≤ 179 (C4). `HeightParams.Create` cannot see `Ocean`, so the check
goes in `OceanParams.Create` (Step 2), and `ChunkMeshBuffers.IndexFormatFor` takes the vertex count of
the largest kind the scene can produce.

### A8 — The water vertex

For each sample `s` of the fine grid, with `sea = (float)SeaLevel` narrowed once:

| attribute | water value |
|---|---|
| `POSITION` | `(float3)(BasePoints[s] + (double3)(BaseNormals[s] * sea) − Anchor)` |
| `NORMAL` | `BaseNormals[s]` |
| `COLOR`, `TEXCOORD0` | 0 |
| `TEXCOORD1` | `(BaseNormals[s], sea)` |
| `TEXCOORD2` | `(coarse base point + coarse base normal × sea − Anchor, Depth)` at the even vertex `cs` |
| `TEXCOORD3` | `(CoarseBaseNormals[cs], Heights[s])`: the morph target's up, and the fine floor height |

The position is the terrain's own expression (`ChunkMeshJob.Local`) with the height replaced by the sea
level, not a call to `SurfaceMath.SurfaceFrame`. The two are the same surface point (C1); the shared
expression also makes terrain and water the same bits wherever a height equals the sea level, so the
coastline sits exactly where the heights cross it. The morph target is the parent's water vertex, bit
for bit, because the coarse base points are the parent's base points; that is what
`ChildAtFullMorphEqualsParent` checks for the terrain, and it carries over.

**Skirts.** The water's drop is `ChunkMeshLayout.SkirtDrop(SkirtDepth, SkirtFraction, 0, ChordSag)`:
no relief term (the water has none), the chord sag whole, and no wave term until `ocean-b`. With skirts
off it is 0, as the terrain's is.

**Bounds.** `VertexBounds` is the box round every vertex written, of either kind, and their morph
targets, as now.

### A9 — What the job reports

`HeightBounds` grows from 3 floats to 6: `[0] min, [1] max` of the fine heights (unchanged: the
selector's sphere), `[2]` the terrain skirt drop, `[3] lo, [4] hi` the drawn range (A1), and `[5]` the
kind as a float (0 Land, 1 Coast, 2 Ocean). `GenerationScheduler.Result` gains `Kind`, and
`LodSelector.NodeState` stores it so the selector can count resident chunks by kind (Step 6). The kind
does not enter selection.

`ChunkKind` is a `byte` enum in `Core/Generation/ChunkKind.cs`, UnityEngine-free, with
`ChunkKinds.Classify(float lo, float hi, float sea, float overlap, float waveAmplitude)`. The job calls
it, and so do the pure tests.

---

## Step 0 — Baselines and the buffer-sizing spike (no commit)

### Baselines

Take these on main before changing anything, so the branch has something to be compared against. Record
them in the parent plan's amendments section as they are taken.

1. **Chunk throughput and upload time.** M5c-2 still owes its "before and after" throughput, and this is
   the "before" for `ocean-a` too. In `EarthStress`, Editor play mode, a fixed descent from orbit: chunks
   built per second, and `GenerationScheduler.Upload`'s time per chunk (wrap it in a Profiler marker for
   the measurement; keep the marker if it is cheap, which M6 wants anyway).
2. **Peak working sets** from each stress scene's end-of-session report, one descent each.
3. **Reference frames for the shader extraction (Step 1).** `SampleScene`, `EarthStress` and
   `RingworldStress` from fixed camera poses, captured through unity-mcp's `Unity_Camera_Capture`, at
   fixed time and with the same window size.

### The spike: who sizes the mesh buffers

A1 decides the kind inside the job, so the vertex count is not known when `GenerationScheduler.Schedule`
calls `SetVertexBufferParams` today. Try, in order, in a throwaway EditMode test run over unity-mcp:

- **S1, the job sizes them.** Pass a persistent `NativeArray<VertexAttributeDescriptor>` and the two
  shared index arrays to a `[BurstCompile]` `IJob`; inside `Execute`, call
  `Mesh.MeshData.SetVertexBufferParams(count, attrs)`, `SetIndexBufferParams`, write vertices and
  indices, set `subMeshCount` and `SetSubMesh`. Then `ApplyAndDisposeWritableMeshData` on the main
  thread. It passes if the safety system raises nothing, Burst compiles it (no managed fallback in the
  console), and the mesh has the vertex count, values, submeshes and bounds written.
- **S2, the main thread shrinks them.** Size for `2n` before scheduling, have the job write the kind's
  vertices at the front, and call `SetVertexBufferParams(actual)` on the main thread before applying. It
  passes only if the prefix survives the call.
- **S3, the fallback, documented API only.** The job writes into a pooled persistent
  `NativeArray<TerrainVertex>` of capacity `2n`; `Upload` calls `Mesh.SetVertexBufferParams`,
  `SetVertexBufferData(scratch, 0, 0, count)`, `SetIndexBufferParams`, `SetIndexBufferData` from the
  shared array, and `SetSubMeshes`, all with `DontRecalculateBounds | DontValidateIndices |
  DontNotifyMeshUsers`. The scratch arrays are pooled across chunks, so nothing allocates per chunk.

Prefer S1: it moves the index copy and the submesh setup off the main thread as well, which ground rule 3
welcomes. Take S3 if S1 fails; S2 only if it is clearly cleaner than S3. Record which was taken, and
why, in the amendments, and correct `CLAUDE.md`'s "Set buffer parameters before scheduling" in Step 8.

A two-phase schedule (sample, read the range on the main thread next frame, then allocate and mesh) also
works, and is not recommended: a frame of latency per chunk, and a second main-thread touch per chunk.

---

## Step 1 — Shared shader includes (commit)

A pure refactor. Nothing rendered may change.

1. Create `SonomaCommon.hlsl` and `SonomaSky.hlsl` as A4 says, moving the code and its comments
   verbatim. Each file opens with a header naming the shaders that include it and why the code is
   shared: two shaders computing `k` from copies of this code would drift.
2. `SonomaTerrainTriplanar.shader`: replace the moved code with the includes. Keep the order:
   `Core.hlsl`, textures, `UnityPerMaterial`, `SonomaCommon.hlsl`. In ForwardLit, `Lighting.hlsl` then
   `SonomaSky.hlsl`. `Frag` calls `SonomaApplyHaze`.
3. **Tests that read shader source read the effective source.** `GeomorphTests` (three tests) and
   `ShaderShadowTests` read the `.shader` file with `File.ReadAllText`. Add a helper, `ShaderSource.Load
   (name)`, in the test assembly, that reads `Assets/Shaders/<name>` and splices in every
   `#include "Sonoma*.hlsl"` relative to it, once, recursively, leaving URP's includes alone. Point the
   four reads at it. `ShaderMorphsInEveryPass` checks the passes' `Attributes` in the `.shader` and
   `SonomaMorphFactor`'s body, which is now in the include; with the splice both are found where they
   were, and a test that checked one file and missed the other is not possible.
4. **Same pixels.** Recapture the reference frames from step 0 and compare. They should be identical; a
   difference is a defect in the move. Also compare the shader's compiled variant count in the Inspector
   (or `ShaderUtil.GetShaderData`) before and after.

## Step 2 — Settings and validation (commit)

1. `TerrainSettings`, in a new `[Header("Ocean")]` group after the sky's, tooltips in the house style:
   - `public bool Ocean;`
   - `public double SeaLevel;` metres along the surface normal, above the base surface (C1). Used by the
     ocean, and checked against erosion's base level (A3).
   - `public float OceanTerrainOverlap;` metres, default 0. In `ocean-a` it only decides how far under
     the sea a chunk must lie to be Ocean; a positive value keeps terrain on chunks just under the sea.
     `ocean-b` sets it to at least `MaxWaveAmplitude`.
2. `TerrainRoot`: `public Material OceanMaterial;` beside `ChunkMaterial`.
3. `Core/Generation/OceanParams.cs`, UnityEngine-free: a struct with `Enabled`, `SeaLevel` (double),
   `SeaLevelFloat`, `TerrainOverlap`, `WaveAmplitude` (0), and `static OceanParams Create(bool ocean,
   double seaLevel, float overlap, int resolution, double envelope, bool hasErosion, double
   erosionBaseLevel)`. It refuses, with constant messages:
   - a non-finite `SeaLevel`, or a negative or non-finite overlap;
   - with `Ocean` on, `|SeaLevel| ≥ envelope` (C1), where the envelope is `MacroMaps.MaxAbsHeight`, as
     `LodVertical` takes it;
   - with `Ocean` on, `resolution > MaxResolutionWithOcean` (179, a constant on `ChunkMeshLayout` next
     to `MaxResolutionFor16BitIndices`, with the arithmetic in its comment);
   - with erosion, `SeaLevel != erosionBaseLevel` (A3), whether or not `Ocean` is on, because the
     erosion's outlets are the sea either way.
   `OceanParams.Disabled` is the default, and `default(OceanParams)` must mean "no ocean", as
   `default(MacroSample)` means "no macro layer".
4. `TerrainRoot.Start`: build it after the macro maps, which give the envelope and before the
   scheduler, and refuse an `Ocean` scene with no `OceanMaterial`. Log one line: sea level, overlap.
5. **Assets** (values only; scenes and materials are Step 6): `Ocean` on and `SeaLevel` 0 in
   `EarthTerrainSettings`, `SmallPlanetTerrainSettings`, `RingworldTerrainSettings` and
   `NarrowRingTerrainSettings`; on and 68 in `TerrariumTerrainSettings`. `TerrainSettings.asset`
   (`SampleScene`) unchanged; if re-saved it gains the new fields at their defaults, `Ocean` off.

Commit with the settings unused, so the next step's diff is only the mesh path.

## Step 3 — Layout and kinds, pure (commit)

All UnityEngine-free, so the out-of-Editor harness runs it.

1. `ChunkKind.cs`: the enum and `ChunkKinds.Classify` (A9). Ties: `lo == SeaLevel + wave` is Coast, and
   so is `hi == SeaLevel − overlap`. A NaN height is Coast, the kind that draws everything.
2. `ChunkMeshLayout`:
   - `VertexCount(int R, ChunkKind kind)`: `n` or `2n`. The one-argument `VertexCount` stays, as the
     terrain grid's count.
   - `IndexCount(int R, ChunkKind kind)`: `I` or `2I`.
   - `WaterBase(int R, ChunkKind kind)`: `n` for Coast, 0 for Ocean.
   - `BuildIndices(int R, bool flipWinding, bool doubled)`: the doubled buffer is the single one, then
     the single one offset by `n`. The 16-bit guard compares the *doubled* vertex count with 65,536 when
     `doubled`, so R 181 throws there and R 253 still builds a single buffer.
   - `MaxResolutionWithOcean = 179`.
   - `WaterSkirtDrop(skirtDepth, skirtFraction, chordSag)`: `SkirtDrop` with a relief of 0, so the rule
     lives in one place.
3. Pure tests (new `OceanLayoutTests.cs`):
   - `KindIsDecidedByTheDrawnRange`: each threshold and its tie, the overlap and wave arguments moving
     them, NaN.
   - `OceanIndexLayout` (parent test 3): per kind, the counts; the doubled buffer's second half is the
     first offset by `n`; the water winds as the terrain does, on every topology, by the same geometric
     check `ChunkMeshLayoutTests` uses for the terrain.
   - `OceanCapsTheResolution` (parent test 2, second half): R 179 builds a doubled buffer and 181
     throws; 253 still builds a single one; `OceanParams.Create` refuses 181 with `Ocean` on and accepts
     it with `Ocean` off.
   - `SeaLevelOutsideTheEnvelopeIsRefused` (parent test 2, first half), and
     `SeaLevelMustBeTheErosionBaseLevel` (A3), and `DefaultOceanParamsMeanNoOcean`.
   - `WaterSkirtCoversTheChordSag`: the water is a smooth surface at radius `R + SeaLevel` (`R −
     SeaLevel` inside a cylinder), so its whole transient gap is the chord sag, and
     `StressSceneTests.ChordSagCoversASmoothSurface`'s check applies with the radius moved. Run it for
     every stress scene at every depth, gaps 1 to `SkirtDepthGap`, morph 0, ½ and 1. This is the
     parent's test 7 without waves; `ocean-b` adds the wave term.

## Step 4 — The mesh job, the scheduler and the pool (commit)

The mechanism is step 0's; this is written for S1, with S3's differences noted.

1. **`ChunkMeshJob`.** New fields: `OceanParams Ocean` (a blittable struct), `float WaterSkirtDrop`
   (computed by the scheduler, it needs no heights), the attribute array and the two shared index
   arrays `[ReadOnly]`. `Execute` becomes:
   1. One pass over the fine R × R heights (min, max, as now) and over the coarse heights at the morph
      targets: `lo`, `hi`.
   2. `kind = Ocean.Enabled ? ChunkKinds.Classify(…) : ChunkKind.Land`.
   3. Size the buffers for `kind` and write the index data and submeshes (S1). Under S3, skip this;
      `Upload` does it.
   4. Unless Ocean, write the terrain vertices and skirts exactly as now. Not a line of the existing
      writes changes; that is ground rule 1's evidence in the diff.
   5. Unless Land, write the water vertices at `WaterBase(R, kind)`, A8's values, and their skirts, by
      `SkirtTopIndex` and `SkirtBottomIndex` offset by the base.
   6. `VertexBounds` over every vertex written; `HeightBounds[0..5]` as A9.
2. **`GenerationScheduler`.** Takes `OceanParams` in its constructor (default: none). `Schedule` passes
   it and the water drop to the job; under S1 it no longer calls `SetVertexBufferParams` and
   `SetIndexBufferParams`, and the attribute array becomes a persistent field allocated once (it is a
   `Temp` per chunk today). `Upload` reads the kind, sets the chunk's materials (below), and under S1 no
   longer copies indices or sets the submesh. `Result` gains `Kind`. `Pending.HeightBounds` is 6 long.
3. **`ChunkMeshBuffers`.** `SharedIndices(R, flipWinding, doubled)`, keyed by all three.
   `IndexFormatFor(R, ocean)`. `DisposeCache` frees all of them.
4. **`ChunkPool` and `TerrainChunk`.** The pool takes both materials and builds three cached
   `Material[]`, one per kind, once. `TerrainChunk.SetKind(ChunkKind, Material[])` assigns
   `MeshRenderer.sharedMaterials` only when the kind differs from the chunk's last, so a recycled chunk
   of the same kind costs nothing and nothing allocates per upload. `TerrainChunk.Kind` is public, for
   the tests and reports. A chunk created by the pool starts as Land with the terrain material, as now.
5. **`TerrainRoot`** passes `OceanParams` to the scheduler and `OceanMaterial` to the pool.
6. **Editor tests**, in `GeomorphTests` and a new `OceanChunkTests`:
   - `ChildAtFullMorphEqualsParent`, extended (parent test 8): for a Coast parent and child over a real
     macro layer, every water morph target is the parent's water vertex, bit for bit, and the surviving
     water triangles are the parent's index for index.
   - `JobDecidesTheKindFromWhatItDraws`: the real job, on `EarthStress`'s configuration, at a node over
     a continent's interior (Land, `n` vertices, one submesh), one across a coast (Coast, `2n`, two), and
     one over deep ocean (Ocean, `n`, water values at `[0, n)`). Find the nodes from the continent map,
     not by hand-picked addresses, so a retune of the continents does not silently move them.
   - `SeaSurfaceIsTheBaseSurfaceOffset` (parent test 1): water positions equal `SurfaceMath.SurfacePoint`
     offset by `SeaLevel` along the normal, to within float rounding of the anchor-relative value, on all
     three topologies; inside a cylinder the radius is `R − SeaLevel`.
   - `WaterSkirtHangsItsDrop`, as `JobHangsTheSkirtItReports` does for the terrain.
   - `OceanOffBuildsTodaysMesh`: with `OceanParams.Disabled`, the job's vertices, indices, submesh and
     bounds equal a run of the job at main's commit, frozen as expected values for one node of
     `SampleScene`'s configuration (or equal byte-for-byte to the terrain half of a Coast chunk of the
     same node, which needs no frozen values; prefer that).
   - `JobHangsTheSkirtItReports` and the others that read `HeightBounds` move to the 6-float layout.

## Step 5 — The ocean shader and material (commit)

1. `Assets/Shaders/SonomaOcean.shader`, header in the terrain shader's style: what it draws, the
   attribute meanings (A8), why it has no ShadowCaster pass (C5), and the parent plan's C-numbers.
   - `HLSLINCLUDE`: `Core.hlsl`, its own `UnityPerMaterial` (`_ShallowColor`, `_DeepColor`,
     `_DepthScale`, `_Smoothness`, `_FarSmoothness`, `_GlintFarDistance`, `_FresnelStrength`), then
     `SonomaCommon.hlsl`.
   - **ForwardLit.** The terrain's main-light shadow keywords exactly (`ShaderShadowTests` pins the set),
     `_SONOMA_SKY` as a local `shader_feature`. Vertex: `SonomaMorph` on position and normal, the
     elevation and floor carried to the fragment. Fragment: the shadow coordinate per fragment under
     cascades, and `GetMainLight(shadowCoord, positionWS, half4(1,1,1,1))`, both as the terrain does it
     and for the same reasons; colour `lerp(_ShallowColor, _DeepColor, 1 − exp(−depth / _DepthScale))`
     with `depth = max(elevation − floor, 0)` (A5); Lambert on the morphed up; a Blinn-Phong glint whose
     smoothness falls from `_Smoothness` to `_FarSmoothness` with the log of distance, so the sea from
     orbit shows a broad highlight rather than nothing; a Schlick term towards the sky ambient at grazing
     angles; under `_SONOMA_SKY` the hemisphere ambient (straight up, so the sky's) and
     `SonomaApplyHaze`.
   - **DepthOnly** and **DepthNormals**, each declaring `TEXCOORD1` and `TEXCOORD2` (`TEXCOORD3` where
     the normal morphs) and morphing as the terrain's do.
   - No ShadowCaster pass.
2. `Assets/Materials/Ocean.mat`, `_SONOMA_SKY` on. Start from a deep `(0.02, 0.07, 0.15)`, a shallow
   `(0.05, 0.35, 0.38)`, `_DepthScale` 20 m, and tune by eye in Step 7.
3. **Editor tests** (parent tests 9 and 10):
   - `ShaderMorphsInEveryPass` runs over both shaders: every pass each has reads `TEXCOORD1` and
     `TEXCOORD2` and morphs; `SonomaMorphFactor` reads `_SonomaViewPosition` and `_SonomaLodVertical`;
     both shaders get it from `SonomaCommon.hlsl` and neither defines its own (search the unspliced
     `.shader` text for the function name).
   - `OceanHasNoShadowCaster`: the ocean has exactly ForwardLit, DepthOnly and DepthNormals.
   - `ShadowCoordIsNotInterpolatedUnderCascades`, the fade overload, and `ShadowKeywordsMatchThePipeline`
     over the ocean's ForwardLit.
   - The sky test: `_SONOMA_SKY` on in `Ocean.mat`.

## Step 6 — Scenes, fly camera and reports (commit)

1. **Scenes.** In `EarthStress`, `SmallPlanetStress`, `RingworldStress`, `NarrowRingStress` and
   `TerrariumStress`, set `TerrainRoot.OceanMaterial` to `Ocean.mat`. `SampleScene` unchanged.
2. **`StressSceneAssetTests`** (parent test 11): each stress scene's `Ocean`, `SeaLevel` and
   `OceanTerrainOverlap` match the tests' copies, its scene names `Ocean.mat`, and `SeaLevel` equals its
   erosion asset's `BaseLevel`. `SampleScene`'s settings have `Ocean` off and its scene no ocean
   material. Read the scenes through `SceneConfig`, as the other scene checks do.
3. **`TopologyFlyCamera`** (C10). `TerrainRoot` exposes `HasOcean` and `SeaLevel`. The fly camera's
   ground is `max(HeightAt(p), SeaLevel)` when there is an ocean: clearance, speed, the automatic clip
   planes and the clearance it reports to `WorldOriginSystem` all follow the water over the sea. The
   readout keeps "Above base surface" and the ground's height, says Land or Ocean by the ground against
   `SeaLevel` (fixing the `> 0.0` in the terrarium), and adds "Above sea". If the clearance rule is
   worth a test, extract it as a static function the test calls, as
   `EarthSettingsAssetTests.FlyCameraClipPlanesStayCullableAndTight` already calls
   `TopologyFlyCamera.AutoClipPlanes`.
4. **Reports.** `LodSelector` counts resident chunks by kind (incremented in `OnChunkReady`, decremented
   in `Forget` and on replacement, from `NodeState.Kind`) and keeps the counts at the frame of its peak
   working set. Its end-of-session report adds the Land, Coast and Ocean counts at that peak and their
   vertex memory, `(Land + Ocean + 2 × Coast) × n × 80 B`. Plain counters on fields: the frame path stays
   allocation-free (`SelectorDoesNotAllocatePerFrame`). A `LodSelectorTests` case checks the counts over
   a scripted run with mixed kinds.

## Step 7 — Measure and record (commit)

In the Editor, each stress scene, one descent from orbit to a coast and a flight along it:

1. Console clean, tests green (Burst witness compiled), `SampleScene` identical to the reference frame
   and its reports identical to step 0's.
2. The peak working set (unchanged from step 0, since selection is unchanged), the Coast and Ocean counts
   at the peak, and the vertex memory against today's.
3. Chunk throughput and upload time against step 0's. The job writes up to twice the vertices on Coast
   chunks; S1 moves the index copy into the job. Record both; a rise in upload time against the 1.5 ms
   budget is a finding for M6, not a reason to stop.
4. Land fraction at the eroded terrain, per scene, from `MacroMapExport` (next item) against the 30%
   target: a check, not a retune (Q2).
5. **`MacroMapExport`** gains `<scene>_sea.png`: the full-detail height at the export's points against
   `SeaLevel`, sea in the ocean material's colours by depth, land hillshaded, with the coast contour, and
   logs the land fraction. `<scene>_coast.png` is the continent field's coast; this is the sea's.

Record the figures in the parent plan's amendments section, and tune `Ocean.mat` by eye.

## Step 8 — Documentation and PR

1. `CLAUDE.md`:
   - Current Status and the Milestones list: `ocean-a` done, what it is.
   - Core Architecture: a short "Ocean" subsection: sea level per scene, kinds by drawn range, water on
     the terrain's nodes, the mesh layout per kind.
   - Mesh Specifications: the water's meaning of each attribute (A8), the per-kind counts, R ≤ 179 with
     an ocean.
   - Non-Obvious entries: the water needs the morph too (C3: the chord sag, kilometres at coarse
     depths); the kind is decided from what the chunk draws, fine and coarse, and why that is exact; the
     halved 16-bit limit; the shared includes and why a copy would drift; who sizes the mesh buffers now
     (step 0's result), correcting "Set buffer parameters before scheduling"; `SeaLevel` and erosion's
     `BaseLevel` held equal.
   - Code Organization and the test counts.
2. Shader headers: both shaders and both includes.
3. `Docs/Ocean_Plan.md`, amendments section: A1–A6 as decided, step 0's spike, the baselines and the
   measurements. Do not edit the plan's body; the amendments say where it changed.
4. `SonomaRevisedPlan.md`: a line placing the ocean among the milestones, if it is not there yet (Q1).
5. Push `ocean-a` and open the PR against main, its description listing the measurements and anything
   left unverified.

---

## Tests, at a glance

| test | where | step | parent test |
|---|---|---|---|
| `KindIsDecidedByTheDrawnRange` | pure | 3 | — |
| `OceanIndexLayout` | pure | 3 | 3 |
| `OceanCapsTheResolution`, `SeaLevelOutsideTheEnvelopeIsRefused` | pure | 3 | 2 |
| `SeaLevelMustBeTheErosionBaseLevel`, `DefaultOceanParamsMeanNoOcean` | pure | 3 | — |
| `WaterSkirtCoversTheChordSag` | pure | 3 | 7, without waves |
| `ChildAtFullMorphEqualsParent`, extended to water | Editor | 4 | 8 |
| `JobDecidesTheKindFromWhatItDraws`, `SeaSurfaceIsTheBaseSurfaceOffset`, `WaterSkirtHangsItsDrop`, `OceanOffBuildsTodaysMesh` | Editor | 4 | 1 |
| `ShaderMorphsInEveryPass` over both, `OceanHasNoShadowCaster`, the shadow tests over the ocean | Editor | 5 | 9, 10 |
| `StressSceneAssetTests`, extended | Editor | 6 | 11 |
| the selector's kind counts | Editor | 6 | — |

Parent tests 4 and 5 (the bound) go to `ocean-c` with C8 (A1). Tests 6 and 14 are `ocean-b`'s, 12 and 13
`ocean-c`'s.

The pure files must stay free of `UnityEngine`, `Unity.Collections`, `Unity.Jobs` and `Unity.Burst`, and
run in the out-of-Editor harness with every existing pure test unchanged: that is ground rule 2's
evidence, since nothing in the height function moves.

---

## Verification

**Agent-side.** The out-of-Editor harness, then the full suite in the Editor over unity-mcp
(`TestRunnerApi` through `Unity_RunCommand`), as for M5c-2. Every existing test passes unchanged except
the ones this plan names as moving to the 6-float `HeightBounds` or to the spliced shader source.

**Human-side, in the Editor.** In each stress scene:

- the sea from orbit, with a glint, and the coastlines where the export puts them;
- the coastline through a descent: it moves smoothly as the LOD changes, never pops, never flickers
  with z-fighting along shallow shores;
- no cracks in the water along cube edges, at LOD boundaries or during a fast descent;
- shallow water reads lighter than deep, and a faint colour pop at a swap is expected (Q6);
- over the sea the fly camera's speed and near plane follow the water, and its readout says Ocean;
- on the rings and in the terrarium, water reaching the open rims is expected (C11);
- `SampleScene` looks and reports as before.

---

## Definition of done

- [ ] A1–A6 answered, and the answers in the parent plan's amendments.
- [ ] `SampleScene` renders and reports exactly as at main; every scene's heights are bit-identical.
- [ ] Every stress scene has a flat sea at its `SeaLevel`, equal to its erosion base level, from orbit to
      the ground, on every topology.
- [ ] Each chunk is Land, Coast or Ocean by what it draws, and an Ocean chunk is water alone.
- [ ] A fully morphed child's water is its parent's, vertex for vertex and triangle for triangle.
- [ ] The terrain shader draws the same pixels through the shared includes; the ocean shader morphs in
      every pass it has and has no shadow caster.
- [ ] The fly camera stays above the sea.
- [ ] Coast and Ocean shares, vertex memory, throughput and upload time recorded against the baselines.
- [ ] All tests pass; `CLAUDE.md`, the shader headers and the amendments are written; the PR is open.

---

## What `ocean-a` hands on

- **To `ocean-b`:** `OceanParams.WaveAmplitude` enters `Classify` and the water skirt drop, and
  `OceanTerrainOverlap` rises to at least it. The vertex has unused `COLOR` and `TEXCOORD0` on water,
  eight bytes. If the floor is to morph (Q6), a UNorm8 depth encoding (`1 − exp(−depth / scale)`) fits
  there for the fine and coarse floor, and keeps `TEXCOORD3.w` free.
- **To `ocean-c`:** C8, reduced to `U` alone (A1), derived against the height function as it stands
  after M6 and D2; the Ocean prediction for unsampled nodes; skipping their sampling; their flat sphere.
  The drawn-range kind stays for every sampled node, and an unsampled Ocean node's children are Ocean
  without a bound, as the parent plan says.

---

## Risks

- **The spike fails all three ways.** Unlikely, since S3 is ordinary documented API. If it costs too much
  main-thread time, the two-phase schedule is the last resort.
- **Coast share larger than expected.** A chunk at depth 0 or 1 spans a cube face or a quarter of one, so near orbit
  most chunks are Coast; the memory factor there approaches 2. The working set from orbit is small, so
  the absolute cost is too; measure the peak, which is near the ground (Step 7).
- **Z-fighting along very flat shores.** Erosion flattens land to `BaseLevel + LandClearance`
  (`ErosionPass.LandClearance` is 1e-7 m) where it fills hollows, so there are plains a hair above the
  sea. The terrain and water are then coplanar to the float. If it flickers, the fix is a small depth
  bias on the ocean's ForwardLit and DepthOnly (`Offset`), not a change to the heights.
- **Material per submesh and the SRP Batcher.** Two shaders on one renderer is ordinary, but check the
  Frame Debugger that Coast chunks batch with their kinds rather than breaking every batch.

---

## Amendments from implementing `ocean-a`

Record here what building it changed, as the other plans do: the spike's result, the baselines, the
measurements, and anything the Editor said that this plan did not.

1. **A1–A6 answered (2026-10-29): all as recommended.** Kinds come from each chunk's drawn range;
   Ocean chunks are water alone; `SeaLevel` is held equal to erosion's `BaseLevel`; two includes; depth
   colour in `ocean-a`; the narrow ring has an ocean and Q7 waits.
2. **Step 0's spike (2026-10-29): S1 works, under Burst, so the job sizes its own buffers.** A
   `[BurstCompile(CompileSynchronously = true)]` `IJob` in the test assembly, with the
   `BurstWitnessJob` sentinel reporting it compiled, called `Mesh.MeshData.SetVertexBufferParams`
   with a `[ReadOnly] NativeArray<VertexAttributeDescriptor>` of `ChunkMeshBuffers.VertexAttributes()`,
   `SetIndexBufferParams(…, UInt16)`, wrote `TerrainVertex`es and a doubled R = 33 index buffer, set
   `subMeshCount` 2 and both submeshes, and `ApplyAndDisposeWritableMeshData` on the main thread
   produced a mesh of 2,442 vertices and two submeshes, the second starting at index 6,912 with its
   first index offset by 1,221, the last vertex's values intact. No safety-system error, no Burst
   diagnostic, console clean. S2 also works (shrinking a `MeshData`'s vertex params on the main thread
   kept the prefix), but S1 is taken: it also moves the index copy and the submesh setup off the main
   thread. The throwaway job was deleted after the run. `CLAUDE.md`'s "Set buffer parameters before
   scheduling" becomes "the mesh job sets them" in Step 8.
3. **Step 0's baselines (2026-10-29), on `ocean-a` at 7baa721: main plus this plan and the
   `GenerationScheduler.Upload` Profiler marker, one sample per chunk landed.** Editor play mode, Burst
   on, the Editor in front. A temporary Editor script, `OceanABaselineDriver`, ran every scene from a
   queue. Nothing could be sent to the Editor during play: a unity-mcp command compiled in play mode
   ended the session. The script is untracked and goes once Step 1 has recaptured the frames.
   - **How it measured.** Each scene settled at its start pose: no job in flight, and no upload for
     90 ticks. It captured a frame, then descended along the local up from the start clearance to 20 m
     above `TerrainRoot.HeightAt`, exponentially in clearance, over 90 s (60 s in the terrarium). At the
     bottom it turned to look along the ground 10° down, settled, and captured again. `ProfilerRecorder`s
     read `GenerationScheduler.Upload` and the three Burst job markers. Worker time is each job marker's
     total over the chunks meshed. A first pass lost its descent figures because
     `ProfilerRecorder.Reset` also stops the recorder; it was rerun.
   - **The descent.**

     | scene | start clearance | chunks landed | fps | upload per chunk | worker per chunk: fine / coarse / mesh |
     |---|---|---|---|---|---|
     | `EarthStress` | 998 km | 5,532 (61.5/s) | 81 | 0.128 ms | 1.48 / 0.41 / 0.21 ms |
     | `SmallPlanetStress` | 249 km | 4,768 (53.0/s) | 85 | 0.122 ms | 1.44 / 0.41 / 0.20 ms |
     | `RingworldStress` | 97.9 km | 3,016 (33.5/s) | 105 | 0.123 ms | 1.52 / 0.43 / 0.20 ms |
     | `NarrowRingStress` | 51.6 km | 3,005 (33.4/s) | 106 | 0.124 ms | 1.58 / 0.45 / 0.20 ms |
     | `TerrariumStress` | 836 m | 512 (8.5/s) | 122 | 0.120 ms | 1.47 / 0.41 / 0.18 ms |

     A descent's rate is set by how fast it asks for chunks, not by capacity. Capacity shows in the
     start-up burst: 880–2,240 chunks in 3–7 s, 290–330 chunks a second (361 on Earth in the first pass),
     at 0.116–0.148 ms of upload per chunk. Upload is about 0.12 ms a chunk everywhere. The job that
     dominates is the fine height sample, gully derivatives included, at about 1.5 ms of worker time a
     chunk near the ground. Ocean-a's Coast chunks add up to n vertices to the mesh job, which is about
     0.2 ms; the measure for Step 7 is upload and mesh time per chunk against these.
   - **Peak working sets**, from each session's report: Earth 6,750 of 9,400; small planet 6,022 of
     8,300; ring 4,304 of 5,600; narrow ring 3,812 of 4,900; terrarium 2,752 of 4,400. `SampleScene` has
     no descent (30 chunks from its start pose). Rebases: 1–4 a session, the slowest 4.09 ms over 6,570
     chunks on Earth. No relief warning, no error.
   - **Reference frames.** The frames are 1920 × 1080, rendered offscreen through
     `RenderPipeline.SubmitRenderRequest` from the active camera, not through `Unity_Camera_Capture`, and
     written to `Logs/OceanA/ref_<scene>_start.png` and `_bottom.png` (gitignored). Every scene has both
     except `SampleScene`, which has only a start frame. The start frames are byte-identical between the
     two passes for Earth, the small planet and the ring; `SampleScene`'s and the others' were taken once.
     So Step 1's same-pixels check compares the start frames, with the driver rerun as it is. The bottom
     frames are
     not reproducible. The timed descent ends a few centimetres differently each run (the ring's bottom
     differed between passes), so they are for the eye only.
4. **Step 1 (2026-10-29): the includes moved nothing rendered.** `SonomaCommon.hlsl` and
   `SonomaSky.hlsl` hold the code A4 names, comments verbatim; `SonomaApplyHaze` takes the two lines
   `Frag` composed the haze with, and their comment. `_SonomaBiomeColors` stays in the terrain shader.
   - **Same pixels.** All six scenes' start frames, recaptured by the step 0 driver after the move,
     are byte-identical to step 0's: `SampleScene` and all five stress scenes.
   - **Same variants.** `ShaderUtil.GetPassKeywords` reports the keywords the unchanged pragmas
     declare, pass by pass, so the variant count is unchanged. The shader compiles with no messages.
   - **Same tests.** `ShaderSource.Load` splices the `Sonoma*.hlsl` includes, and the four reads use
     it. `ShaderMorphsInEveryPass` found `SonomaMorphFactor`'s end by eight spaces of indentation,
     which the include's top-level function does not have; it now matches braces. All 228 EditMode
     tests pass in the Editor, with the eleven `[Explicit]` tools skipped.
5. **Step 2 (2026-10-29): the settings and their checks, unused.** `TerrainSettings` has `Ocean`,
   `SeaLevel` and `OceanTerrainOverlap`, `TerrainRoot` has `OceanMaterial`, and `OceanParams.Create`
   refuses what the plan lists. `ChunkMeshLayout.MaxResolutionWithOcean` came forward from Step 3,
   since `Create` needs it. Three things differ from the plan.
   - **`Ocean` stays off in the assets until Step 6.** `TerrainRoot` refuses an `Ocean` scene with no
     `OceanMaterial`, and the scenes get theirs in Step 6. Turned on now, every stress scene would stop
     generating for four commits. The assets carry their sea levels already: 0, and 68 in the terrarium.
   - **The two refusals come before the macro build.** `OceanParams.CheckSeaLevel`, A3's check, is
     public and runs first, as is the missing-material check, so neither waits seconds of erosion to
     fail. `Create` repeats the sea-level check, so a caller cannot skip it. The sea level and the
     float base level are compared as floats.
   - **`OceanParams`' tests are in this step.** `OceanLayoutTests` has
     `DefaultOceanParamsMeanNoOcean`, `SeaLevelOutsideTheEnvelopeIsRefused`,
     `SeaLevelMustBeTheErosionBaseLevel` and `OceanCapsTheResolution`'s `OceanParams` half; Step 3 adds
     the layout's. `StressSceneAssetTests.SeaLevelIsTheErosionBaseLevel` holds the five stress assets'
     sea levels to their erosion and `SampleScene` to none. 233 EditMode tests pass in the Editor.
6. **Step 3 (2026-10-29): the kinds and the layout, pure.** `ChunkKind` and `ChunkKinds.Classify`,
   and in `ChunkMeshLayout` the per-kind `VertexCount` and `IndexCount`, `WaterBase`,
   `BuildIndices(R, flipWinding, doubled)` and `WaterSkirtDrop`, as the plan lists them.
   - **An undecidable range is Coast.** `Classify` asks the range to be ordered as well as above or
     below the sea, so a NaN at either end, a NaN sea, and an empty range (lo above hi, which is where
     a min-max scan starts) are all Coast, the kind that hides nothing. `WaterBase` throws for Land.
   - **The two-argument `BuildIndices` is the single buffer, unchanged.** The doubled one refuses R 181
     on its 2n vertices; the single one still builds 253.
   - **`WaterSkirtCoversTheChordSag` runs every depth and every gap, unthinned,** on a new
     `StressSceneTests.SceneHeights.Water`: the smooth surface with every height the sea level, so its
     vertices are the job's water vertices. The sea level is each scene's erosion base level, so the
     terrarium's water is 68 m inward. The chord sag, at the base surface's radius, covers it there too:
     the rule's 16 spacings at the node-size spread leave room for a radius 0.7% smaller. The worst
     water gaps are Earth 6,403 m, the small planet 1,601, the ring 30.0, the terrarium 2.99 and the
     narrow ring 0.90. The narrow ring's is under the 1 m floor, so the test requires a gap past the
     floor everywhere else, which is what makes it a test of the sag; the first run asked it of the
     narrow ring too and failed there, with every skirt covering its gap.
   - `OceanIndexLayout` checks the winding on every grid triangle of both halves, not only the first.
   - 236 EditMode tests pass in the Editor, with the eleven `[Explicit]` tools skipped; the seven in
     `OceanLayoutTests` take about a second.
7. **Step 4 (2026-10-29): the mesh job decides the kind and sizes its own buffers (S1).** `ChunkMeshJob`
   takes one pass over the fine heights and the morph targets for `[0, 1]` and `[3, 4]`, classifies,
   then sets the vertex and index buffer parameters, copies the single or doubled shared index buffer
   and sets one or two submeshes, all inside the job. The terrain writes are the old loop moved into
   `WriteTerrain` unchanged; the skirt loop is shared by both grids (`WriteSkirts`, offset by the grid's
   base). `GenerationScheduler` no longer sets buffer parameters, copies indices or sets the submesh;
   `Upload` reads the kind from `HeightBounds[5]` and calls `TerrainChunk.SetKind` with the pool's
   cached `Material[]`. `Result` carries `Kind`; storing it in `LodSelector.NodeState` waits for Step 6,
   where the reports use it.
   - **The doubled buffer is built only with an ocean.** Without one the scheduler passes the single
     buffer as both, since at R above 179 the doubled one cannot be built and nothing reads it. The
     vertex attribute array is a persistent scheduler field, freed in `Dispose`; it is not `readonly`,
     because `Dispose` on a `readonly` struct field frees a copy and a second `Dispose` (which
     `MacroMapsOutliveTheJobs` makes) would free it again.
   - **`ChunkPool` takes the ocean material as an optional fourth argument;** its index format and
     cached arrays follow from whether it has one. `TerrainRoot` passes it only when the ocean is on.
   - **The tests share one job driver.** `ChunkJobRunner.Build` runs the three real jobs and reads back
     vertices, indices, submeshes, both bounds arrays and the sample jobs' own outputs. `GeomorphTests`
     uses it in place of its private copy.
   - **`JobDecidesTheKindFromWhatItDraws` sweeps rather than picks.** On Earth's eroded maps, built by
     Burst, it runs 216 depth-7 nodes (a 6 × 6 grid on every face), takes each drawn range from the sample
     jobs' heights, classifies it independently and holds the job's range, kind, counts, submeshes and
     grid placement to it. It found 46 Land, 31 Coast and 139 Ocean chunks and requires at least one of
     each, so a retune of the continents cannot leave it testing one kind. The plan asked for three nodes
     found from the continent map; the final terrain's coast is not the map's (96% agreement), so the
     sweep reads the terrain as built.
   - **The water geomorph runs on forced Coast chunks.** `ChildAtFullMorphEqualsParent` gains a run over
     the four-level macro layer with a sea whose overlap and waves are far past the relief, so the parent
     and all four children are Coast. Each child water vertex's morph target is the parent's water vertex
     (0 m apart on the plane tile), its morph normal is the parent's to the bit, and every surviving water
     triangle is one in the parent's own index buffer, 32 a child.
   - **`SeaSurfaceIsTheBaseSurfaceOffset`** holds positions to 4e-7 of their anchor-relative magnitude,
     plus a millimetre; the worst was 0.13 of that. Normals are held to 1e-6, not to the bit, because
     Burst's trigonometry is not managed code's in the last bit.
   - **`OceanOffBuildsTodaysMesh`** compares the job with no ocean to the terrain half of a Coast chunk of
     the same node on a cube sphere, bit for bit, plus the single submesh and the shared single buffer.
   - 240 EditMode tests pass in the Editor, with the eleven `[Explicit]` tools skipped. No play session
     has run this step's scheduler path; the selector tests drive it through real uploads.
8. **Step 5 (2026-10-29): `SonomaOcean.shader` and `Ocean.mat`, drawn by nothing until Step 6.** The
   shader has ForwardLit, DepthOnly and DepthNormals, its own `UnityPerMaterial` with the seven
   properties the plan names, and the morph and sky from the two includes. ForwardLit is the plan's
   list.
   - **The glint's smoothness falls with `log2(distance) / log2(_GlintFarDistance)`,** from
     `_Smoothness` at a metre to `_FarSmoothness` at the far distance. The Blinn-Phong lobe is
     normalised by `(n + 8) / 8π`, so a broad far highlight is dimmer than a near one.
   - **Fresnel without the sky reflects `SampleSH` along the reflected view ray;** with it, the sky
     ambient. The water's ambient is the terrain's hemisphere formula, which for a flat sea is the sky's.
   - **Starting values:** shallow `(0.05, 0.35, 0.38)`, deep `(0.02, 0.07, 0.15)` and 20 m, as the plan
     gives; smoothness 0.9 near and 0.4 far, a far distance of 1,000 km and a Fresnel strength of 0.6.
     All to be tuned by eye in Step 7.
   - **Compiled, not only imported.** `ShaderUtil.CompilePass` over every pass, with the sky on and off,
     under no shadows, `_MAIN_LIGHT_SHADOWS` and `_MAIN_LIGHT_SHADOWS_CASCADE` with
     `_SHADOWS_SOFT_MEDIUM`, left no shader messages.
   - **Tests.** `ShaderShadowTests` is a fixture over both shaders, so its three tests are six cases.
     `ShaderMorphsInEveryPass` checks every pass of both (`ShaderSource.NodeShaders`) and, in each
     `.shader` file unspliced (`ShaderSource.Raw`), that it includes `SonomaCommon.hlsl` and defines no
     `SonomaMorphFactor` of its own. `OceanShaderTests` holds the pass set (`OceanHasNoShadowCaster`), the
     sky feature with `Ocean.mat` running the shader with `_SONOMA_SKY` on, and the colour by depth. 246
     EditMode tests pass in the Editor.
9. **Step 6 (2026-10-29): the ocean is on in the five stress scenes.** Each scene's `TerrainRoot` names
   `Ocean.mat`, and each settings asset has `Ocean` on, at the sea levels Step 2 set: 0, and 68 m in the
   terrarium. The scenes were opened and saved by the Editor, which also serialised the fly camera's
   `ShowHeightReadout` for the first time (its default, on). `SampleScene` is untouched.
   - **The fly camera flies above the water.** `TopologyFlyCamera.Ground` is `max(terrain, SeaLevel)`
     with an ocean and the terrain without, and clearance, speed, the clip planes and the clearance
     reported to `WorldOriginSystem` follow it. The readout keeps the base and ground heights, says
     Land or Ocean against `SeaLevel` (so the terrarium's shallows below 68 m read Ocean), and adds
     "Above sea" with an ocean. `Ground` is one `max`, so it has no test of its own.
   - **The kind counts are tallied each frame, not kept incrementally.** `LodSelector.CountResident`
     already walks the resident set every frame for `ResidentCount`; it counts each kind there from
     `NodeState.Kind`, which `OnChunkReady` sets. A replaced or forgotten chunk needs no decrement, so
     nothing can drift. The tally kept for the report is the one at the peak of the resident chunks'
     grids, a Coast chunk counted twice, rather than at the peak of the wanted set: the memory figure
     needs the frame when most vertices existed, and at the wanted set's peak many nodes are not yet
     built. The report adds the three counts and their vertex memory against the same chunks without
     water, and prints nothing new in a session with no water chunk, so `SampleScene`'s report is
     unchanged. `ChunkPool.Resolution` gives it the grid size.
   - **Tests.** `StressSceneAssetTests.StressScenesHaveTheirOcean` replaces `SeaLevelIsTheErosionBaseLevel`:
     each stress scene's ocean on, sea level its erosion's base level, no overlap, and `Ocean.mat` in its
     scene; `SampleScene` with none. `LodSelectorTests.SelectorCountsChunksByKind` runs the selector's
     tile with a sea at 0 (11 Land, 42 Coast, 32 Ocean of 85), holds each node's kind to its chunk's, the
     tallies to the resident set, and the Dispose report to them; `SelectorDoesNotAllocatePerFrame`
     still passes. 247 EditMode tests pass in the Editor.
   - **The play check (2026-10-29).** The step 0 driver reran every scene's start pose with the ocean
     on, the Editor in front. No warning or error in any scene.
     - **`SampleScene`** is byte-identical to step 0's frame, its reports unchanged and no ocean line.
     - **The working sets are unchanged.** Each stress scene's start-pose peak equals the same capture
       with the ocean off (Step 1's): Earth 1,218, small planet 1,254, ring 1,288, narrow ring 884,
       terrarium 2,240.
     - **The sea draws where the seafloor did,** with teal shallows at the coast, the deep colour
       offshore, haze over the far water and, on the planets from orbit, the broad glint. On the ring
       the water fills exactly step 0's lavender seafloor. Inland basins below sea level fill too, since
       ocean-a has one sea level everywhere. The colours read dull and grey against the land; tuning is
       Step 7's.
     - **What the water costs at each start pose:**

       | scene | resident | Land / Coast / Ocean | vertex memory, with water / without |
       |---|---|---|---|
       | `EarthStress` | 1,218 | 168 / 624 / 426 | 171.6 / 113.5 MB |
       | `SmallPlanetStress` | 1,254 | 276 / 612 / 366 | 173.8 / 116.8 MB |
       | `RingworldStress` | 1,288 | 420 / 399 / 469 | 157.2 / 120.0 MB |
       | `NarrowRingStress` | 884 | 6 / 86 / 792 | 90.4 / 82.3 MB |
       | `TerrariumStress` | 2,240 | 1,577 / 342 / 321 | 240.5 / 208.7 MB |

       From orbit the coarse chunks are large and half of them span a coast, so Earth's water adds 51%.
       Step 7 measures it at the bottom of a descent, where chunks are small and Coast is rarer.

10. **Step 7 (2026-10-30): measured on all five stress scenes, and nothing moved that should not have.**
    Each scene ran step 0's vertical descent with the same timing, then a second session: down to a
    coast the driver found by walking the surface, 30 s along it holding the sea on one side, then
    settled views at 50 m, 3 km and 100 km (the terrarium 20 m, 1.2 km and 4 km). The Editor in front,
    Burst on. No warning or error from the terrain in any session; the only others are the Joystick
    and Account API notices. The figures come from a temporary driver, deleted after this step.
    - **Tests:** all 247 pass in the Editor with the tuned material, and the 11 `[Explicit]` tools skip.
    - **`SampleScene`** is byte-identical to step 0's frame, peak 30, no ocean line. Its one rise, the
      mesh job from 0.126 to 0.326 ms a chunk, is 22 jobs in 3 s and does not recur in any larger sample.
    - **The descents, against step 0:**

      | scene | peak, of budget | chunks landed | upload ms a chunk | mesh job ms | fps | at the bottom |
      |---|---|---|---|---|---|---|
      | `EarthStress` | 6,750 → 6,742 of 9,400 | 5,532 → 5,524 | 0.128 → 0.107 | 0.206 → 0.207 | 81 → 89 | 59 → 61 |
      | `SmallPlanetStress` | 6,022 → 6,022 of 8,300 | 4,768 → 4,768 | 0.122 → 0.114 | 0.195 → 0.229 | 85 → 83 | 65 → 59 |
      | `RingworldStress` | 4,304 → 4,304 of 5,600 | 3,016 → 3,016 | 0.123 → 0.116 | 0.199 → 0.224 | 105 → 102 | 92 → 94 |
      | `NarrowRingStress` | 3,812 → 3,812 of 4,900 | 3,005 → 2,996 | 0.124 → 0.121 | 0.196 → 0.103 | 106 → 115 | 98 → 105 |
      | `TerrariumStress` | 2,752 → 2,752 of 4,400 | 512 → 512 | 0.120 → 0.111 | 0.178 → 0.224 | 122 → 117 | 120 → 118 |

      Selection is unchanged, as planned. Four peaks are equal; Earth's is 8 lower, two sibling groups,
      because the descent is not frame-locked and it ended 4 m from step 0's. The narrow ring descends
      over open sea (Land 6, Coast 86, Ocean 3,720 at its peak), and its mesh job halves because an
      Ocean chunk writes only the flat water grid. Elsewhere the job is up to 26% dearer with the water,
      on worker threads. Upload rose nowhere. Its small falls are within what the machine varies by, so
      they are not credited to anything.
    - **Vertex memory at each session's peak,** with water and without:

      | scene | descent: Land / Coast / Ocean | MB | coast flight: Land / Coast / Ocean | MB |
      |---|---|---|---|---|
      | `EarthStress` | 5,141 / 972 / 629 | 718.6 / 628.1 (+14%) | 3,515 / 1,878 / 1,809 | 845.8 / 670.9 (+26%) |
      | `SmallPlanetStress` | 4,625 / 891 / 506 | 644.0 / 561.0 (+15%) | 2,452 / 2,128 / 1,914 | 803.2 / 604.9 (+33%) |
      | `RingworldStress` | 3,184 / 462 / 658 | 444.0 / 400.9 (+11%) | 1,780 / 901 / 1,927 | 513.2 / 429.3 (+20%) |
      | `NarrowRingStress` | 6 / 86 / 3,720 | 363.1 / 355.1 (+2%) | 1,275 / 587 / 2,166 | 429.9 / 375.2 (+15%) |
      | `TerrariumStress` | 2,089 / 342 / 321 | 288.2 / 256.4 (+12%) | 1,365 / 698 / 1,197 | 368.7 / 303.7 (+21%) |

      A coast is the expensive place, and the small planet's +33% is the most any session reached. The
      coast sessions' peak working sets are 7,234, 6,510, 4,660, 4,072 and 3,292 in the table's order,
      17% to 25% under budget. They differ from the descents' because the camera is somewhere else:
      selection does not read the kind.
    - **Throughput and upload in flight.** 195–303 chunks/s along the coasts and 203–365 down to them,
      with mean upload 0.12–0.21 ms a frame, and 0.35–0.55 while settling, far under the 1.5 ms budget.
      That is a mean; no peak frame was measured. Upload per landed chunk reads 0.04–0.06 ms in flight,
      which is not comparable with a descent's: Earth landed 12,169 chunks on the way down and never
      held more than 4,093, so most were probably dropped unwanted, without an upload.
    - **Finding for M6: throughput follows the frame rate.** Jobs are started and collected once a
      frame with `MaxInFlightJobs` 8, and about 3 to 5 chunks land per frame in every scene. Start-up
      runs at 283–313 chunks/s against step 0's 291–330, and the terrarium's fall, 330 to 283, is its
      frame rate, 96 to 82, at 3.44 and 3.45 chunks a frame. So in flight the tree does not reach its
      depth: at 200 m/s, 50 m up, Earth holds 3,264 chunks against the 7,202 it settles to, and takes
      22 s to get there (small planet 17 s, ring 9 s, narrow ring 13 s). No flight was measured without
      water, so this cannot say what the ocean adds, and the terrarium's lower start-up frame rate is
      unexplained. Frame rate is 47–84 fps along the coasts and 39–61 while settling.
    - **Rebases in the coast flights** ran to 206 a session and 14 in a second, the slowest 7.31 ms over
      4,094 chunks, because the driver crosses hundreds of kilometres at low clearance. That is its
      flight path, not the ocean's: the descents kept to 1–5.
    - **Land at the eroded terrain, by area at `MaxDepth`,** against the 30% target. A check, not a
      retune; every scene is within 0.6 of a point:

      | scene | land | highest above the sea | deepest below it |
      |---|---|---|---|
      | `EarthStress` | 29.8% | 10,414 m | 14,581 m |
      | `SmallPlanetStress` | 29.9% | 7,735 m | 9,266 m |
      | `RingworldStress` | 29.7% | 8,262 m | 12,500 m |
      | `NarrowRingStress` | 29.9% | 8,985 m | 7,658 m |
      | `TerrariumStress` | 29.4% (sea at 68 m) | 786 m | 784 m |

    - **`MacroMapExport` writes `<scene>_sea.png`** and logs those figures. `Write` is now internal and
      returns the path, so a driver can call it.
    - **`Ocean.mat`, tuned by eye: `_DeepColor` (0.02, 0.07, 0.15) → (0.03, 0.16, 0.33) and
      `_FarSmoothness` 0.4 → 0.5,** with the shader's defaults to match. The grey Step 6 saw was the
      colour's own fault. A material colour is sRGB, so the old deep colour was (0.0015, 0.006, 0.02) in
      linear, nearly black, and what lifted it, haze above all, is neutral. The new one is (0.002, 0.022,
      0.089), and deep water stays blue from 50 m to 100 km. At 0.4 the far glint flooded whole seas
      brighter than the land from orbit. At 0.5 it is a patch by the sun, though its core still goes
      near-white, and the shader has no strength to turn down. Both values were judged on Earth and the
      narrow ring only; the other three scenes share the material and were not recaptured with it, and
      the `_sea.png` images on disk still show the old deep colour. The measurements above were taken
      before the colour changed, and a colour moves none of them.
    - **A `ProfilerRecorder` sentinel, not a terrain fault.** Four coast phases reported worker times
      near −1e9 ms. Each was exactly −2^63 ns over the phase's job count: one sample of `long.MinValue`
      on a Burst job's marker. With it taken out they read as the others do (fine 1.548, 1.867 and 1.572
      ms, mesh 0.189). Anything that reads these markers has to skip it.
    - **The hard diagonal edge across the sky in some coast frames** looks like the skybox's own
      horizon, tilted because the local up is not world Y. It is nothing to do with the sea.
