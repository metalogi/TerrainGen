# M4 — Floating Origin at Scale: Exact Implementation Steps

**Audience:** a coding agent working in `G:\UnityProjects\TerrainGen` with no prior context on this project.
**Parent plan:** `SonomaRevisedPlan.md` (repo root), milestone M4.
**Prerequisite:** M3 is complete and merged. `git log --oneline -1` should show `7ad45d7` or later, `git status --short` should be empty, and the 64 EditMode tests should pass before you start.

M4 is the first milestone that runs the system at the scale it was designed for. Everything it needs
structurally already exists — chunks carry `double3 Anchor`, `WorldOriginSystem` rebases from those
anchors rather than shifting by a delta, `LodSelector.Run` takes an absolute `double3` camera position,
and the shader morph measures from `_SonomaViewPosition` in render space. None of it has ever been
exercised beyond a **300 m** sphere whose `RebaseThreshold` of 1000 m means the origin has, in practice,
never rebased at all.

So M4 is less "build the floating origin" than "turn it on at planetary scale and find out what breaks".
The work is a configuration retune, a handful of gaps in the origin system, a stress scene, and the
1,000-rebase test the parent plan asks for.

Every number below was derived and verified before this document was written. The working-set replica
reproduces `CLAUDE.md`'s documented 2970 to within 1%, and the relief model reproduces both its
"23% to spare at SplitFactor 4" and its "overran by up to 2.8× at 3" exactly. Port the numbers; let the
tests confirm the port.

---

## Ground rules

1. **The Earth stress scene gets its own `TerrainSettings` asset.** Do **not** retune `SampleScene`.
   Its SplitFactor 4 was chosen deliberately in `c46e2ae` for a 300 m sphere carrying a 50 m height
   scale, and that decision is still correct *for that scene*. The LOD configuration is legitimately
   scene-dependent; see C1.
2. **Two landable branches**, as M2 and M3 did.
   - **M4a — `m4a-scale-config`.** The settings asset, the octave-count fix, the depth/budget numbers,
     and the pure tests that pin them. No scene, no origin changes.
   - **M4b — `m4b-origin-at-scale`.** Camera/rig handling, the rebase test, the stress scene, the fly-down.
3. **Measure before concluding.** The whole point of M4 is that planetary scale exposes things reading
   the code does not. When a figure here disagrees with what the Editor shows, the Editor is right —
   record it in the amendments section.

---

## Conventions and settled decisions

### C1 — SplitFactor 3 is safe at Earth scale, and that is not a reversal of `c46e2ae`

The terrain-relief bound is a ratio, and it is dominated by `HeightScale / S0`. `LodMath.MaxHalfRelief`
as a fraction of node size is:

```
allowance = (SplitFactor × (2(1 − MorphStartFraction) − Hysteresis) − SizeSpread) / 2
```

With `MorphStartFraction` 0.15, `Hysteresis` 1.1 and a tangent-adjusted cube sphere
(`SizeSpread` = π/2 ≈ 1.5708):

| SplitFactor | allowance |
|---|---|
| 3 | 0.1146 |
| 4 | 0.4146 |

Measured demand is `≈ 3.15 × HeightScale / S0` at persistence 0.5, and `S0` scales linearly with radius
(`S0` = 489.9 m at radius 300, 10,403,799 m at Earth radius):

| configuration | `HeightScale / S0` | demand | vs SF 3 allowance |
|---|---|---|---|
| `SampleScene` — 300 m, HeightScale 50 | 0.10206 | **0.3215** | **2.8× over** |
| Earth, HeightScale 9,000 (real relief) | 0.00087 | 0.0027 | **42× under** |
| Earth, HeightScale 20,000 (dramatic) | 0.00192 | 0.0061 | 19× under |
| Earth, HeightScale 100,000 (absurd) | 0.00961 | 0.0303 | 3.8× under |

This model reproduces `CLAUDE.md`'s two recorded outcomes exactly — 0.3215 / 0.4146 leaves 22.4% spare
at SplitFactor 4 ("23% to spare"), and 0.3215 / 0.1146 is a 2.8× overrun at 3 ("up to 2.8×"). Trust it.

