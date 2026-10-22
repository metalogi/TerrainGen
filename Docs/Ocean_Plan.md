# Ocean — Sea Level and the Water Surface: Exact Implementation Steps

**Audience:** a coding agent working in `G:\UnityProjects\TerrainGen` with no prior context on this project.
**Parent plan:** `SonomaRevisedPlan.md` (repo root). The ocean is not one of its milestones; it was out of
scope for M5 ("a separate system by design", `Docs/M5_MacroLayer_Plan.md`). Where it sits relative to M6
is open question Q1, and where it sits relative to M5c (erosion, planned) is Q10.
**Prerequisite:** M5a and M5b are complete and merged, and so is `NarrowRingStress` (main at b290fdb or
later). All EditMode tests should pass before you start. Local changes in `ProjectSettings/` and
`Assets/Settings/` (the URP assets and `Build Profiles/`) may be present; they are the user's, so leave
them alone.

The stress scenes have continents (M5a) and biomes (M5b), but nothing marks where the sea is. This plan
adds a sea at a **fixed altitude above the base surface**, on every topology. It is opaque, and the camera
never goes under it. Close up, a vertex shader animates it with waves. Seen from a distance it is a flat,
shaded surface, all the way out to orbit. Because the water is opaque, terrain only has to reach a
little way below the sea, and terrain that lies well under it can stop subdividing.

The recommended design puts the water on **the terrain's own quadtree nodes**, as a second payload beside
the terrain mesh. It is not a second tree or a camera-centred grid. That choice is C2, and most of the
rest follows from it: the water inherits selection, residency, eviction, the floating origin, culling
and, above all, the geomorph. So the coastline is drawn by two surfaces that agree on `depth − k` at
every point. Most of this document is about keeping the invariants that made M2–M5 work while
that happens: heights still a pure function, a fully morphed child still its parent, and `SampleScene`
still bit-identical.

---

## Ground rules

1. **`SampleScene` does not change.** It gets no ocean, keeps one submesh per chunk and keeps its
   material. Its meshes stay bit-identical, and so do its relief and working-set reports.
2. **The ocean never enters the height function.** `TerrainHeightFunction.Height` and both sample jobs
   are untouched, so every scene's heights are bit-identical to M5's, and every relief figure pinned
   against them stays valid. This is `CLAUDE.md`'s "Water Bodies: separate system, not integrated into
   heightmap", and it still holds: the water shares the tree, not the height.
3. **Nothing new per frame on the main thread** beyond pushing a few shader globals. `TerrainRoot.Update`
   is already over its 2 ms budget, which is M6's to fix. Classifying a node (C8) happens once, when its
   parent's chunk arrives, not every frame.
4. **Three landable branches.**
   - **`ocean-a-flat`.** Sea level, the classification bound (C8), water patches on land/coast chunks,
     the ocean shader without waves, the fly camera held above the sea. No pruning: every node still
     samples terrain.
   - **`ocean-b-waves`.** The wave displacement and its precision rules (C6, C7), shading by water depth.
   - **`ocean-c-pruning`.** Ocean-only nodes: no height sampling, no terrain mesh, a flat bounding
     sphere. Working sets re-measured.
5. **Measure before concluding.** When a figure here disagrees with the Editor, the Editor is right;
   record it in the amendments section.

---

## Conventions and settled decisions

### C1 — Sea level is an altitude above the base surface, per scene

`TerrainSettings` gains `public bool Ocean;` and `public double SeaLevel;` (metres along the surface
normal). The water surface is the base surface offset by `SeaLevel`, which `SurfaceMath.SurfaceFrame`
already evaluates exactly when given `SeaLevel` as the height:

| topology | water surface | "up" |
|---|---|---|
| cube sphere | sphere of radius `R + SeaLevel` | outward |
| plane grid | plane `y = Origin.y + SeaLevel` | `+Y` |
| cylinder | cylinder of radius `R − SeaLevel` | inward, towards the axis |

That is the same altitude convention as `LodVertical.Altitude` and the shader's `SonomaAltitude`, so a
point's height above the sea is `altitude − SeaLevel` on every topology, with no special cases.

Validation goes in `HeightParams.Create` (or a sibling `OceanParams.Create`), once:

