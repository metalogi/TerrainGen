# Shore stability — a coastline that holds still across LOD: Implementation Plan

**Audience:** a coding agent working in `G:\UnityProjects\TerrainGen` on a branch of its own, after PR #24
(`ocean-b` and its shading) has merged.
**Context:** `CLAUDE.md`, "The gully term" and "The one number that says whether the terrain cracks"; the
gully plan in `Docs/M5_MacroLayer_Plan.md` (M5c-2, G1), whose machinery this reuses.
**Status:** not started. Written 2026-11-26 at the user's request, after they reported that the shoreline
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
