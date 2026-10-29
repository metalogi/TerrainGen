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
