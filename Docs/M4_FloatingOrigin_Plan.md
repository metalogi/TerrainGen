# M4 — Floating Origin at Scale: Exact Implementation Steps

**Audience:** a coding agent working in `G:\UnityProjects\TerrainGen` with no prior context on this project.
**Parent plan:** `SonomaRevisedPlan.md` (repo root), milestone M4.
**Prerequisite:** M3 is complete and merged. `git log --oneline` should include the relief and budget reconciliation that followed `7ad45d7` ("Reconcile SampleScene's relief and budget figures with the runtime"), `git status --short` should be empty, and the 73 EditMode tests should pass before you start.

M4 is the first milestone that runs the system at the scale it was designed for. Everything it needs
structurally already exists — chunks carry `double3 Anchor`, `WorldOriginSystem` rebases from those
anchors rather than shifting by a delta, `LodSelector.Run` takes an absolute `double3` camera position,
and the shader morph measures from `_SonomaViewPosition` in render space. None of it has ever been
exercised beyond a **300 m** sphere whose `RebaseThreshold` of 1000 m means the origin has, in practice,
never rebased at all.

So M4 is less "build the floating origin" than "turn it on at planetary scale and find out what breaks".
The work is a configuration retune, a handful of gaps in the origin system, a stress scene, and the
1,000-rebase test the parent plan asks for.

Every number below was derived before this document was written, and two of them were corrected on
2026-10-10, before M4 started, by measuring instead of sampling. The relief demand in C1 first
reproduced `CLAUDE.md`'s 3.15 exactly — which is how it inherited that figure's error, a 49-node sample
standing in for the whole population. The working set in C2 first held no node split by hysteresis.
Port the numbers; let the tests confirm the port.

---

## Ground rules

1. **The Earth stress scene gets its own `TerrainSettings` asset.** Do **not** retune `SampleScene`
   any further. Its SplitFactor 4 (`c46e2ae`), and its MorphStartFraction 0.09 and MaxResidentChunks
   4000 (set just before M4), were chosen for a 300 m sphere carrying a 50 m height scale and are
   pinned by `TerrainReliefTests` and `WorkingSetTests`, whose `ConstantsMatchSampleScene` fails if the
   asset changes; they are correct *for that scene*. The LOD configuration is
   legitimately scene-dependent; see C1.
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

With `Hysteresis` 1.1 and a tangent-adjusted cube sphere (`SizeSpread` = π/2 ≈ 1.5708):

| SplitFactor | MorphStartFraction | allowance |
|---|---|---|
| 3 | 0.15 | 0.1146 |
| 4 | 0.15 | 0.4146 |
| 4 | 0.09 | 0.6546 — `SampleScene`'s asset |

Demand is `C × HeightScale / S0`, where `C = halfRelief / (nodeSize × HeightScale/S0)` for the roughest
node. `S0` scales linearly with radius (489.9 m at radius 300, 10,403,799 m at Earth radius) and `C` does
not depend on radius at all — but it **does depend on depth**, which the first version of this section
missed. At persistence 0.5 each octave coarser than the node adds the same slope, so `C` creeps up with
the number of octaves a node carries:

| depth | octaves | `C`, roughest node | how it was measured |
|---|---|---|---|
| 8 | 13 | **5.89** | every node on all six faces; `TerrainReliefTests.WorstReliefFactor` |
| 19 | 24 | **≈ 9, not a bound** | 20,000 random nodes: maximum 8.05, 99.9th percentile 6.81 against 4.34 at depth 8; ≈ 9 is that percentile scaled by depth 8's max/p99.9 ratio |

A face at depth 19 is 4^19 nodes, so that row is a sample by necessity — the same kind of number this
section was corrected for. The extrapolation only says what the worst of about 393k nodes (depth 8's
population) would look like. A long low-altitude session generates far more depth-19 chunks than that,
and its tail is unmeasured. The same sample's maximum at depth 16 was already 9.74. So treat `C` at Earth
depth as an estimate, use **`C` = 15**, half again over the largest sampled value, and let the runtime
report (the selector names the roughest chunk when the terrain shuts down) be the arbiter. Persistence
matters far more than depth: at 0.6 and above, `C` grows roughly as `(2 × persistence)^depth`, so none of
this carries over to a rougher noise.

