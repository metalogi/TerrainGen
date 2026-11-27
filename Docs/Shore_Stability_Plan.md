# Shore stability — a coastline that holds still across LOD: Implementation Plan

**Audience:** a coding agent working in `G:\UnityProjects\TerrainGen` on a branch of its own, after PR #24
(`ocean-b` and its shading) has merged.
**Context:** `CLAUDE.md`, "The gully term" and "The one number that says whether the terrain cracks"; the
gully plan in `Docs/M5_MacroLayer_Plan.md` (M5c-2, G1), whose machinery this reuses.
**Status:** option B built (amendments 2–4); the play check is the user's. Written 2026-11-26 at the user's request, after they reported that the shoreline
visibly changes shape with LOD, more than the terrain's own LOD switching does.

---

## The problem

The coastline is not drawn by anything of its own. It is where the terrain crosses the flat sea: a level
set of the height function at `SeaLevel`. Each LOD step adds or drops one octave of detail, so the height
at a point changes by that octave's amplitude, `δ`. The geomorph makes that change continuous, so nothing
pops. But a level set magnifies a height change sideways by the ground's slope: the shoreline moves by
`δ / |∇h|`.

- **The land hides the same change.** On land a few metres of height on a hillside a kilometre away are
  barely visible.
- **The coast magnifies it.** At a 2% beach the same change moves the coast by 50 times as much: 4 m of
  height is 200 m of shoreline.
- **What the user sees.** As the camera moves, the morph slides every coast between its fine and coarse
  shapes, and coves open and close.
- **These figures are an estimate.** They come from the scenes' noise spectrum, not a measurement:
  - about 1.3 m an octave at 10 m wavelength on Earth;
  - persistence about 0.53 below 5 km;
  - an octave of 30–60 m wavelength added at about 1 km.

  Step 0 measures them.

The shallows fade (`Docs/Ocean_B_Shading_Plan.md`, amendment 8) softens the edge, so the movement is
less obvious, but it moves the same distance.

There are two sources of `δ` near the coast, and only one is the detail noise's:

1. **The detail octaves**, added one per depth by `HeightParams.MaxOctave(depth)`.
2. **The macro base height**, read at `MacroLayout.MacroLevel(depth)`. The continent field is filtered
   down the pyramid, so the base height also changes between depths, up to the pyramid's top level. The
   continents' coast is steep (slope 15/8 over `CoastWidth`), so it may matter less. Step 0 says.

---

## Decisions to confirm before building

### R1 — A shore term: damp the fine detail near sea level, by the coarser detail alone

**Recommended.** It works like the gully term.
- **The rule.** Every octave `k` from a start wavelength down is multiplied by
  `1 − ShoreDamping × b(u)`, where `u = (P_<k − SeaLevel) / ShoreWidth`.
  - `P_<k` is the base height plus the octaves before `k`, the same prefix the gully term already sums.
  - `b` is a polynomial bump: `(1 − u²)²` for `|u| < 1`, and 0 outside.
- **What it does.** Where the coarser terrain is within `ShoreWidth` of the sea, the fine octaves are
  damped. Those are the ones that change at the LOD steps seen near the ground. The coastline is then
  the level set of octaves that do not change there, so it holds still.
- **What it keeps.**
  - **The geomorph.** Octave `k`'s factor depends only on octaves below `k`, so a coarse evaluation is
    still a strict prefix of a finer one. The geomorph target is still the parent, as with the gullies.
  - **The height bound.** The factor is at most 1, so `MaxAbsHeight` still bounds the height, and every
    relief figure can only fall.
  - **Bit-identical results.** It uses only `+ − × ÷`, so the Burst and managed builds agree to the bit.
  - **The eroded octaves.** Erosion's T is built through `Height` before the term exists, so the term
    must start past octave `Oe`. `ErosionParams.Create` refuses a gully start that does not, and the same
    check applies to this term.
- **What it costs.** Fine detail within `ShoreWidth` of the sea is smoothed: small rocks and coves at
  the waterline become beaches. The coast is shaped at the start wavelength and above. With a
  `ShoreDamping` under 1 the fine coves stay, smaller, and their movement scales by the same factor.

**The alternatives:**
- **Steepen the coast.** Remap height near sea level so that `|∇h|` is large there. That also cuts
  `δ / |∇h|`, but it turns beaches into cliffs everywhere.
