# Ocean-b — Waves on the Sea: Implementation Plan

**Audience:** a coding agent working in `G:\UnityProjects\TerrainGen` on the `ocean-b` branch, with the
parent plan to hand.
**Parent plan:** `Docs/Ocean_Plan.md`. Its conventions C1–C11 and questions Q1–Q10 are referred to by
number and not repeated. `Docs/Ocean_A_Plan.md` is the stage before this one; its decisions A1–A9 and its
"What `ocean-a` hands on" stand. This document turns the parent's Task 3, and the parts of Tasks 5–7 that
are `ocean-b`'s (tests 6, 7 with waves, 9's displacement half, 14), into ordered steps against the code
as it stands after `ocean-a` and the continent designer (main at c00b390).
**Prerequisite:** main at c00b390 or later, all 257 EditMode tests green. `Assets/Materials/TerrainBiomes.mat`,
`Assets/Settings/SandboxContinents.asset` and `Assets/ForestTiles/` have uncommitted changes that are the
user's; leave them alone, as with `ProjectSettings/` and the URP assets.

`ocean-b` puts waves on the sea `ocean-a` drew: a vertex displacement close up that fades out where the
LOD stops resolving it, wave normals in the shading further out than the geometry, and a glint that
broadens with what has faded. It also morphs the floor height, which removes `ocean-a`'s depth-colour pop
at a swap and lets the waves shoal out in shallow water (Q6, in part). Selection, the height function and
every terrain vertex stay as they are.

---

## What changed since the parent plan was written

1. **The kinds are exact, and already take a wave amplitude.** `ChunkKinds.Classify(lo, hi, sea, overlap,
   wave)` calls a chunk Land only if `lo > sea + wave` (A1). `OceanParams.WaveAmplitude` is 0 today. So
   C8's bound is not this stage's: setting the amplitude moves the kinds exactly, with no bound to derive.