| configuration | `HeightScale / S0` | demand | vs SF 3, 0.15 allowance (0.1146) |
|---|---|---|---|
| `SampleScene` — 300 m, HeightScale 50, depth 8 (measured `C` 5.89) | 0.10206 | **0.601** | **5.2× over** |
| Earth, HeightScale 9,000 (real relief), depth 19, `C` 15 | 0.000865 | 0.0130 | **9× under** |
| Earth, HeightScale 20,000 (dramatic), `C` 15 | 0.00192 | 0.0288 | 4× under |
| Earth, HeightScale 100,000 (absurd), `C` 15 | 0.00961 | 0.144 | **1.3× over** |

The first version of this table used `C ≈ 3.15` and "reproduced `CLAUDE.md`'s two recorded outcomes
exactly" — of course it did: all three came from the same sample of 49 nodes per depth on one face,
the 98.8th percentile of the depth-7 population. The `SampleScene` relief warning in the first play
session was the evidence against it. `SampleScene` at SplitFactor 4 and MorphStartFraction 0.15 actually
overran by up to 45% at depth 8, which is why its asset now runs 0.09.

The conclusion survives the correction. `SampleScene` is a *small planet with enormous mountains*;
Earth is not, and the constraint that forced SplitFactor 4 (and then MorphStartFraction 0.09) does not
bind at planetary scale with realistic relief, even at `C` 15. It does at HeightScale 100,000. Also check
`LodMath.Create` will accept the combination: `MaxMorphStartFraction(3, 1.1, π/2)` = **0.1882**, and
0.15 is under it, so it does.

**Still confirm at runtime rather than trusting this table.** `LodSelector.OnChunkReady` warns on the
first chunk whose relief overruns the allowance, and the selector names the *roughest* one when the
terrain shuts down. If either fires in the stress scene, raise SplitFactor for that scene, retune against
the roughest chunk rather than the first, and re-measure the working set.

### C2 — The working set, measured

Worst case is the camera low over a **face edge** — at or near a corner — not a face centre. The
counts are the whole wanted set: hidden parents held for an instant collapse, preloaded children, and
the children of every node that hysteresis is still holding split. Measured over face 0, 257 × 257
camera positions at 0.5, 2 and 10 m altitude (all three give the same worst). At 98 KB of vertex data
per chunk (resolution 33, 80-byte vertices):