- `SeaLevel` is finite;
- `|SeaLevel| < LodVertical.Envelope`. Outside the envelope `LodVertical` shifts altitudes rather than
  scaling them, so the water would be flattened by the camera's rule rather than the terrain's.
  Nothing would crack, but the coastline's `k` would no longer match the land's;
- with `Ocean` on, `ChunkResolution ≤ 179` (C4).

`SeaLevel` 0 is the natural first setting: the detail height is zero-mean and the macro base height
is centred on 0. What land fraction that gives in each scene has not been measured (Q2).

### C2 — Water is a second payload on the terrain's nodes, not a second tree

Three alternatives were weighed:

- **A second quadtree for water.** It reuses `NodeId` and `SurfaceMath` cleanly. But it doubles the
  selector walk, which is the cost M6 is fighting. And the two trees morph independently, so at a coast
  land and water are at different `depth − k` and the shoreline swims.
- **A camera-centred clipmap or projected grid**, bent onto the surface in the vertex shader. This is
  the usual game approach, cheap and independent of world size. But it covers neither orbit nor a
  cylinder's far wall, so it needs a second, far-field mesh anyway. It knows nothing about land, so the
  coastline is a depth intersection between two unrelated tessellations. On the shallow slopes of a
  coast that intersection moves with every terrain morph.
- **Clamping the terrain in its own shader**, drawing `max(h, SeaLevel)` flat and blue. This is the
  cheapest, and its coastline is exact. But it can't prune underwater terrain, because pruning removes
  the tessellation the waves need, and it gives no overlap for shore shading. It is worth keeping as a
  diagnostic only.

On the terrain's nodes, every node gets one of three **kinds**, decided before it is scheduled (C8):

| kind | terrain mesh | water mesh | height sampling |
|---|---|---|---|
| **Land** | yes | no | yes |
| **Coast** | yes | yes | yes |
| **Ocean** | no (`ocean-c`; until then, yes) | yes | no (`ocean-c`) |

Roots have no parent to classify them from, so they are Coast.

What comes free with that:

- **Selection, residency and eviction.** The water is in the chunk, so the atomic swap and the
  sibling-group eviction cover it.
- **The floating origin.** The chunk's `Anchor` places both meshes, and `WorldOriginSystem` rebases them
  in its existing pass.
- **Culling.** `VertexBounds` grows to include the water's vertices.
- **The geomorph.** The water grid is the terrain grid at a different height, so it gets morph targets by
  the same rule (C3). Land and water at one surface point compute the same `k`, so the coastline moves
  continuously through every LOD transition.

### C3 — The water grid is the terrain grid at sea level, morph targets and skirts included

`ChunkMeshJob` (or, for Ocean nodes, the cheap `WaterMeshJob` of Task 4) writes, for each vertex
`(i, j)` of the same `R × R` grid:

- the position `SurfaceFrame(u, v, SeaLevel)` relative to the chunk's `Anchor`;
- the morph target, the even vertex `(i & ~1, j & ~1)` at sea level. On a curved surface that differs
  from the vertex by the chord sag alone, which is kilometres at Earth's coarse depths. So the water
  needs the morph as much as the terrain does, even though it is flat;
- skirts, with a drop of `SkirtDepth + ChordSag(depth) + 2 × MaxWaveAmplitude`. There is no relief
  term, because the water has no relief; the wave term covers two neighbours displaced in opposite
  directions during a transient state (`ocean-b`; 0 before).

The water vertex reuses `TerrainVertex`, all 80 bytes, so a chunk keeps one vertex stream and one layout.
The ocean shader reads some attributes differently from the terrain shader:

| attribute | terrain meaning | water meaning |
|---|---|---|
| `POSITION` | surface point | sea-surface point |
| `NORMAL` | shading normal | the base up (topoUp) |
| `COLOR`, `TEXCOORD0` | splat, morph splat | unused, written 0 |
| `TEXCOORD1` | `(topoUp, elevation)` | `(topoUp, SeaLevel)`: `LodVertical` reads `.w`, so it must be the sea level |
| `TEXCOORD2` | `(morph position, depth)` | the same |
| `TEXCOORD3` | `(morph normal, morph elevation)` | `(morph topoUp, floor height)`: the terrain height at this vertex, for shore shading (C9). An Ocean chunk writes `−Envelope` |

The floor height is the vertex's own, not the morph target's. It drives shading only, and a pop of
shallow-water colour at a swap is the cost; see Q6.