2. **The water skirt has its own rule.** `ChunkMeshLayout.WaterSkirtDrop` and `WaterChordSag`, the chord
   sag whole whenever skirts are on (PR #20's review, item 3). C3's `2 × MaxWaveAmplitude` is one more
   term in it.
3. **Culling bounds are the box round what the job drew** (`VertexBounds`), not a padded cube. A displaced
   wave lies outside that box by up to its amplitude, so the box has to grow by the waves' bound. The
   parent plan did not say so, because it predates the box.
4. **The fly camera has a floor, a metre above the sea** (`TopologyFlyCamera.SeaClearance`,
   `LiftAboveSea`). With waves a metre is inside a crest.
5. **The water vertex has eight spare bytes,** `COLOR` and `TEXCOORD0`, written zero. `ocean-a` named them
   for the morphed floor.
6. **The camera position the shader has is flattened.** `_SonomaViewPosition` is the camera as the LOD
   sees it (`LodVertical`), not the true camera. C6's "fade with distance from `_SonomaViewPosition`" would
   fade by a distance that is neither the true one nor what decided the vertex spacing. B2 replaces it.

---

## Decisions to confirm before building

Each has a recommendation, which the steps assume. Where one is answered the other way, the steps it
touches are named.

### B1 — The phase is kept exact by per-wave offsets in double, not by C7's lattice

**Recommended.** C7 makes the phase exact through a rebase by putting every wave vector on a 2⁻¹⁶
cycles-per-metre lattice and pushing the origin modulo 2¹⁶ m. It works, but the shader then takes the
phase of `positionWS + _SonomaWaveOrigin`, a float up to about 65 km, good to 8 mm. The same exactness
comes more cheaply by splitting the phase where the precision is:

```
phase_i(X, t) = k_i · X − ω_i t                       (cycles; X absolute, double)
             = k_i · p  +  frac(k_i · O − ω_i t)       (p = X − O, render space, float)
```

The second term is one number per wave, computed in double on the CPU each frame and pushed as
`_SonomaWavePhase[N]`. The shader adds `dot(k_i, p)` with `p` the vertex's render-space position, which
is metres from the camera where waves are visible, so the float error is `ULP(k · p)`: about 1e-4 of a
cycle at 3 km for a 2 m wave, against 4e-3 for the lattice. Wave directions are free, with no snapping.

A rebase moves `O` by `s` and every `p` by `−s`, so the offset must move by `k_i · s` in the same frame:
`WorldOriginSystem.ShiftOrigin` raises a static `OriginShifted` event and `TerrainRoot` re-pushes the
phases from the new origin and the same frame time. (It cannot add to the pushed floats, as it does for
`_SonomaViewPosition`, because the offset needs double.) Time is `Time.timeAsDouble`, multiplied out each
frame rather than accumulated, so nothing drifts over a session.

`_SonomaWaveOrigin` and the lattice check are then unneeded. The parent's test 6 becomes "the phase at a
fixed absolute point survives a thousand rebases to within float rounding of `k · p`", with the
mutations "the offset not re-pushed on a shift" and "the offset computed from the float origin".

**The alternative** is C7 as written. If chosen, Step 1 gains the lattice check and snaps directions,
and Step 3 pushes `_SonomaWaveOrigin` instead of offsets.

### B2 — Each wave fades by the effective depth `depth − k`, not by distance

**Recommended.** A displacement must agree at every point two chunks share, or it cracks the water. C6
gets that from a fade by distance. But what decides whether a wave is resolved is the vertex spacing,
and the spacing at a given distance is not fixed: the atomic swap holds a parent while its children
build, the budget collapses groups, and in a fast low flight the tree runs well short of its depth
(`ocean-a` amendment 10: 3,264 chunks against 7,202 on Earth, for 22 s). A distance fade tuned for the
settled tree aliases all through those states.

`CLAUDE.md` already names the number both sides agree on: "The one number that says whether the
terrain cracks is `depth − k`". A chunk at depth `d` and morph `k` draws depth `d − k`'s geometry, and two
chunks meeting at a point agree on it. So each wave's amplitude is a function of the spacing at that
effective depth:

```
spacing(e) = NominalSize(0) × 2^(−e) / (R − 1)
fade_i     = smoothstep(4, 8, λ_i / spacing(e))      0 below 4 vertices a wavelength, 1 from 8
```

That is continuous in `e`, so within a depth it is a function of distance (through `k`), and across a
boundary the two sides agree because they agree on `e`. A fully morphed child (`e = d − 1`) displaces as
its parent does at `k = 0`. It is correct in every transient state, since it reads the depth actually
drawn, and it needs no reasoning about `LodVertical`. The shader takes `NominalSize(0) / (R − 1)` as one
global. Waves shorter than four vertex spacings at `MaxDepth` never displace; they live in the normals
(B3).

**The alternative** is C6's distance fade, with its constants from `LodMath`'s split distances and the
transients accepted as aliasing.

### B3 — Normals carry the waves further than the geometry, and the glint takes up what has faded

**Recommended.** Geometry needs four vertices a wavelength. Shading needs only a couple of pixels, which
at the near field is far further out. So in ForwardLit:

- **Geometric components** displace the vertex (B2) and contribute their analytic normal with the same
  fade, so the lit surface matches the drawn one.
- **Every component** also contributes to the per-fragment normal with a second fade, by the pixel
  footprint: `smoothstep(2, 4, λ_i / footprint)`, `footprint` from `fwidth` of the render-space position.
  This needs no agreement between chunks; it is shading.
- **The glint** broadens by what has faded. The slope variance a component no longer draws,
  `(1 − fade) × (2π a_i / λ_i)² / 2`, summed, widens the Blinn-Phong lobe (a Toksvig-style roughness),
  added to the existing log-distance smoothness. The sea from altitude then glints as wide as its waves
  are rough, not as the material guesses.

Evaluated per fragment, at the undisplaced morphed position, carried as a varying. With eight
components that is eight `sincos` a fragment.

**The alternative** is geometry only (normals from the displaced components), simpler and flat beyond a
few hundred metres.

### B4 — Gerstner waves from a few parameters, in one shared asset

**Recommended** (Q3). A sum of `N` Gerstner waves, `N` = 8 to start:

- **Vertical** `a_i cos(2π phase_i)` along the topoUp, and **horizontal** `Q a_i sin(2π phase_i)` along
  `k_t`, the wave vector projected onto the tangent plane, unnormalised so it vanishes smoothly where the
  wave's direction is along the up (C7's "poles"). No loops while `Σ Q a_i |k_i| 2π ≤ 1`, checked.
- **Generated, not hand-entered.** A new ScriptableObject, `OceanWaveSettings`, holds `Count`, the shortest
  and longest wavelength (geometric between), `Steepness` (each `a_i = Steepness × λ_i / 2π`, scaled down
  for the longer waves by a spectrum exponent), `Choppiness` Q, a wind direction as a world vector and a
  spread angle, and a seed. `OceanWaves.Build` (pure) turns it into the table: `k_i` from the direction
  and wavelength, `ω_i = √(2π g / λ_i)` (deep water), `a_i`, and `MaxWaveAmplitude = Σ a_i`.
- **A world direction poles twice on a sphere and twice a turn inside a cylinder.** With the spread,
  other components carry the surface there. The default wind lies along the cylinder's axis, the one
  direction that never poles inside one, and is a world direction on the spheres.
- **Starting values,** to be tuned by eye: 8 waves from 1.5 m to 80 m, steepness 0.06, Q 0.6, spread 50°.
  That is about 1.6 m of `MaxWaveAmplitude`. The skirts (C3), the overlap (C8), the bounds and the
  camera's floor all scale with it.
- **One asset, `Assets/Settings/OceanWaves.asset`, named by each stress scene's `TerrainSettings.Waves`.**
  Null means a flat sea, which is `ocean-a` to the bit. The terrarium may want a calmer one later; the
  field is per scene so that costs nothing now.