| configuration | fresh splits only | with hysteresis-held splits | ≈ memory |
|---|---|---|---|
| MaxDepth 8, SF 4, Preload 1.15 (`SampleScene`'s geometry) | 2,946 | 2,946 | 282 MB |
| MaxDepth 19, SF 4, Preload 1.15 | 8,794 | 8,794 | 842 MB |
| **MaxDepth 19, SF 3, Preload 1.0** | 4,458 | **5,118** | **490 MB** |

Hysteresis-held splits are the column that counts. A node the camera has just backed away from stays
split out to `HysteresisFactor` × its split distance, so its four children stay wanted. When
`PreloadFactor` exceeds `HysteresisFactor` preload was asking for those children anyway, and the
columns agree. At `PreloadFactor` 1.0 nothing else asks for them, and they are 15% of the set. The first
version of this table gave 4,942 here and set the budget at 5200 — 1.6% above the 5,118 that a camera
which has just backed away can actually leave wanted.

**The two replicas disagree, and that is not resolved.** The replica behind this table does not
reproduce the first version's 4,942 for the fresh-split case: it gives 4,458, 10% lower. The direction
is not consistent across configurations either — at SF 4 / Preload 1.15 it gives *more* than the first
version did (8,794 against 8,506) — so it is not one replica simply counting less. The first replica no
longer exists to compare against. Until they are reconciled, size from the larger. Scaled by the 15%
that hysteresis-held splits add here (5,118 / 4,458), the first replica's figure implies a worst case of
about **5,670**.

The counts are scale-free — a 300 m sphere and an Earth-radius one give identical figures at the same
depth — which is why the 300 m scene could never have revealed this. The relief pad is left out, and
at Earth scale that is safe: the roughest node's pad is `C × HeightScale/S0` ≈ 0.013 of its size at
`C` 15, against 0.60 on `SampleScene`, where the pad takes the working set from 2,946 to 3,546.

Set `MaxResidentChunks` to **6400** for the stress scene: 13% above the larger estimate of 5,670 — the
same headroom `SampleScene`'s 4000 has over its 3,546 — so the build-and-evict churn warning does not
fire. That is about 630 MB at the worst case. Two things settle the number, and either may bring it back
down to about 5,800 (13% over 5,118). Task 4 test 3 copies `WorkingSetTests.Select`, which calls the
same `LodMath.NodeDistance` the selector does, so its figure is the selector's. And the selector reports
the session's peak working set when the terrain shuts down. If test 3 reproduces 5,118 *and* the
EarthStress peak stays under it, lower the budget and record the figure in the amendments.

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

### C5 — Skirt depth is absolute, so the Earth asset cannot inherit it

`SkirtDepth` is metres, not a fraction of the node: `ChunkMeshJob` drops every skirt vertex by
`TopoElevation × SkirtDepth` whatever the chunk's size, and `GenerationScheduler` pads every chunk's
bounds by the same amount. `SampleScene`'s 2 m suits a 300 m sphere whose finest nodes are 1.9 m
across. At Earth radius nodes run from 20 m (depth 19) to 10,400 km (depth 0). The vertical gap a skirt
has to hide in a transient two-depth state is on the order of the coarse node's relief,
`2 × C × HeightScale/S0` of its size: about 2.6% at `C` 15, so roughly 1 km at depth 8 and half a metre at
depth 19. A 2 m skirt hides nothing coarser than about depth 17. The fly-down from orbit passes through
every coarser depth while the tree is changing fastest, so the cracks would show in exactly the
verification meant to judge rebasing, and would read as origin jitter.

One large value is no better. A kilometre of skirt hanging straight down from a 20 m chunk comes out of
any hillside below it, and bloats every chunk's culling bounds by a kilometre.

So M4a makes the skirt node-relative, without touching `SampleScene`. Add `SkirtFraction` to
`TerrainSettings` (default 0) and drop each chunk's skirt by
`SkirtDepth + SkirtFraction × NominalSize(depth)`, computed once per chunk where `GenerationScheduler`
reads `_skirtDepth` today and used for its bounds pad too. At 0 the meshes and `SampleScene` are
bit-identical, and `SkirtsEnabled` still zeroes the lot. The Earth asset sets it (Task 1), and the
stress scene checks it with skirts on during a fast descent, when transients are most frequent.

---

## Task 1 — Scale configuration (M4a)

Create `Assets/Settings/EarthTerrainSettings.asset` — a second `TerrainSettings`, leaving the existing
one untouched:

| field | value | why |
|---|---|---|
| `ChunkResolution` | 33 | unchanged |
| `MaxDepth` | 19 | 1 m vertex spacing at Earth radius (C3) |
| `HeightScale` | 9000 | realistic relief; keeps the relief demand 9× under the SF 3 allowance at `C` 15 (C1) |
| `SplitFactor` | 3 | C1 |
| `HysteresisFactor` | 1.1 | unchanged |
| `MorphStartFraction` | 0.15 | under the 0.1882 ceiling for SF 3 (C1). Deliberately not `SampleScene`'s 0.09: Earth's relief leaves room for the wider morph window |
| `PreloadFactor` | 1.0 | C2; no preload margin, in exchange for about 3,700 fewer chunks |
| `MaxResidentChunks` | 6400 | 13% above the larger of the two replicas' worst cases, about 5,670 (C2); may come down to ~5,800 once test 3 and the runtime peak report agree |
| `OctaveCount` | 24 | C3 — the one that is invisible until you look for it |
| `SkirtDepth` | 1 | an absolute floor for the finest depths; the node-relative term below does the rest (C5) |
| `SkirtFraction` | 0.03 | new field (C5): the node-relative term, rounded up from the ~2.6% gap estimate |
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
   Earth settings exceeds the demand `C × HeightScale / S0` with margin, taking `C` = 15 at depth 19
   (C1 — an estimate with margin, not a measurement; say so in the test), and that the same check
   *fails* for the `SampleScene` settings at SplitFactor 3 with
   `C = TerrainReliefTests.WorstReliefFactor` (5.89, the full-scan depth-8 figure). Asserting both
   directions is what stops the table in C1 rotting into a comment nobody re-derives. Do not reach for
   3.15: it was a sample, and this test would have locked it in.
3. **`WorkingSetFitsTheResidentBudget`** — a simulated descent asserting the wanted set at `MaxDepth` 19
   stays under `MaxResidentChunks`. Copy `WorkingSetTests.Select` rather than `LodBoundaryTests`'s
   `Descend`: it counts preloaded children and holds every split hysteresis can
   (`ShouldSplit(..., currentlySplit: true)`). Drop its relief pad, which is negligible at Earth scale
   (C2), and with it the job system, so the test stays pure. Its distance is `LodMath.NodeDistance`,
   the selector's own, so pass zero heights rather than writing a padless copy.
   Pin what it measures — 5,118 at face 0, camera at `(u, v) = (20/256, 0)`, 2 m up, if the current
   replica is right — with a tolerance, so a change to `SplitFactor` or `PreloadFactor` that quietly
   triples memory fails here rather than in the profiler. If it measures something else, C2's
   disagreement is resolved in that direction; record it in the amendments and size the budget from it.

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

- `CLAUDE.md`: status to M4; the C3 octave-count trap, the C4 texture-precision constraint and the C5
  absolute-skirt trap belong in Non-Obvious Implementation Details, all three being
  invisible-until-they-bite.
- `SonomaRevisedPlan.md` §4.5: record that the triplanar fix is deliberately deferred and why.
- Shader header comment: the `_SonomaWorldOrigin` precision limit and the power-of-two texture-period
  constraint any future fix implies.

---

## Verification

### Agent-side, without the Editor

Compile `Sonoma.Core` and `Sonoma.Tests.EditMode` with Unity's own Roslyn and `.rsp` arguments, outputs
redirected to scratch. Then compile every UnityEngine-free source plus a reflection runner and run it:
all pure tests green, including the 58 that already pass this way. See the
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
   - on leaving play mode, the selector's reports of the roughest chunk and the peak working set,
     which are the numbers to retune against, not the first ones the warnings named,
   - cracks along chunk edges during the fast part of the descent, with skirts on (C5 — if they show
     at coarse depths, `SkirtFraction` is too small),
   - visible detail continuing to appear all the way down (C3 — if the ground goes smooth around
     20 m features, `OctaveCount` is still too low).
4. Profiler at ground level: terrain work within `UploadBudgetMs`, zero GC alloc per frame, and the
   rebase pass not showing up as a spike.

---

## Definition of done

- [x] `EarthTerrainSettings.asset` exists with the C1–C3 and C5 values; `TerrainSettings.asset` is untouched apart from gaining `SkirtFraction: 0`.
- [x] Skirt depth is node-relative through `SkirtFraction`, and at 0 `SampleScene` builds bit-identical meshes (C5).
- [x] `WorldOriginSystem` shifts tracked transforms and documents the unparented-camera assumption.
- [ ] `EarthStress.unity` flies from orbit to sub-metre ground detail without jitter or cracks.
- [ ] 5 new tests pass; the 73 existing ones still pass; test 4 fails under the `position -= shift` mutation.
- [x] The triplanar precision limit is recorded in the shader, `CLAUDE.md` and §4.5, and deliberately not fixed.
- [x] `CLAUDE.md` updated; amendments section written.

## Amendments from implementing M4

The plan above is the plan as written. Building it changed the following; they are recorded here
rather than edited into the tasks, so the plan still reads as what was decided up front.

### M4a

1. **C2's two replicas are reconciled, in the second one's favour.** Task 4 test 3's `Select` calls
   `LodMath.NodeDistance`, the selector's own distance, and over face 0 (257 × 257 cameras at 0.5, 2
   and 10 m) it reproduces the current replica exactly: **5,118** with hysteresis-held splits, at
   `(20/256, 0)`, and **4,458** without, at the corner `(0, 0)`. The first replica's 4,942 was the
   outlier, so the "about 5,670" sized from it no longer stands. One small correction to the table's
   claim that all three altitudes give the same worst: at 10 m it is 5,110, at the same camera.
   `EarthScaleTests.WorkingSetFitsTheResidentBudget` pins 5,118 at that camera with a 1% tolerance, and
   walks a descent from 10,000 km to 2 m asserting every step is under budget and none is above the
   ground figure.
   **The budget stays 6400 for now.** C2 names two conditions for bringing it down to about 5,800;
   this is the first. The second is the `EarthStress` peak report, which needs M4b and a play session.