- **Accept it, with the shallows fade alone.**

### R2 — Where the term starts, and how wide it is

**Recommended:**
- **The start wavelength** is the wavelength added at the depth where the camera is, at most, a few
  kilometres from the coast, about 500 m on Earth. Step 0's measurement picks it per scene: the longest
  wavelength whose LOD shift is still visible.
- **`ShoreWidth`** has to exceed the damped octaves' summed amplitude. Otherwise the fine detail itself
  carries the height out of the band, and the coast moves anyway. On Earth that is about 10–20 m, which
  Step 0 measures too.
- **`ShoreDamping`** is 0.8 to start, tuned by eye.

### R3 — The macro base height

If Step 0 finds the base height's change between pyramid levels moves the coast visibly, the shore term
cannot fix it, because the base is not an octave. The choices are then:
- read the continent field at the top level at every depth, where the base is coarse enough to sample
  the top;
- or accept it.

Decide on Step 0's numbers.

---

## Step 0 — Measure the shoreline's movement (no commit)

A pure probe, in the out-of-Editor harness or as an `[Explicit]` test. For each continent scene and the
terrarium:
1. **Find shoreline points.** Sample points on the sea-level crossing at `MaxDepth`, about 2,000 of them,
   by bisection along random rays from sea to land.
2. **Measure each depth.** At each depth `d` from 4 to `MaxDepth`, find the crossing at band limits `d`
   and `d − 1` along the local gradient, and record the distance between them. Do the same with the macro
   level fixed, to separate the detail's share from the base height's.
3. **Turn it into pixels.** Express each distance as a share of the distance at which depth `d` morphs,
   `LodMath.MorphRange(d)`. That is roughly the share of the screen the coast slides at that range, so
   report it in pixels at 1080p and 60° FOV.
4. **Report** the median and the 90th percentile per depth, and the share of the movement that the
   detail octaves cause.

That gives R2's start wavelength and width, and R3's answer.

## Step 1 — The term, pure (commit)

1. **The parameters.** `TerrainSettings.ShoreDamping`, `ShoreWidth` and `ShoreStartWavelength`, and
   `HeightParams.WithShore`. A damping of 0 takes the old path to the bit.
2. **The sum.** The term goes in `LatticeNoise.Fbm`'s gully overload, which already carries the prefix.
   It multiplies the gully factor. The general path, with no gullies and no shore, is unchanged.
3. **The guard.** `ErosionParams.Create` refuses a shore start inside the eroded octaves.
4. **Tests**, pure:
   - `ShoreOffKeepsTheBits`;
   - `ShoreDampsNearSeaLevel` (factor 1 outside the band, `1 − ShoreDamping` at its centre);
   - `ShoreKeepsTheBound`;
   - `SceneShoreStartsPastTheErodedOctaves`;
   - `GeomorphTests.ChildAtFullMorphEqualsParent` run with the term on.

## Step 2 — The scenes, and the pins (commit)

1. Set the term on the four continent scenes and the terrarium from Step 0's figures.
2. Re-measure what the term moves:
   - `ContinentsSurviveTheDetailNoise`' land share and agreement, since the term shifts which side of the
     sea some points fall;
   - the designer's `LandFraction` calibration;
   - the pinned relief and skirt figures through Tools → Sonoma → Measure Open Scene, which can only
     fall.
3. Re-pin what Measure Open Scene marks, and run the suite.

## Step 3 — Check it (no commit)

1. Rerun Step 0's probe with the term on. The movement at the chosen depths should fall by about
   `1 − ShoreDamping`.
2. Fly the coasts in play, and compare frames at fixed poses as the camera moves through a morph range.
   This is the user's judgement: does the coast hold still?

## Step 4 — Documentation and PR

`CLAUDE.md` gets the height-function bullet, a Non-Obvious entry on the level set's magnification, the
test counts and the scenes' table; this plan gets its amendments.

---

## Risks

- **The band is not wide enough.** The damped octaves can still carry the height across sea level
  outside the band. Step 0 sizes the width, and the probe in Step 3 checks it.
- **Beaches everywhere.** A strong damping smooths every coast at fine scales. The user tunes it by eye.
- **The macro base height moves the coast too.** R3, decided on Step 0's numbers.
- **The gully term and this one interact.** Both damp the same fine octaves, so a steep coast is damped
  twice. That is harmless: both factors are at most 1, and their product is still a function of the
  prefix.