**The alternative** is tiled normal or displacement textures (Q3's other option). It suits fine detail,
but waits on M7's textures and the triplanar lattice (M4 C4).

### B5 — The floor morphs, in the spare bytes, and shallow water damps the waves

**Recommended** (Q6, in part). The water vertex's floor height (`TEXCOORD3.w`) is the fine one, so the
depth colour pops at every swap, and a shallow-water damping by it would differ between a fully morphed
child and its parent: a crack. Both need the floor to morph.

- `COLOR` carries the fine floor's depth below the sea, and `TEXCOORD0` the coarse one's, at the morph
  target, each as `1 − exp(−depth / FloorScale)` in 16 bits over two UNorm8 channels. `FloorScale` is a
  constant on `ChunkMeshLayout`, 64 m. The coarse floor at the morph target is the parent's fine floor
  there to the bit (the geomorph invariant), and the encoding is a fixed function, so the parent's byte
  and the child's morph-target byte are equal.
- The shader lerps the decoded depths on the same `k`. Colour by depth reads the morphed depth.
- Each wave's amplitude is multiplied by `saturate(depth / (ShoalFactor × λ_i))`, `ShoalFactor` 0.25 in
  the asset, so long waves flatten first towards the shore. A wave at the coast of zero depth has zero
  amplitude, so the shoreline does not swim. Breaking and foam are M7's (Q6, the rest).
- `TEXCOORD3.w` keeps the fine floor as a float: the classification test reads it.

**The alternative** is no damping and the colour pop kept: `COLOR` and `TEXCOORD0` stay zero.

### B6 — What scales with the amplitude

**Recommended,** and each follows from `MaxWaveAmplitude`:

- **`OceanParams.WaveAmplitude`** is it, and `Classify` already uses it, so chunks a wave can reach become
  Coast rather than Land.
- **`OceanTerrainOverlap`** rises from 0 to at least it in every stress asset, and `OceanParams.Create`
  refuses less. An Ocean chunk draws no terrain, so without the overlap a shoal just under the sea would
  never break the surface in a trough.
- **The water skirt** gains `2 × MaxWaveAmplitude` (C3): two edges a transient state apart can be displaced
  in opposite directions.
- **The water's culling box** grows by `MaxWaveAmplitude` along every axis, vertical and Gerstner
  horizontal together. The job pads only the water's vertices, so Land chunks' bounds are unchanged.
- **The fly camera's floor** is `SeaLevel + MaxWaveAmplitude + SeaClearance`, so it never sits inside a
  crest. Its ground for speed and clip planes stays the sea level.

---

## Step 0 — Baselines (no commit)

As `ocean-a`'s step 0, with its driver rebuilt (it was deleted after Step 7):

1. The descent and coast-flight figures of `ocean-a` amendment 10 are the "before": peak working sets,
   Land / Coast / Ocean at the peak, vertex memory, mesh job time a chunk, upload a chunk, fps.
2. **Reference frames at a frozen phase.** Waves move, so frames are comparable only at a fixed time. Add
   `TerrainRoot.WaveTimeOverride` (a nullable double, tests and drivers only) first, then capture each
   stress scene's start pose with waves off. Step 2 must leave those frames identical apart from the
   colour by depth near coasts (the floor morph changes it), and `SampleScene` byte-identical.

## Step 1 — The wave table, pure, and the settings (commit)

1. `Core/Generation/OceanWaves.cs`, UnityEngine-free: the component struct (`double3 K` in cycles a metre,
   `double Omega`, `double Amplitude`), `Build(...)` from the settings' values (B4), `MaxAmplitude`, the
   no-loop check, `PhaseOffset(in wave, double3 origin, double time)` returning `frac(k · O − ωt / 2π)`
   (B1), and `Fade(lambda, spacing)` (B2) as the shader will compute it, so the tests price it.
2. `Systems/Configuration/OceanWaveSettings.cs` (the ScriptableObject, tooltips in the house style) and
   `TerrainSettings.Waves`. `Assets/Settings/OceanWaves.asset` at B4's values.
3. `OceanParams.Create` takes the amplitude, sets `WaveAmplitude`, and refuses an overlap below it with an
   ocean on (B6). `ChunkMeshLayout.WaterSkirtDrop` takes the amplitude and adds twice it.
4. Pure tests, in a new `OceanWaveTests.cs`:
   - `WaveTableIsDeterministicAndBounded`: the same settings give the same bits; `MaxAmplitude` is the sum;
     the no-loop bound holds, and a choppiness past it is refused.
   - `WavePhaseSurvivesRebases` (parent test 6, by B1): random absolute points on Earth's sphere and a
     thousand random shifts; the phase as the shader computes it (`k · p` in float, plus the double
     offset narrowed) stays within 2e-4 cycles of the true double phase. Mutations: the offset not moved
     on a shift fails at the first; the offset from the float origin fails at Earth radius.
   - `WaveFadeAgreesAcrossDepths` (B2): `Fade` is continuous in `e`; a fully morphed child (`d`, `k = 1`) and
     its parent (`d − 1`, `k = 0`) get the same fade to the bit; no component displaces at fewer than four
     vertices a wavelength at any `e`. Mutation: a fade by `d` alone jumps at a boundary.
   - `WaterSkirtCoversTheTransientGap` (parent test 7, with waves): `OceanLayoutTests`'
     `WaterSkirtCoversTheChordSag` with each side displaced by ±`MaxAmplitude`, opposite signs; dropping
     the wave term fails it somewhere.
   - `OceanParams` refuses an overlap under the amplitude.

The stress assets stay wave-free in this commit (`Waves` null), so nothing moves yet.

## Step 2 — The morphed floor and the padded bounds (commit)

1. `ChunkMeshJob.WriteWater`: `Splat` and `MorphSplat` carry the fine and coarse floor encodings (B5),
   by a pure `ChunkMeshLayout.EncodeFloor(sea, height)`. The job pads `VertexBounds` round the water's
   vertices by `Ocean.WaveAmplitude` (B6); the terrain's vertices are boxed as now.
2. `SonomaOcean.shader`: decode and morph the floor on `k`; colour by the morphed depth.
3. Tests:
   - `ChildAtFullMorphEqualsParent`, extended: on the forced Coast chunks, each child water vertex's
     morph-target floor bytes equal the parent's fine floor bytes at that vertex.
   - `EncodeFloorIsMonotoneAndExactAtTheSea` (pure): zero depth encodes to 0, above the sea too; monotone.
   - `JobPadsTheWaterBoundsByTheWaves` (Editor): with an amplitude, every water vertex displaced by it in
     any direction is inside the box; a Land chunk's box is unchanged.
   - `OceanOffBuildsTodaysMesh` must still pass untouched: no ocean, no water vertex.
4. Recapture step 0's frames: identical except the near-coast colour; `SampleScene` byte-identical.

## Step 3 — Phases on the CPU, and the rebase (commit)

1. `TerrainRoot.PushShaderGlobals`, with waves: `_SonomaWaves[N]` (`float4(k.xyz, amplitude)`),
   `_SonomaWaveExtra[N]` (`float4(λ, Q, shoal depth, 0)`), `_SonomaWavePhase[N]` (B1),
   `_SonomaWaveCount` and `_SonomaWaveLod = (NominalSize(0) / (R − 1), 0, 0, 0)` (B2). The constant arrays
   are pushed every frame like `_SonomaMorphRanges`, so one scene cannot leak into the next; a scene with
   no waves pushes a count of 0. `N` is at most 16 (`SONOMA_MAX_WAVES`), checked in `OceanWaves.Build`.
2. `WorldOriginSystem.ShiftOrigin` raises `OriginShifted` after moving the origin; `TerrainRoot`
   subscribes in `Start` and unsubscribes in `OnDestroy`, and re-pushes `_SonomaWavePhase` from the new
   origin and the frame's time.
3. Test `RebaseCarriesTheWavePhase` (parent test 14, Editor): a `TerrainRoot` with waves, a thousand
   `ShiftOrigin`s; the pushed offsets at each equal `OceanWaves.PhaseOffset` at the new origin to the bit,
   and the phase at a fixed absolute point, computed from the pushed globals, stays within the pure
   test's tolerance. Unsubscribing fails it.

## Step 4 — The displacement and the shading (commit)

1. `Assets/Shaders/SonomaWaves.hlsl`, included by the ocean shader only, after `SonomaCommon.hlsl`:
   the globals, `SonomaWaveDisplace(inout positionOS, topoUp, effectiveDepth, floorDepth)` returning the
   undisplaced render-space position for the fragment, and `SonomaWaveNormal(position, topoUp, e,
   footprint, floorDepth, out slopeVariance)`. Header: why the displacement takes the morphed position
   and the fade the effective depth (C6, B2), and why the phase is split (B1).
2. `SonomaCommon.hlsl`: `SonomaMorphPosition` returns its `k`, as `SonomaMorph` does, so the depth
   prepass can form `e` without recomputing it. The terrain shader ignores the return; its pixels must not
   change (step 0's frames, waves off).
3. `SonomaOcean.shader`, all three passes: morph, then displace, with `e = depth − k`. ForwardLit's
   normal is B3's, and the glint's lobe takes the slope variance. DepthNormals writes the geometric wave
   normal (the components that displace), so screen-space effects see the drawn surface.
4. Tests:
   - `ShaderMorphsInEveryPass`, extended (parent test 9): every ocean pass calls `SonomaWaveDisplace`
     after the morph and passes it the morphed position and `depth − k`; the terrain shader includes no
     `SonomaWaves.hlsl`.
   - `ShadowCoordIsNotInterpolatedUnderCascades` and the keyword tests still pass over the ocean.
   - `ShaderUtil.CompilePass` over every pass, sky on and off, under each shadow keyword set: no messages.

## Step 5 — Scenes, fly camera, measure (commit)

1. The five stress assets: `Waves` → `OceanWaves.asset`, `OceanTerrainOverlap` → the amplitude rounded
   up. `StressSceneAssetTests.StressScenesHaveTheirOcean` holds both, and `SampleScene` to none.
2. `TopologyFlyCamera`: the floor of B6, through `LiftAboveSea`; `FlyCameraIsLiftedAboveTheSea` asks for
   it above the crest.
3. In the Editor, each stress scene, `ocean-a`'s descent and coast flight with the same timing:
   - console clean, tests green, Burst witness compiled;
   - the Coast share at the peak and its vertex memory against `ocean-a`'s: more chunks within
     `MaxWaveAmplitude` of the sea become Coast, so a small rise is expected;
   - mesh job and upload time a chunk against `ocean-a`'s (the floor encode is the job's only addition);
   - fps against `ocean-a`'s: the fragment cost of B3 is the one new per-pixel cost;
   - frames at a frozen phase, near, 3 km and 100 km, for the record.
4. Tune `OceanWaves.asset` and `Ocean.mat` by eye.

## Step 6 — Documentation and PR

1. `CLAUDE.md`: Current Status and Milestones (`ocean-b` done); the Ocean subsection (waves, the morphed
   floor); the water vertex's new meanings of `COLOR` and `TEXCOORD0`; Non-Obvious entries for B1 (the
   split phase and why the offset moves on a rebase), B2 (fade by `depth − k`), B5 (why the floor had to
   morph before it could damp); test counts; Code Organization.