2. **An absolute 2 m skirt covers depths 18 and 19, not "nothing coarser than about depth 17".** At
   `C` 15 the coarse-node gap at depth 17 is 2.06 m. `SkirtFraction` 0.03 clears the 2.6% estimate at
   every depth; `EarthScaleTests.SkirtCoversTheTransientGapAtEveryDepth` asserts both.
3. **Octave starvation also warns at runtime, not only in a test.** C3 made it a test. It is also
   `HeightParams.IsOctaveStarved(depth)`, and `TerrainRoot.Start` logs a warning naming the depth the
   octaves run out at and the `OctaveCount` that would fix it, because the next scene to raise
   `MaxDepth` will not have a test of its own. Not a throw: a deliberately smooth world is legitimate.
4. **`EarthTerrainSettings.asset` was written by hand, not through the menu**, because the Editor
   held the project open. The ground rule's trap — a stale field set — is covered by
   `EarthSettingsAssetTests.ConstantsMatchTheEarthAsset`, which holds every constant `EarthScaleTests`
   copies to the asset, `SkirtFraction` included, and so fails on a missing field whose C# default
   differs. `WorkingSetTests.ConstantsMatchSampleScene` now also asserts `SampleScene`'s
   `SkirtFraction` is 0.
5. **`LodMath.NominalSize` gained a static `(s0, depth)` overload**, which the instance method calls.
   `GenerationScheduler` sizes skirts with it and has `S0` but no `LodMath`; one expression means the
   two cannot disagree on a node's size.