---

## Amendments

Record here what building it changed.

1. **Step 0, measured (2026-11-27): in a settled tree the coast moves about a pixel per depth.**
   - **The probe.** A temporary, untracked Editor tool, `ShoreProbe`, opened each ocean scene in edit
     mode and built the macro maps as `TerrainRoot` does, erosion included. It found 300 shoreline
     points at `MaxDepth`. At each depth `d` it then found the crossing of depth `d`'s terrain along the
     local gradient, and the crossings of three variants:
     - depth `d − 1`'s terrain (the total shift);
     - only the detail's band limit lowered (the detail's share);
     - only the macro level lowered (the continent map's share).
   - **How the shift was turned into pixels.** At 1080p and 60°, at the middle of depth `d`'s morph
     range: about the share of the screen the coast slides over while that depth blends, with the
     shoreline square to the view.

   | scene | coarse depths seen from far (px, p50 / p90) | depths seen from tens of km down (px, p50 / p90) |
   |---|---|---|
   | `EarthStress` | depths 3–6: 1.0–1.2 / 4–47 | depths 9–19: 0.26–0.48 / 0.9–2.5 |
   | `SmallPlanetStress` | depths 3–5: 1.1–1.3 / 5–56 | depths 8–17: 0.25–0.44 / 0.9–1.8 |
   | `RingworldStress` | depths 3–4: 1.3–1.6 / 6–7 | depths 7–16: 0.21–0.41 / 0.7–1.8 |
   | `NarrowRingStress` | — | depths 3–14: 0.27–0.58 / 0.8–2.2 |
   | `TerrariumStress` | — | depths 3–9: 0.57–0.89 / 1.9–4.9 |

   - **In metres.** On Earth the shift's median is 30 m at depth 10 (morph range 77 km), 1.4 m at depth
     14 (4.8 km) and 4 cm at depth 19 (150 m). At the 90th percentile it is about three to four times
     that.
   - **The coast's slope.** The median slope at the coast is 0.17–0.33 from depth 10 down, steeper than
     a sandy beach. The fine detail and the gullies roughen it.
   - **The detail octaves cause all of it below depth 3.** The macro map changes only up to its top level
     (levels 0–3 on the planets and the wide ring, 0–2 elsewhere). Its own shift is 0.4–2.9 km at depth
     3, on the scenes with continents.
   - **Coasts that vanish between depths** are 0–2% of the points, and only at depths 4–5.
   - **What this says about the premise.** The plan assumed the blend's sliding was what the user saw.
     Near the ground it is about a pixel per depth, so the visible change is more likely one of two
     other things:
     - **The view from altitude**, where the coarse depths move the coast by tens of pixels at the 90th
       percentile.
     - **Late subdivision while the camera moves.** The tree then runs short of depth (`CLAUDE.md`,
       Performance Targets: 3,264 chunks against 7,202 in a fast low flight). A swap made nearer than
       the child's morph range shows the octave's full shift at a fraction of the planned distance, as
       a jump, not a slide.

     R1 would scale both down, by `1 − ShoreDamping`. But the second's cause is chunk throughput, which
     is M6's. This leaves R1–R3 open until the user says under which conditions the change shows.

2. **The decision (2026-11-27): the user sees the change from high up, and chose option B, a vertex's side
   of the sea taken from a finer depth. R1–R3 are superseded.**
   - **Why not R1.** The octaves that move the coast from altitude are Earth's 7–10 (81–10 km), past the
     eroded octave 6, so a shore term could reach them. But damping them near sea level would take every
     bay and peninsula 2.5–80 km across out of the coast. That changes the world; B changes only how
     coarse chunks draw it.
   - **The rule.** A vertex at depth `d` has the height `h_d`, as now. Where it lies on the other side of
     `SeaLevel` from `h_r`, the height at the reference depth `r(d) = max(d + ShoreLead, F)`, it takes
     `h_r` instead. `F` is the first depth whose nominal node size is at most `ShoreReferenceSize`.
   - **What it keeps.**
     - **The geomorph.** The correction is a pure function of the point and the depth. The coarse job
       applies depth `d − 1`'s rule, which is the parent's own, so a fully morphed child is still its
       parent.
     - **The bound.** `h_r` is a value of `Height`, so `MaxAbsHeight` still bounds every vertex.
     - **Every scene without an ocean.** With `ShoreLead` 0 the jobs write the bits they wrote.
   - **What it costs.** Only vertices near enough to the sea for the extra octaves to flip them evaluate
     `h_r`: a vertex further from the sea than those octaves' largest amplitude is skipped, exactly. Where
     the reference reads another macro level, at depths under the pyramid's top, every vertex evaluates it.
   - **The normals** follow the corrected heights. The correction adds octaves the vertex spacing cannot
     carry, at single vertices along the coast, and the slope that adds is at most the octaves' own.

3. **Option B measured, on the drawn mesh (2026-11-27).** The probe now builds the mesh each depth draws,
   both triangles of every cell, and finds where it crosses the sea along the gradient. It compares that
   crossing with the parent mesh's, which is what the morph slides between. Four rules were measured:
   now, lead 2, lead 2 with the reference at least the depth of 20 km nodes, and lead 3 with that floor.

   | scene, depth | now, px p50 / p90 | lead 2 | lead 2, floor | lead 3, floor |
   |---|---|---|---|---|
   | Earth 3 | 1.43 / 106 | 1.21 / 27.3 | 1.09 / 23.1 | the same |
   | Earth 4 | 1.09 / 57.7 | 0.99 / 20.6 | 0.97 / 18.6 | the same |
   | Earth 5 | 1.20 / 26.2 | 1.16 / 11.1 | 1.14 / 10.5 | the same |
   | Earth 6 | 1.10 / 5.36 | 0.98 / 4.26 | 0.98 / 4.77 | the same |
   | Small planet 3 | 1.44 / 161 | 1.31 / 128 | 1.31 / 48.3 | the same |
   | Small planet 4 | 1.20 / 89.7 | 1.02 / 17.5 | 1.02 / 10.1 | the same |
   | Small planet 5 | 1.04 / 14.5 | 0.96 / 6.76 | 0.95 / 6.76 | the same |
   | Ring 2 | 2.11 / 54.9 | 1.54 / 12.9 | 1.50 / 7.44 | the same |
   | Ring 3 | 1.69 / 16.9 | 1.36 / 7.05 | 1.33 / 4.88 | the same |

   - **The tail falls 2 to 9 times at the coarse depths,** and the medians a little. From tens of km down
     every scene's 90th percentile falls by 0–25%, from about 1–2 px. The narrow ring and the terrarium
     have no coarse tail to cut.
   - **What remains is what a mesh cannot draw.** A vertex is one sample of the coast. Islands, straits and
     peninsulas narrower than the parent's spacing appear in a child and vanish in its parent: 4–7% of the
     points at Earth's depths 2–5. No rule per vertex can keep them; finer coastal chunks could, at a cost
     in chunks.
   - **Depth 2 got worse on Earth,** 52 px to 74, from features the size of its 81 km spacing. It is seen
     only from far out in orbit.
   - **The choice:** lead 2, with the reference at least the depth of 20 km nodes (`ShoreReferenceSize`
     20,000 m): Earth's depth 10, the small planet's 8, the ring's 7, the narrow ring's 4 and the
     terrarium's 0. Lead 3 measured the same and costs more.

4. **Built (2026-11-27).** Steps 1–2 became one change, since B touches no pinned figure:
   - **The code.** `HeightParams.WithShore` and `ShoreReferenceDepth`; `TerrainHeightFunction.ShoreHeight`,
     called by both sample jobs after `Height`; `TerrainSettings.ShoreLead` and `ShoreReferenceSize`, read by
     `TerrainRoot` only with `Ocean` on. The six ocean scenes' settings run 2 and 20,000 m.
   - **The skip** shares `OctaveBound` with `MaxAbsHeight`'s detail term, so the two cannot drift apart.
   - **Nothing pinned moves.** `HeightAtPoint`, the erosion's T, the continents' land share and every
     relief and skirt measurement evaluate `Height` without the rule. The rule changes only coastal vertices
     of chunks short of `MaxDepth`; at Earth's `MaxDepth` the reference adds no octave, since the octaves
     run out there.
   - **Tests.** `ShoreTests` (four, pure) and a shore case in `GeomorphTests.ChildAtFullMorphEqualsParent`;
     `StressScenesHaveTheirOcean` pins the settings. 272 green in the Editor.
   - **Not yet checked:** the cost in the sample jobs, and the look from orbit in play (Step 3, 2).