2. Shader headers: the ocean shader's (the floor is no longer said not to morph) and `SonomaWaves.hlsl`.
3. `Docs/Ocean_Plan.md` amendments: B1–B6 as answered, and where they change C6, C7 and Q3/Q6.
4. Push `ocean-b` and open the PR against main, listing the measurements and what is unverified.

---

## Tests, at a glance

| test | where | step | parent test |
|---|---|---|---|
| `WaveTableIsDeterministicAndBounded` | pure | 1 | — |
| `WavePhaseSurvivesRebases` | pure | 1 | 6 (by B1) |
| `WaveFadeAgreesAcrossDepths` | pure | 1 | — |
| `WaterSkirtCoversTheTransientGap` with waves | pure | 1 | 7 |
| overlap under the amplitude refused | pure | 1 | — |
| `EncodeFloorIsMonotoneAndExactAtTheSea` | pure | 2 | — |
| `ChildAtFullMorphEqualsParent`, the floor | Editor | 2 | 8 |
| `JobPadsTheWaterBoundsByTheWaves` | Editor | 2 | — |
| `RebaseCarriesTheWavePhase` | Editor | 3 | 14 |
| `ShaderMorphsInEveryPass`, the displacement | Editor | 4 | 9 |
| `StressScenesHaveTheirOcean`, `FlyCameraIsLiftedAboveTheSea`, extended | Editor | 5 | 11 |