6. **Test count.** M4a adds six: four in `EarthScaleTests` (the plan's three pure tests, plus the
   skirt), `ChunkMeshLayoutTests.SkirtDropIsExactWithNoFraction` for C5's bit-identical claim, and the
   Editor-only asset check. 79 in all, 63 of them pure.

### M4b

7. **C4 understated the triplanar error: the sum is quantised, not merely offset.**
   `positionWS + _SonomaWorldOrigin` is a float the size of the world, so at Earth radius *every
   fragment within half a metre* gets the same sample coordinate, and the pattern jumps by up to 0.25 m
   per rebase. The decision stands — no textures, nothing visible — but the shader header, `CLAUDE.md`
   and §4.5 record the quantisation, and that the current 0.05 scales (20 m periods) would not meet the
   power-of-two constraint the fix implies.
8. **`WorldOriginSystem.OnDestroy` zeroes the origin.** Not in the plan. The origin is static, and the
   Editor does not reload the domain on leaving play mode, so after an `EarthStress` session it would
   otherwise sit megametres out for the next session or EditMode test run. Before M4 nothing moved it.
9. **`WorldOriginSystem.ShiftOrigin(Vector3)` is public**, split out of `LateUpdate`, so the rebase test
   drives the real rebase without a camera. `LateUpdate` calls it, then zeroes the camera.
10. **`TopologyFlyCamera` flies by clearance above the terrain**, through a new `TerrainRoot.HeightAt`,
    and works out its absolute position in double. The plan said leave the speed model alone, and the
    clamping is unchanged; what changed is what "elevation" measures. Measured from the base surface,
    a camera two metres above a 5 km peak at `HeightScale` 9000 flew at 5 km/s, which would have made
    the "nearby geometry" checks impossible. On `SampleScene` the two differ by tens of metres at most.
11. **`EarthStress` camera:** at `(0, 0, −7,371,000)`, 1,000 km above a face centre, looking straight
    down — the same framing as `SampleScene`'s camera on the −Z axis. Clip planes 0.3 m to 20,000 km,
    static: `SampleScene`'s 1 m to 10 km would show nothing from orbit. The range is safe because URP's
    default depth attachment on Windows is 32-bit float with reversed Z
    (`CoreUtils.GetDefaultDepthStencilFormat`). `MaxSpeed` 50,000 m/s: the descent from 1,000 km takes
    about half a minute at the default wheel setting, and above about 60 km the camera covers most of
    `RebaseThreshold` every frame, so the origin rebases every frame or two -- the stress case.
12. **Test 4's mutation check was run against an arithmetic replica, not yet in the Editor.** The same
    random sequence with `Transform` replaced by a `float3`: `position -= shift` disagrees with the
    recomputed position on the first shift, for near and far chunks alike, in 127,469 of 128,000
    comparisons. The in-Editor run of the real mutation is still owed.
13. **`EarthSettingsAssetTests.EarthStressRunsTheEarthAsset`** holds the scene to the asset and the
    radius, as `ConstantsMatchSampleScene` does for `SampleScene`. Both now read scenes through a
    shared `SceneConfig` helper. 82 tests in all, 63 pure.

## Out of scope for M4

- **Fixing triplanar precision.** C4. Revisit when textures are real — M7, or whenever they land.
- **The macro layer, biomes, erosion.** M5; `MacroSample` stays zero.
- **Colliders, the debug overlay, the heightmap cache, the profiling pass.** M6.
- **Retuning `SampleScene`.** Ground rule 1.
- **Lazy infinite plane-grid roots.** Deferred since M1, and a cube sphere does not need them.
- **2:1 quadtree balancing.** Optional, M6 at the earliest.