`SampleScene` is a *small planet with enormous mountains*; Earth is not. The constraint that forced
SplitFactor 4 simply does not bind at planetary scale. Also check `LodMath.Create` will accept the
combination: `MaxMorphStartFraction(3, 1.1, π/2)` = **0.1882**, and 0.15 is under it, so it does.

**Still confirm at runtime rather than trusting this table.** `LodSelector.OnChunkReady` already warns
once, naming the measured relief and the allowance, if the generated terrain overruns. If that warning
fires in the stress scene, raise SplitFactor for that scene and re-measure the working set.

### C2 — The working set, measured

Worst case is the camera hovering over a **face corner**, not a face centre. Resident counts include
hidden parents held for an instant collapse. At 98 KB of vertex data per chunk (resolution 33,
80-byte vertices):

| configuration | resident | + preloaded | ≈ memory |
|---|---|---|---|
| MaxDepth 8, SF 4, Preload 1.15 (today) | 2,722 | 2,946 | 282 MB |
| MaxDepth 19, SF 4, Preload 1.15 | 7,878 | 8,506 | 814 MB |
| **MaxDepth 19, SF 3, Preload 1.0** | **4,942** | **4,942** | **472 MB** |

The counts are scale-free — a 300 m sphere and an Earth-radius one give identical figures at the same
depth — which is why the 300 m scene could never have revealed this.

Set `MaxResidentChunks` to **5200** for the stress scene: above the 4,942 worst case, so the
build-and-evict churn warning does not fire, with a little room for the relief pad that this replica
zeroed.

### C3 — `OctaveCount` must go to 24, or the last four depths are smooth mush

This is the one that will look like a rendering bug if it is missed.

`MaxOctave(d) = clamp(d + K0, 0, OctaveCount − 1)`, and `K0` is 4 for the reference configuration
(`λ₀ = S0`, R = 33). At depth 19 that wants octave 23. With today's `OctaveCount` of 20 it clamps to
19, whose wavelength is `λ₀ / 2^19` = **19.8 m** — while the vertex spacing at depth 19 is **0.62 m**.

So you would fly all the way down to sub-metre vertex spacing over terrain whose finest feature is
20 m across: perfectly smooth ground, thirty times more triangles than information. It reads as "the
detail levels aren't working", and the cause is one integer.

Set `OctaveCount = 24` for the stress scene. Then `MaxOctave(19) = 23`, giving a 1.24 m finest
wavelength against 0.62 m spacing — right at Nyquist, which is the intent.

Depth/spacing reference at Earth radius, R = 33:

| target spacing | depth | node size |
|---|---|---|
| 1000 m | 9 | 20,320 m |
| 100 m | 12 | 2,540 m |
| 10 m | 15 | 317.5 m |
| **1 m** | **19** | **19.8 m** |

### C4 — Triplanar precision: measure, document, defer

`SonomaTerrainTriplanar.shader` computes `wpTri = input.positionWS + _SonomaWorldOrigin` (line ~276).
`_SonomaWorldOrigin` is a `float3` narrowed from a `double3`; at Earth radius its ULP is **0.5 m**, so
the triplanar sample coordinate carries up to 0.25 m of error — and because the origin only changes on
rebase, the error is a *constant offset that jumps* each time it does. That is precisely the "texture
swimming" `SonomaRevisedPlan.md` §4.5 predicted.

**Do not fix it in M4.** All four textures on `TerrainTriplaner.mat` are unset (`m_Texture: {fileID: 0}`),
so every triplanar sample returns the default white texture and is multiplied by a solid colour. The
error is not merely small right now — it is mathematically invisible. The elevation band blend reads
`TEXCOORD1.w`, a vertex attribute, and the cliff blend reads normals; neither touches `wpTri`.

§4.5 itself says "decide when textures are real". They are not. Building precision machinery for
content that does not exist is speculative work, and the fix constrains texture periods to powers of
two (reduce the origin modulo a large power of two, so all scales tile across the wrap), which is a
decision better made alongside the actual textures.

**Record the constraint** in the shader header comment and in `CLAUDE.md` so whoever adds textures
finds it, and move on.

---