The pure files stay free of `UnityEngine`, `Unity.Collections`, `Unity.Jobs` and `Unity.Burst`, and every
existing pure test passes unchanged: nothing in the height function or the terrain's vertices moves.

---

## Verification

**Agent-side.** The out-of-Editor harness, then the full suite in the Editor over unity-mcp.

**Human-side, in the Editor,** each stress scene:

- waves near the camera that fade smoothly with distance, with no ring where the geometry hands over to
  the normals, and none where a node splits;
- no jump at a rebase: watch the waves while the log reports one (low and fast over the sea rebases
  often);
- no gaps in the water during a fast descent, nor along a coast at a swap;
- the shore: shallow water calm and lighter, no colour pop at a swap, no waves riding up a beach;
- from altitude, a glint as wide as the sea is rough;
- the camera skimming the waves never goes inside a crest;
- `SampleScene` looks and reports as before.

---

## Definition of done

- [ ] B1–B6 answered, and the answers in the parent plan's amendments.
- [ ] `SampleScene` and every terrain vertex unchanged; with `Waves` null a scene draws `ocean-a`'s sea.
- [ ] Waves animate close up, fade by effective depth, and survive a rebase within float rounding of
      `k · p`.
- [ ] A fully morphed child's water, floor included, is its parent's.
- [ ] Skirts, overlap, culling bounds and the camera's floor all cover `MaxWaveAmplitude`.
- [ ] The Coast share, vertex memory, job, upload and frame rate recorded against `ocean-a`'s.
- [ ] All tests pass; `CLAUDE.md`, the shader headers and the amendments are written; the PR is open.

---

## What `ocean-b` hands on

- **To M6:** the fragment cost of B3's normals, measured in Step 5, if it shows in the frame.
- **To `ocean-c`:** an Ocean prediction for unsampled nodes must use `SeaLevel − OceanTerrainOverlap`,
  which now includes the wave amplitude. An unsampled Ocean node has no floor, so its water vertices
  encode the deepest floor, and its waves never damp.