### C4 — One mesh per chunk, one or two submeshes, and the 16-bit limit halves

A Coast chunk's mesh holds the terrain vertices, then the water vertices, with two submeshes and a
renderer whose `sharedMaterials` is `[terrain, ocean]`. A Land chunk keeps today's single submesh. An
Ocean chunk has the water vertices only, one submesh and `[ocean]`. The scheduler sets buffer
parameters before scheduling (`CLAUDE.md`, "Mesh writing"), which is why the kind must be known before
the job runs (C8). `ChunkPool` hands out a chunk and sets its materials per kind; meshes are still
recycled, never destroyed during play.

The index buffers stay shared constants, now one per `(resolution, winding, kind)`. The water indices
are the terrain's offset by `R² + 4R`.

A Coast chunk has `2(R² + 4R)` vertices. That fits 16-bit indices up to **R = 179** (179: 64,082 vertices;
181: 66,970). `ChunkMeshLayout.MaxResolutionFor16BitIndices` (253) stays for scenes without an ocean,
and C1 validates the lower limit for scenes with one. Every shipped scene runs R = 33.

**Memory.** A Coast chunk is 195 KB of vertex data against 98 KB. Coast chunks are a band along
coastlines, so this should be a small share of the working set; measure it (Task 6) rather than assume
it.

### C5 — The water passes through the same LOD map, and the shaders share one include

The morph, `LodVertical` and the four-pass rule apply to the water exactly as to the terrain. Extract
the shared vertex code (`SonomaMorphFactor`, the morph lerp, the `_SonomaMorphRanges`,
`_SonomaViewPosition`, `_SonomaLodVertical` and `_SonomaWorldOrigin` declarations, `SonomaAltitude` and
the haze integral) from `SonomaTerrainTriplanar.shader` into `Assets/Shaders/SonomaCommon.hlsl`. Then
both shaders compute `k` from the same code. A copy would drift; this is the shader-side form of "a
test-side copy is how a figure drifts from what it claims to measure".

`SonomaOcean.shader` has ForwardLit, DepthOnly and DepthNormals, each with the morph and (from `ocean-b`)
the waves. **It has no ShadowCaster pass.** The water is never above anything visible it could shade:
the land it meets is above it, and what is under it is hidden. A pass that does not exist cannot
disagree with the others, so the four-pass rule becomes "every pass the shader has".
`GeomorphTests.ShaderMorphsInEveryPass` runs over both shaders. The water **receives** main-light
shadows, with the per-fragment cascade coordinate and the fade overload, as the terrain does
(`ShaderShadowTests`).

### C6 — Waves displace the morphed position, so a fully morphed child is still its parent

The displacement is `W(p)`, where `p` is the vertex's absolute position **after** the morph. The morph's
`k` is still computed from the unmorphed position, as now. At `k = 1` every child vertex sits exactly on
a parent vertex position (three of every four cells collapse onto the even vertices). `W` evaluated
there equals what the parent evaluates, so the fully morphed child is still the parent, waves included,
and the cross-depth boundary argument carries over unchanged. Within a depth, shared vertices have the
same morphed position, so the same displacement. The transient states are the skirts' job (C3).

Wave amplitude fades with distance from `_SonomaViewPosition`, measured at the morphed position. Each
component fades out where the LOD's vertex spacing at that distance stops resolving its wavelength,
four vertices a wavelength, the macro maps' rule (`CLAUDE.md`, "pyramid read by depth"). The fade is
a function of distance, never of depth, so it is continuous and two chunks agree on it at a shared
point. Beyond the last component the surface is flat, and shading takes over (C9).

`W` is mostly along the topoUp. A horizontal Gerstner term, if used, is projected onto the tangent plane.
The amplitude bound `MaxWaveAmplitude` (the sum of component amplitudes) is a constant on the C# side,
because skirts (C3) and the classification margin (C8) both need it.

### C7 — The wave phase is exact through a rebase, by a power-of-two lattice

A float absolute position at Earth radius is quantised to 0.5 m, so a phase computed from
`positionWS + _SonomaWorldOrigin` jitters and jumps at each rebase. That is the triplanar defect
(`CLAUDE.md`, "Triplanar texture coordinates are quantised"), and the same fix applies here, now
made a rule:

- **Every wave vector's components are integer multiples of 2⁻ᴾ cycles a metre**, with P = 16
  (65,536 m). Then shifting a position by 2ᴾ m along any axis changes every phase by a whole number of
  cycles.
- **`_SonomaWaveOrigin`** is the world origin reduced modulo 2ᴾ, per component, in double, then
  narrowed. The shader takes the phase from `positionWS + _SonomaWaveOrigin`, which stays under about
  2ᴾ plus the render-space extent, where a float is good to a few millimetres.
- **`WorldOriginSystem.ShiftOrigin` re-pushes it**, as it re-pushes `_SonomaViewPosition` (`CLAUDE.md`,
  "A rebase has to move every render-space position").
- **Time** is not `_Time`, which loses precision over a long session. Each component's temporal phase
  `ω t mod 2π` is accumulated in double on the CPU and pushed as `_SonomaWavePhase[N]` with the other
  globals.

With 2⁻¹⁶ quantisation, a 50 m wave's direction can be chosen to about 0.05°, which costs nothing
visible. The same lattice is the one the triplanar textures will need (M4 C4), so one global can serve
both later.

A plane wave with a fixed world direction has two "poles" on a sphere, where its direction is along
the local up and its wavelength across the surface grows without limit. With several directions that
isn't a visible defect anywhere, since the other components carry the surface there. Whether it is
good enough on a cylinder, whose up rotates about one axis only, is Q3.

### C8 — The kind is decided from the parent, by a bound that must be a bound

A node's kind must be known before its job is scheduled (C4), so it comes from its **parent's** result.
For a parent at depth `d`, an upper bound `U` and a lower bound `L` on the height anywhere in the parent,
at any depth `≥ d`, are:

```
U = measuredMax(d) + Between(d) + Residual(d)
L = measuredMin(d) − Between(d) − Residual(d)
```

- `measuredMax/Min(d)` are the chunk's own min and max height over its vertices, which `ChunkMeshJob`
  already returns in `HeightBounds`.
- `Between(d)` bounds how far the band-`d` function strays between vertices. It is a slope bound times
  half a cell diagonal. The slope bound is the sum over the octaves up to `MaxOctave(d)` of amplitude ×
  the noise's derivative bound ÷ wavelength, with the largest band gain (`MacroParams.MaxBandAmplitude`),
  plus the macro read's slope. The derivative bound of `LatticeNoise.Gradient3D` with its quintic fade is
  **measured, pinned and padded**, as `C` is; derive it analytically if that turns out to be easy.
- `Residual(d)` bounds what the depths below can add. It is the octaves above `MaxOctave(d)` at their
  largest band gain, as `TerrainHeightFunction.MaxAbsHeight` sums them, plus the macro base height's
  difference between `MacroLevel(d)` and the top level. The second term is bounded by the macro octaves
  the coarser level leaves out, times `MacroSampler.MaxOvershoot`, plus the two levels' interpolation
  difference. The interpolation part is the least obvious term; bound it, and test it (Task 5, test 4).

Then each child is:

- **Ocean** if `U < SeaLevel − OceanTerrainOverlap`;
- **Land** if `L > SeaLevel + MaxWaveAmplitude`;
- **Coast** otherwise.

An Ocean node's children are Ocean without a bound, because nothing was sampled and the parent's bound
covers them. `OceanTerrainOverlap` is the depth below the sea to which terrain must still exist. It is at
least `MaxWaveAmplitude`, plus whatever depth the shore shading needs (C9).

**What a wrong bound costs.** Too small, and a node is Ocean where its descendants have land above
water: an island goes missing under flat sea. Or a node is Land where its descendants have water: a lake
bed is drawn dry. Both are silent, which is why the bound is a bound and not an estimate, the same
standard `MaxAbsHeight` meets for `LodVertical`. Too large costs only more Coast chunks. If it ships
loose, measure how loose and record it.

**At a Coast–Ocean boundary** the terrain stops at the Coast chunk's edge, under water. The Ocean
chunk's bound places every point along that edge below `SeaLevel − OceanTerrainOverlap` at its depth and
finer. A coarser Coast neighbour evaluates a smoother function there, which can differ; its skirt hangs
from that edge as it does today. Nothing new opens above the sea.