## Task 1 — Scale configuration (M4a)

Create `Assets/Settings/EarthTerrainSettings.asset` — a second `TerrainSettings`, leaving the existing
one untouched:

| field | value | why |
|---|---|---|
| `ChunkResolution` | 33 | unchanged |
| `MaxDepth` | 19 | 1 m vertex spacing at Earth radius (C3) |
| `HeightScale` | 9000 | realistic relief; keeps the relief demand 42× under the SF 3 allowance (C1) |
| `SplitFactor` | 3 | C1 |
| `HysteresisFactor` | 1.1 | unchanged |
| `MorphStartFraction` | 0.15 | under the 0.1882 ceiling for SF 3 (C1) |
| `PreloadFactor` | 1.0 | C2; no preload margin, in exchange for 3,500 fewer chunks |
| `MaxResidentChunks` | 5200 | above the 4,942 worst case (C2) |
| `OctaveCount` | 24 | C3 — the one that is invisible until you look for it |
| everything else | as `TerrainSettings.asset` | |

`MaxDepth` 19 is well inside both hard ceilings: `NodeId.MaxAddressableDepth` is 30, and the shader's
`_SonomaMorphRanges` holds 32 entries.

**Watch the asset-migration trap.** M2 changed `TerrainSettings`'s fields and left the asset stale; it
took a review to catch. Creating the asset through the Editor's `Sonoma/Terrain Settings` menu gets the
current field set by construction — prefer that to hand-writing YAML.

## Task 2 — `WorldOriginSystem`: camera and rig handling (M4b)

`Assets/Scripts/Core/CoordinateSpace/WorldOriginSystem.cs` currently zeroes `Camera.main` and rebases
every `TerrainChunk`. Two gaps:

- **It only moves the camera.** §4.5 asks for "the camera and any tracked rigs". Nothing else exists in
  the scene yet, so add a minimal, tested hook — a `List<Transform> Tracked` that gets shifted by the
  same delta — rather than machinery for objects that do not exist. Shift tracked transforms by the
  delta; chunks are still recomputed from their anchors, which is the part that must not accumulate.
- **It assumes the camera is unparented.** `cam.transform.position = Vector3.zero` sets a world
  position, which is right for a lone camera and wrong for a camera under a rig. Either shift the
  outermost parent, or document that the camera must be a root object. Document; do not over-engineer.

Also reconsider `RebaseThreshold`. At 1000 m and 3000 m/s the origin rebases roughly every 20 frames,
each pass touching up to ~5,000 chunks. That is cheap, but confirm it in the profiler rather than
assuming — it is the one per-rebase cost that scales with the working set.

## Task 3 — The stress scene (M4b)

Create `Assets/Scenes/EarthStress.unity`: a copy of `SampleScene` with `TerrainRoot.Radius` = 6371000,
`Settings` pointing at the new asset, and `TopologyFlyCamera` positioned at altitude.

`TopologyFlyCamera` auto-speed is `BaseSpeed + elevation × SpeedPerMetre`, clamped to `MaxSpeed`. The
scene currently has `MaxSpeed` 3000, so a descent from 1,000 km takes about five and a half minutes —
tedious but usable. Raise `MaxSpeed` for this scene, or accept it; do not change the clamping model,
which is fine at the scales it was built for.

## Task 4 — Tests

### Pure (run outside the Editor)

1. **`DeepestDepthIsNotOctaveStarved`** — for the Earth configuration, assert
   `MaxOctave(MaxDepth) < OctaveCount − 1` is **false only because it is unclamped**, i.e. that
   `MaxDepth + K0 ≤ OctaveCount − 1`. Equivalently: the finest octave's wavelength is at most twice the
   vertex spacing at `MaxDepth`. This is C3 as an assertion, and it is the test that would have caught
   the mush.
2. **`EarthConfigurationClearsTheReliefAllowance`** — assert `MaxHalfRelief(d) / NominalSize(d)` at the
   Earth settings exceeds the measured demand `3.15 × HeightScale / S0` with margin, and that the same
   check *fails* for the `SampleScene` settings at SplitFactor 3. Asserting both directions is what
   stops the table in C1 rotting into a comment nobody re-derives.