- **To M7:** foam, breaking, and wave textures on the triplanar lattice.

---

## Risks

- **The fade by `depth − k` reads as a band.** A wave fades over one effective depth, from eight vertices
  a wavelength to four, which on screen is a distance range a factor of two wide. If a band shows, widen
  the smoothstep to two depths; it costs resolution, not agreement.
- **Fragment cost.** Eight `sincos` a fragment over a sea filling the screen. If it shows in Step 5,
  fade out the shortest components by footprint earlier, or drop to six.
- **Gerstner horizontal motion near the poles of a direction** shrinks to zero with `k_t`, so the surface
  only bobs there. With the spread that should not show; if it does, raise the spread.
- **`OriginShifted` ordering.** `ShiftOrigin` runs in `LateUpdate`; the re-push must land before
  rendering in the same frame, which an event raised inside `ShiftOrigin` does. A subscriber that
  deferred it to its own `LateUpdate` would render one frame of phase jump per rebase: the test drives
  `ShiftOrigin` and reads the globals immediately.

---

## Amendments from implementing `ocean-b`

Record here what building it changed: the answers to B1–B6, the baselines, the measurements, and
anything the Editor said that this plan did not.

1. **B1–B6 answered (2026-11-16): all as recommended.** Phases split into a double offset per wave,
   re-pushed on a rebase; each wave fades by `depth − k`; wave normals beyond the geometry and a glint
   widened by what has faded; Gerstner waves from `OceanWaves.asset`; the floor morphs in `COLOR` and
   `TEXCOORD0` and damps the waves in shallow water; the kinds, overlap, skirts, bounds and camera floor
   take the amplitude.
2. **Step 0's descent baseline moves to Step 5.** `Waves` null draws `ocean-a`'s sea, and the narrow ring
   was retuned to 48% land after `ocean-a` measured it, so `ocean-a` amendment 10's figures are stale for
   it. Step 5 flies each scene twice from one driver, waves off and on, so the "before" is measured on the
   same code, terrain and machine as the "after". Step 0 keeps the reference frames, which Steps 2 and 4
   need. `WaveTimeOverride` comes with the phases in Step 3, since there is no wave time before it.
3. **Step 0's reference frames (2026-11-16), on `ocean-b` at c00b390, main plus this plan.** A temporary
   Editor script, `Assets/Editor/OceanBFrameDriver.cs` (untracked, deleted after Step 4), opens each
   scene, plays it, waits for no job in flight and no change in the resident or visible chunks for 90
   player frames, and renders the active camera offscreen at 1920 × 1080 through
   `RenderPipeline.SubmitRenderRequest` to `Logs/OceanB/ref_<scene>_<label>.png` (gitignored).
   - **Play mode stops when the Editor is not in front.** The first pass ran with the Claude app focused:
     `EditorApplication.update` kept ticking, but the player loop stayed at frame 1 with no chunk, and
     every frame was empty. The driver now sets `Application.runInBackground` at runtime (the Player
     settings are untouched), counts player frames rather than Editor ticks, refuses to write a frame of
     no chunks, and gives up with a message if the player loop has not moved in 15 s. With that the
     Editor need not be in front.
   - **The frames reproduce.** Two passes, `start` and `repeat`, are byte-identical in all six scenes.
     Settled in 3.0–9.6 s, at 30 resident (`SampleScene`), Earth 1,218, small planet 1,254, ring 1,288,
     narrow ring 900 and terrarium 2,240: `ocean-a`'s start-pose counts, except the narrow ring's 884,
     which its retune to 48% land moved. The console is clean.