**In the selector** (`ocean-c`), an Ocean node reports `MinHeight = MaxHeight = SeaLevel`. Its bounding
sphere is then flat, so ocean selects by geometry alone and splits less than rough land. The relief
warning and the roughest-chunk report skip it.

### C9 — Shading: opaque, hazed like the land, coloured by the floor near the shore

- **The base colour** comes from the water depth, `SeaLevel − floor height` (`TEXCOORD3.w`), from
  shallow to deep. Ocean chunks read as deep.
- **Normals** are the analytic wave normals near the camera, fading to the topoUp with the
  displacement (C6), plus a sun glint term whose roughness grows with distance, so the sea from orbit
  shows a highlight rather than nothing.
- **Under `_SONOMA_SKY`** the water takes the same hemisphere ambient and aerial haze as the terrain,
  through `SonomaCommon.hlsl` (C5), so coast and sea fade together into the distance.
- **Shoreline foam** and any depth-of-field or refraction effects are out of scope (Q6).

### C10 — The fly camera stays above the sea

The camera is assumed never to go under water. `TopologyFlyCamera` measures its clearance from
`max(HeightAt(p), SeaLevel)` and clamps there, so over the sea its speed and near plane follow the
water, not the sea bed. Otherwise, over a 4 km deep ocean, it would fly at the speed and near plane of
4 km up, while skimming the waves. The F3 readout keeps its meaning (height above the base surface)
and gains a line for height above the sea.

### C11 — Open edges are deferred, and plane grids have them too

A cylinder's rims, and the outer edges of a plane grid, would show the sea's edge as a sheet ending in
the air. Two ways to deal with that, both deferred:

- **A rim term in the macro base height** near `v = 0, 1` on a cylinder (and the outer tiles of a grid),
  raising the ground above `SeaLevel + MaxWaveAmplitude` before the edge. It is a pure function of
  `(quad, u, v)`, so C2 of M5 keeps the edges agreeing for free. It spends relief allowance, which the
  terrarium has least of.
- **Walls**, a separate piece of geometry with its own LOD.

Until then the stress scenes may show water edges at their rims. That is expected, not a defect.

---

## Task 1 — Sea level and the classification bound (`ocean-a`)

- `TerrainSettings`: `Ocean`, `SeaLevel`, `OceanTerrainOverlap`, and a `Material OceanMaterial` on
  `TerrainRoot` beside `ChunkMaterial`. Validation per C1.
- `Core/Generation/OceanBounds.cs` (pure, UnityEngine-free so the harness runs it):
  - `Between(in HeightParams, in MacroParams, int depth)`;
  - `Residual(in HeightParams, in MacroParams, int depth)`;
  - `Classify(min, max, depth, …) → NodeKind` (C8);
  - the measured noise-derivative constant, with a comment naming where it was measured and the padding.
- `NodeKind` (`Land`, `Coast`, `Ocean`) on `LodSelector.NodeState`. A child takes its kind from
  `Classify` when its parent's `OnChunkReady` arrives. A root is `Coast`.
- `GenerationScheduler` takes the kind with each request and sizes the mesh data per C4.

## Task 2 — Water geometry and the ocean shader (`ocean-a`)

- `ChunkMeshJob` writes the water vertices after the terrain ones for a Coast chunk (C3), and extends
  `VertexBounds` round them. The surface frame at sea level comes from `SurfaceMath`, never from
  interpolating corners.
- `ChunkMeshLayout`: the water vertex range, the per-kind index counts, the 16-bit limit with an ocean
  (C4), and the water skirt drop. It stays UnityEngine-free.
- `ChunkMeshBuffers` and `GenerationScheduler.Upload`: one or two submeshes per kind.
- `ChunkPool` / `TerrainChunk`: materials per kind.
- `Assets/Shaders/SonomaCommon.hlsl`, extracted from the terrain shader, which must compile to the same
  variants afterwards (C5).
- `Assets/Shaders/SonomaOcean.shader`: ForwardLit, DepthOnly, DepthNormals; the flat shading of C9;
  `_SONOMA_SKY` as a local `shader_feature`, as in the terrain shader.
- `Assets/Materials/Ocean.mat`.
- `TopologyFlyCamera`: C10.

## Task 3 — Waves (`ocean-b`)

- `Core/Generation/OceanWaves.cs` (pure): the component table, the lattice check that each wave vector
  is a multiple of 2⁻ᴾ (C7), `MaxWaveAmplitude`, and the distance fade's constants.