3. **`WorkingSetFitsTheResidentBudget`** — a simulated descent (reuse `LodBoundaryTests`'s `Descend`)
   asserting the resident count at `MaxDepth` 19 stays under `MaxResidentChunks`. Pin the measured
   4,942 with a tolerance, so a change to `SplitFactor` or `PreloadFactor` that quietly triples memory
   fails here rather than in the profiler.

### Editor

4. **`RebaseIsExactAfterAThousandShifts`** — the parent plan's headline test. Create chunks with known
   anchors, apply 1,000 random origin shifts, and after each assert
   `transform.position == (float3)(Anchor − WorldOrigin)` **exactly** — bit-identical, not within a
   delta. The property being tested is that nothing accumulates, and a tolerance would hide exactly the
   drift the design exists to prevent.
   **Mutation check:** reintroducing `position -= shift` must fail it. If it still passes, the test is
   not testing what it claims.
5. **`TrackedTransformsMoveWithTheOrigin`** — a tracked transform keeps its world-relative position
   across a rebase.

## Task 5 — Documentation

- `CLAUDE.md`: status to M4; the C3 octave-count trap and the C4 texture-precision constraint belong in
  Non-Obvious Implementation Details, both being invisible-until-they-bite.
- `SonomaRevisedPlan.md` §4.5: record that the triplanar fix is deliberately deferred and why.
- Shader header comment: the `_SonomaWorldOrigin` precision limit and the power-of-two texture-period
  constraint any future fix implies.

---

## Verification

### Agent-side, without the Editor

Compile `Sonoma.Core` and `Sonoma.Tests.EditMode` with Unity's own Roslyn and `.rsp` arguments, outputs
redirected to scratch. Then compile every UnityEngine-free source plus a reflection runner and run it:
all pure tests green, including the 53 that already pass this way. See the
`unity-verification-without-batchmode` note.

Note that `LodMathSettings.cs` imports `Sonoma.Systems.Configuration`, so it needs `UnityEngine`
transitively even though it never says so — the harness must exclude it by that import, not just by a
`using UnityEngine` line.

### Human-side, in the Editor

1. Console clean; the Burst witness still reports compiled; Test Runner all green.
2. `SampleScene` **unchanged** — same LOD behaviour as before M4. This is the regression that matters,
   since M4 touches shared configuration.
3. `EarthStress`: fly from orbit to ground. Watch for
   - jitter or swimming of nearby geometry as the origin rebases (the thing M4 exists to disprove),
   - the relief warning in the console (C1 — if it fires, SplitFactor 3 was wrong for this scene),
   - the resident-budget warning (C2 — if it fires, the working set exceeded the estimate),
   - visible detail continuing to appear all the way down (C3 — if the ground goes smooth around
     20 m features, `OctaveCount` is still too low).
4. Profiler at ground level: terrain work within `UploadBudgetMs`, zero GC alloc per frame, and the
   rebase pass not showing up as a spike.

---

## Definition of done

- [ ] `EarthTerrainSettings.asset` exists with the C1–C3 values; `TerrainSettings.asset` is untouched.
- [ ] `WorldOriginSystem` shifts tracked transforms and documents the unparented-camera assumption.
- [ ] `EarthStress.unity` flies from orbit to sub-metre ground detail without jitter.
- [ ] 5 new tests pass; the 64 existing ones still pass; test 4 fails under the `position -= shift` mutation.
- [ ] The triplanar precision limit is recorded in the shader, `CLAUDE.md` and §4.5, and deliberately not fixed.
- [ ] `CLAUDE.md` updated; amendments section written.

## Out of scope for M4

- **Fixing triplanar precision.** C4. Revisit when textures are real — M7, or whenever they land.
- **The macro layer, biomes, erosion.** M5; `MacroSample` stays zero.
- **Colliders, the debug overlay, the heightmap cache, the profiling pass.** M6.
- **Retuning `SampleScene`.** Ground rule 1.
- **Lazy infinite plane-grid roots.** Deferred since M1, and a cube sphere does not need them.
- **2:1 quadtree balancing.** Optional, M6 at the earliest.