4. **Step 1 (2026-11-17): the wave table, its settings and its bounds, unused by any scene.**
   `OceanWaves` (pure), `OceanWaveConfig`, `OceanWaveSettings` and `TerrainSettings.Waves`, and
   `Assets/Settings/OceanWaves.asset` at the settings' defaults. No stress asset names it yet, so every
   scene builds `ocean-a`'s sea; `TerrainRoot` builds the table only for its bounds and logs it.
   - **The default sea is 1.26 m high, not B4's estimate of 1.6.** The spectrum exponent of 0.1 takes the
     longest wave's steepness to 0.04. The no-loop sum is 0.24. Its largest displacement along any axis
     equals its amplitude, since a choppiness under 1 moves a vertex sideways by less than it lifts it.
     `OceanParams` therefore carries two bounds, `WaveAmplitude` for the kinds, the overlap and the skirt,
     and `WaveDisplacement` for the culling box. `OceanParams.Create` takes both as optional trailing
     arguments and refuses an overlap under the amplitude and a displacement under it.
   - **K is a float's value.** `Build` rounds each wave vector to float, so the shader's K and the
     offset's are the same number, and the phase at a fixed point moves across a rebase only by the
     float rounding of `K · p`.
   - **The phase split, measured.** Over 1,000 rebases at Earth radius, every point within 3 km of a
     wandering camera, the shader's phase stayed within 1.37e-4 cycles of the true one; the test asks for
     1e-3. An offset left at the old origin is off by up to half a cycle. One computed from the origin
     narrowed to float is off by 0.17 cycles where the waves run along a large coordinate. The test first
     put the camera on the x axis, where the waves, running mostly along z, hid that error at 0.034.
   - **`WaterSkirtCoversTheChordSag` became `WaterSkirtCoversTheTransientGap`.** It checks each edge
     flat and at 2 m of waves, and requires the drop without its wave term to leave some edge open in
     every scene, so the term is tested. `OverlapMustCoverTheWaves` is the other new `OceanLayoutTests`
     case.
   - **The skirt has a sideways gap the plan missed; Steps 2 and 4 close it.** Gerstner waves move the
     water sideways. Two edges a transient state apart are displaced differently, because the coarse side
     lacks the short waves (B2) and interpolates the rest linearly, so they can part sideways by up to
     the choppiness times the amplitude. A skirt hangs straight down from its own edge and moves sideways
     with it, so it does not fill that strip. The fix: the water's skirt vertices are flagged in a spare
     byte (`COLOR.b`, beside B5's floor encodings) and the shader does not displace them at all. Each
     skirt then slants from its displaced edge back to the undisplaced line, which the two sides share up
     to the vertical gap the drop already covers. With undisplaced bottoms the drop needs only one
     amplitude, but it keeps C3's two, as margin.
   - **The suite runs from a menu item.** The unity-mcp bridge now refuses any command that calls
     `TestRunnerApi` ("user interactions are not supported"), even deferred. A temporary, untracked
     Editor script, `OceanBTestRun`, runs it from Tools → Sonoma and writes `Logs/OceanB/tests.txt`.
     All 261 EditMode tests pass in the Editor, with the 12 `[Explicit]` tools skipped.
5. **Step 2 (2026-11-17): the floor morphs, the water's box is padded, and its skirt bottoms are
   flagged.** No scene has waves yet, so the padding is 0 and the flag unread.
   - **The encoding is `d / (d + 64 m)`, not the plan's `1 − exp(−d / S)`.** Only `+` and `/`, which
     IEEE rounds alike in Burst and managed code, so `ChunkMeshLayout.EncodeFloor` in a test and in the
     job give the same bytes; `exp` would not (`CLAUDE.md`, "No trigonometry inside the continent
     jobs"). A step is 0.98 mm at the shore, and it saturates at 4.2 km, so it never reaches infinity.
     Byte 0 is the high byte and byte 1 the low one; the decode is within half a step everywhere it was
     checked (`EncodeFloorIsMonotoneAndExactAtTheSea`).
   - **The colour by depth reads the morphed floor,** `lerp` of the two decoded depths on the
     geometry's `k`. `TEXCOORD3.w` keeps the fine height, which the shader no longer reads.
     `ChildAtFullMorphEqualsParent` holds each child water vertex's morph-target floor bytes to the
     parent's own floor bytes, and asks for wet vertices to test it on.
   - **The skirt flag is `COLOR.b`,** on the water skirt's bottom row only
     (`ChunkMeshLayout.WaterSkirtFlag`); `WaterSkirtHangsItsDrop` checks it is there, is on no grid
     vertex, and that the terrain's skirt bottoms keep their top's splat.
   - **The box.** The terrain's vertices are boxed as before and the water's apart, padded by
     `OceanParams.WaveDisplacement` and joined, so a Land chunk's box is unchanged and a flat sea's is
     the same bits (`JobPadsTheWaterBoundsByTheWaves`).
   - **Same pixels, except the shallows.** `SampleScene`'s start frame is byte-identical to step 0's.
     In the five ocean scenes 0.30–0.56% of the pixels change, all in a band along the coasts where the
     shallow colour now follows the morphed floor: up to 31 levels of 255 on the planets and the rings,
     and at most 2 in the terrarium, its start pose mostly unmorphed. Diff masks are in `Logs/OceanB`.
   - **A stale test assembly ran twice.** Unity compiled some edits on its own while the Editor was in
     the background, and an `AssetDatabase.Refresh` then found nothing new, so two runs used a test
     assembly older than the last edit. `CompilationPipeline.RequestScriptCompilation` after a forced
     import of the edited file rebuilt it. Check the assembly's time against the files' before trusting
     a run. 263 EditMode tests pass, with the 12 `[Explicit]` tools skipped.
6. **Step 3 (2026-11-17): the wave globals and the rebase.** No scene has waves yet, so every scene pushes
   a count of 0 and nothing reads the globals until Step 4.
   - **`OceanWaveGlobals` pushes them, and TerrainRoot owns one.** The five globals, the 16-long arrays
     and the subscription to `WorldOriginSystem.OriginShifted` live in one small class in
     `Core/Quadtree`, built in `Start` after the sea, pushed from `PushShaderGlobals` and disposed in
     `OnDestroy`. A flat sea or no ocean pushes a count of 0 and does not subscribe. The phases are a
     float array (`SetGlobalFloatArray`), the count an integer (`SetGlobalInteger`).
   - **The shoal depth is on the wave.** `OceanWave.ShoalDepth`, `ShoalFactor × λ`, set by `Build`, so
     the table carries everything the shader's `_SonomaWaveExtra` needs.
   - **`OriginShifted` is raised last in `ShiftOrigin`,** after the origin, the chunks, the tracked
     transforms and the view position have moved, and is cleared on entering play with the origin, so a
     session with domain reload off cannot inherit a destroyed `TerrainRoot`'s handler. The handler
     re-pushes the phases alone, from the new origin and the time the frame's push used.
   - **`WaveTimeOverride`** is a nullable property on `TerrainRoot`, not serialized: set, it replaces
     `Time.timeAsDouble`.
   - **`RebaseCarriesTheWavePhase` drives the class, not a whole `TerrainRoot`,** in `WorldOriginTests`,
     whose fixture saves and restores the origin. A `TerrainRoot` would need a scene's macro build to
     start; the class is exactly what it runs, and it subscribes in its constructor. Over 1,000 real
     `ShiftOrigin`s, a camera wandering within 3 km of a fixed point at Earth radius, the pushed offsets
     are `PhaseOffset` at the new origin to the bit, unused slots are 0, the time does not move, and the
     phase at the point stays within 1.27e-4 cycles. Unsubscribed, the mutation, it is 0.496 cycles off
     after the next shifts.
   - 264 EditMode tests pass in the Editor, with the 12 `[Explicit]` tools skipped.
7. **Step 4 (2026-11-17): the displacement and the shading.** No scene has waves yet, so nothing drawn
   changes; the waves were looked at with them turned on in memory.
   - **`SonomaWaves.hlsl`** holds the globals and three functions. `SonomaWaveDisplace(inout positionOS,
     normalOS, effectiveDepth, floorDepth, skirtFlag, out geometricNormalWS)` moves the morphed vertex
     and returns its undisplaced render-space position. `SonomaWaveNormal(positionWS, up,
     effectiveDepth, footprint, floorDepth, out slopeVariance)` is the fragment's normal. And
     `SonomaWaveGlintPower` widens the Blinn-Phong power by the variance, as a Beckmann roughness
     `2 / (power + 2)` plus the variance. Each wave's weight in the normal is the larger of its
     geometric fade and its fade by the pixel footprint, so the lit surface is never flatter than the
     drawn one.
   - **The up is the morphed normal, so `SonomaMorphPosition` is unchanged.** The plan had it return `k`
     for the depth prepass. But the displacement has to run along the up the parent uses at full morph,
     which is the morphed base normal, not `TEXCOORD1`'s. So DepthOnly now calls `SonomaMorph` with
     the normal and reads `NORMAL`, `TEXCOORD3`, the floors and the skirt flag. The terrain's include
     and the terrain shader are untouched.
   - **A flat sea is `ocean-a`'s to the bit.** With a count of 0 the displacement adds zero, the
     DepthNormals normal is the base normal unchanged, ForwardLit keeps its normal (it renormalises
     only with waves), and the glint power is returned exactly. The six start frames, waves off, are
     byte-identical to Step 2's.
   - **The compile test uses `ShaderData.Pass.CompileVariant`,** not `ShaderUtil.CompilePass`, which
     warms a material's variant rather than reporting a compile. `OceanPassesCompile` compiles 28
     variants on D3D for Windows (ForwardLit under every main-light shadow keyword, soft shadows on and
     off, sky on and off; the depth passes plain) in 4.6 s, and asks for no error and no warning from
     a Sonoma file. `OceanWavesMatchTheTable` holds the include's constants, fade, phase, skirt flag and
     global names to `OceanWaves` and `OceanWaveGlobals`. `ShaderMorphsInEveryPass` asks every ocean
     pass to displace after the morph, by `depth − k`, and the terrain to include no waves. 266
     EditMode tests pass in the Editor, with the 12 `[Explicit]` tools skipped.
   - **Frames.** The driver now adds a low pose in each ocean scene: 12 m above the nearest sea at
     least 30 m deep, looking along the surface 12° down. It runs every scene with waves off, then the
     five ocean scenes with `OceanWaves.asset` and an overlap of 2 m set on their `TerrainSettings` in
     memory, restored on leaving play and never saved, at `WaveTimeOverride` 1234.5. With waves on, the
     sea is rippled near the camera and fades smoothly towards the horizon, with no seam, crack or ring
     visible in a still frame, on all five. Motion, the rebase, the shore and the tuning are Step 5's.
     The console stayed clean.
   - **The temporary scripts stay until Step 5,** still untracked. It flies each scene from a driver
     and runs the suite, and the bridge still refuses `TestRunnerApi`. It also refused a command that
     only deleted a file, and a menu item queued with `delayCall` never ran; a direct `ExecuteMenuItem`
     does.