- `TerrainRoot.PushShaderGlobals`: `_SonomaWaveOrigin` and `_SonomaWavePhase[N]`, with the phases
  accumulated in double. `WorldOriginSystem.ShiftOrigin` re-pushes `_SonomaWaveOrigin`.
- `SonomaOcean.shader`: the displacement on the morphed position in all three passes, wave normals in
  ForwardLit, and colour by water depth (C9).
- The water skirt drop gains `2 × MaxWaveAmplitude` (C3).

## Task 4 — Pruning (`ocean-c`)

- `Core/Generation/WaterMeshJob.cs`: the water grid alone, from `SurfaceFrame` at sea level. No height
  sampling, no macro read.
- `GenerationScheduler`: an Ocean request schedules only `WaterMeshJob`.
- `LodSelector`: Ocean nodes report a flat relief (C8); the relief warning and the roughest-chunk report
  skip them.
- Re-measure each stress scene's worst-case working set with the replica (`LodSelectorTests.Earth
  ReplicaWantsWhatTheSelectorWants` and its siblings), now with Ocean nodes unpadded, and record the Coast
  share and the memory. The budgets should hold or fall; if one rises, it is a finding.

## Task 5 — Tests

### Pure (run outside the Editor)

1. **`SeaSurfaceIsTheBaseSurfaceOffset`** — water vertex positions are `SurfaceFrame` at `SeaLevel` on
   all three topologies. On a cylinder the radius is `R − SeaLevel`, inward (C1).
2. **`SeaLevelOutsideTheEnvelopeIsRefused`** and **`OceanCapsTheResolution`** — C1's validation,
   including R 179 accepted and 181 refused with an ocean, and 253 still accepted without one (C4).
3. **`OceanIndexLayout`** — per kind: counts, the water range offset by `R² + 4R`, and the same winding
   as the terrain's on every topology (`CLAUDE.md`, "handedness").
4. **`ClassificationBoundHolds`** — for each stress scene, random nodes at every depth. Compute `U` and `L`
   from the node's real vertex heights, then sample descendants down to `MaxDepth` at random points
   (vertices and between them) with the full band and macro level. No sample may exceed `U` or fall below
   `L`. Then drop each of the three terms in turn (`Between`, the detail residual, the macro residual)
   and confirm each drop fails, at some scene and depth. If a term can be dropped without failing, the
   sample is too small to justify it; enlarge it before concluding the term is unneeded.
5. **`ClassificationIsMonotone`** — an Ocean node's children are Ocean and a Land node's are Land:
   no descendant contradicts its parent's kind, over the same sample.
6. **`WavePhaseIsPeriodicInTheOrigin`** (`ocean-b`) — every shipped wave vector is on the 2⁻ᴾ lattice,
   and shifting a position by 2ᴾ along each axis leaves every phase unchanged, exactly in double. Then
   perturb one vector off the lattice and confirm it fails (C7).
7. **`WaterSkirtCoversTheTransientGap`** — `StressSceneTests.TransientEdges` over water: two flat
   patches up to `SkirtDepthGap` apart, each at morph 0, ½ and 1, with waves at their bound; the skirt
   covers every gap.

### Editor

8. **`ChildAtFullMorphEqualsParent`**, extended — the water submesh of a fully morphed Coast child is
   its parent's water, vertex for vertex and triangle for triangle (C3, C6).
9. **`ShaderMorphsInEveryPass`**, extended to `SonomaOcean.shader`: every pass it has reads `TEXCOORD1`,
   `TEXCOORD2` and `_SonomaLodVertical`, and the shared code comes from `SonomaCommon.hlsl` in both
   shaders (C5). In `ocean-b`, also that the displacement takes the morphed position and the factor the
   unmorphed one (C6).
10. **`ShadowCoordIsNotInterpolatedUnderCascades`** and **`ShadowKeywordsMatchThePipeline`**, extended to
    the ocean's ForwardLit.
11. **`StressSceneAssetTests`**, extended — each stress scene's `Ocean`, `SeaLevel` and ocean material
    match the tests' copies, and `SampleScene` has no ocean and one submesh per chunk (ground rule 1).
12. **`OceanNodesSampleNothing`** (`ocean-c`) — an Ocean node is never given a height sample job, and a
    selection over a Coast–Ocean boundary leaves no node uncovered.
13. **`WorkingSetTests` and the replica**, re-pinned (`ocean-c`), Task 4.
14. **`RebaseCarriesTheWaveOrigin`** (`ocean-b`) — `ShiftOrigin` moves `_SonomaWaveOrigin` with the
    view position, and the phase at a fixed absolute point is unchanged to the bit across a thousand
    shifts, like `RebaseIsExactAfterAThousandShifts`.

## Task 6 — Configuration

| asset | change |
|---|---|
| `EarthTerrainSettings`, `SmallPlanetTerrainSettings`, `RingworldTerrainSettings`, `TerrariumTerrainSettings` | `Ocean` on, `SeaLevel` (Q2), `OceanTerrainOverlap` |
| each stress scene's `TerrainRoot` | `OceanMaterial: Ocean.mat` |
| `TerrainSettings.asset` (`SampleScene`) | unchanged, apart from new default fields if re-saved |

Record per scene: the land fraction at the chosen `SeaLevel`, the Coast share of the peak working set,
and the vertex memory that adds.

`MacroMapExport` gains a coastline: the base height's and the full height's zero contours relative to
`SeaLevel`, on the existing hillshade. That is how Q2 gets answered with numbers.

## Task 7 — Documentation

- `CLAUDE.md`: the ocean under Core Architecture; the kinds and the bound; Non-Obvious entries for C3
  (water needs the morph too), C6 (displace the morphed position), C7 (the wave lattice), C8 (a bound,
  and what a loose one costs) and the halved 16-bit limit (C4); the vertex table's water meanings; test
  counts.
- Shader headers for both shaders and `SonomaCommon.hlsl`.
- `SonomaRevisedPlan.md`: a note recording where the ocean sits among the milestones (Q1).

---

## Verification

### Agent-side, without the Editor

Compile and run the out-of-Editor harness (`unity-verification-without-batchmode`). Every pure test must
pass: the existing ones unchanged, which is ground rule 2's evidence, plus tests 1–7. Keep
`OceanBounds`, `OceanWaves` and the layout additions free of `UnityEngine`, `Unity.Collections`,
`Unity.Jobs` and `Unity.Burst`.

### Human-side, in the Editor

1. Console clean; Test Runner all green; the Burst witness still reports compiled.
2. **`SampleScene` unchanged:** it looks the same, and its relief and budget reports match M5's.
3. `EarthStress`, flat (`ocean-a`):
   - oceans visible from orbit with a glint;
   - the coastline stable through a descent: it moves smoothly as LOD changes and never pops or z-fights;
   - no cracks in the water along cube edges or at LOD boundaries;
   - over the sea the fly camera's speed and near plane follow the water.
4. Waves (`ocean-b`):
   - waves near the camera that fade smoothly with distance, with no visible ring;
   - no jump at a rebase (watch the waves while the log reports one);
   - no gaps in the water during a fast descent;
   - the shore shading follows the floor.
5. Pruning (`ocean-c`):
   - fly low over deep ocean and along a coast: no missing islands and no dry lake beds;
   - the peak working set at exit, against the budget and against `ocean-b`'s.
6. Repeat 3–5 in `SmallPlanetStress`, `RingworldStress` and `TerrariumStress`. On the cylinders, the water
   inside the curve and at the open rims (C11, expected).
7. Profiler, a development build, exported with Tools → Sonoma → Export Profiler Capture: main-thread
   `TerrainRoot.Update` must not rise (ground rule 3), and worker time per Ocean chunk should be a small
   fraction of a terrain chunk's.

---

## Definition of done

- [ ] `SampleScene` renders and reports exactly as at M5; every scene's heights are bit-identical.
- [ ] Every stress scene has a sea at its `SeaLevel`, from orbit to the ground, on every topology (C1).
- [ ] The water is a payload on the terrain's nodes, morphed with them; a fully morphed child's water is its parent's (C2, C3, C6).
- [ ] Waves animate close up, fade by distance, and survive a rebase exactly (C6, C7).
- [ ] Node kinds come from a bound that test 4 shows is one, with each term earning its place (C8).
- [ ] Ocean nodes sample no terrain, and the working sets are re-measured and within budget (Task 4).
- [ ] The fly camera stays above the sea (C10).
- [ ] All tests pass; the Coast share, its memory and the per-chunk job costs are recorded.
- [ ] `CLAUDE.md`, the shader headers and the revised plan are updated; the amendments section is written.

---

## Open questions

These need the user's decision, or a measurement, before the task that depends on them.

- **Q1 — Before or after M6?** `ocean-c` changes the selector and the scheduler, the same code M6's
  profiling fixes rewrite (bounding-sphere caching, hash traffic, allocation-free uploads). `ocean-a` and
  `ocean-b` barely touch the selector. One option is to do `ocean-a` and `ocean-b` now and `ocean-c` after
  M6; another is to do M6 first. Which?
- **Q2 — Sea level and land fraction per scene.** With `SeaLevel` 0 and a zero-centred macro base height,
  roughly half of each world may be under water; this has not been measured. Is there a target land
  fraction (Earth-like 30%?), and is it reached by moving `SeaLevel` or by biasing the macro base height?
  The coastline export (Task 6) answers this with numbers.
- **Q3 — Wave model.** Gerstner sums with world-space directions on the 2⁻ᴾ lattice (C7) are the
  proposal. Their poles are harmless on a sphere with several directions; a cylinder's up rotates about
  one axis, so directions along the axis never pole and directions across it pole twice per turn. The
  alternative is tiling normal or displacement textures, triplanar on the same lattice. That suits
  detail better, but it waits on textures generally (M7). Which, and what amplitude and wavelengths? The
  skirts and the overlap scale with the largest amplitude.
- **Q4 — How deep do Ocean nodes subdivide?** Waves need a vertex spacing of perhaps a quarter of the
  shortest displaced wavelength, likely coarser than land's `MaxDepth` (0.62 m on Earth). An
  `OceanMaxDepth` would save chunks over the sea, at the cost of a second depth limit in the selector.
  Worth it?
- **Q5 — Coast chunk memory.** Allocating the water vertices on every Coast chunk doubles their vertex
  memory, even where the sampled terrain turns out wholly dry. An alternative is a separate water mesh
  allocated only after the terrain job reports its minimum, at a frame of latency. Accept the doubling
  until it is measured (Task 6)?
- **Q6 — Shore effects.** Colour by depth is in (C9). Foam lines, wave damping in shallow water, and a
  morph target for the floor height (to remove the shallow-colour pop at a swap) are each small. Now, or
  with M7's content?
- **Q7 — Climate and the sea.** `MacroGenerator`'s lapse rate cools from base height 0
  (`math.max(baseHeight, 0.0)`), which assumes the sea is there. Should it measure from `SeaLevel`? That
  changes the macro maps' bits in any scene with a nonzero sea level, and the biome pins with them. And
  does the biome table want a shore or beach biome, which climate alone cannot place?
- **Q8 — Open edges.** Macro rim or walls (C11), and when.
- **Q9 — `NarrowRingStress`.** It was added after this plan's tasks were drafted. Should it get an ocean
  along with the other four stress scenes? If so, it joins Task 6's table and every per-scene test.
- **Q10 — Before or after M5c?** Erosion (the M5c amendment in `Docs/M5_MacroLayer_Plan.md`) rewrites the
  macro base height and adds a local gully term to the height function. Both move coastlines, and the
  gully term changes what C8's `Residual(d)` has to bound. Building the ocean first means re-deriving
  the bound's terms and re-running test 4 after M5c; building it second means sea level and land fraction
  (Q2) are tuned once, on the eroded terrain. Rivers would also want to end at the sea.

---

## Amendments from implementing the ocean

The plan above is the plan as written. Building it will change some of it; record those changes here
rather than editing them into the tasks, so the plan still reads as what was decided up front.

---

## Out of scope

- **Transparency, refraction and underwater views.** The ocean is opaque and the camera stays above
  it. `OceanTerrainOverlap` is the lever a transparent sea would turn up later.
- **Rivers, lakes above sea level, and tides.** A lake is water at another level; the kinds and the bound
  generalise to it, but the levels would have to come from the macro maps, probably from M5c's drainage.
- **Water collision and buoyancy.** M6's colliders are the terrain's; a flat sea needs no mesh collider.
- **The open edges** (C11).
- **A second, far camera** for the ring's arch (`CLAUDE.md`, "Inside a cylinder").
