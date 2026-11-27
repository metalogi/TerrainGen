# Ocean-b shading — making the waves readable: Implementation Plan

**Audience:** a coding agent working in `G:\UnityProjects\TerrainGen`, with `Docs/Ocean_B_Plan.md` to hand.
**Parent plan:** `Docs/Ocean_B_Plan.md`. Its decisions B1–B6 stand. This document is that plan's Step 5.4,
"tune `OceanWaves.asset` and `Ocean.mat` by eye", which turned out to need shader work first.
**Prerequisite:** `ocean-b` at b07c316 (PR #24), all 266 EditMode tests green. The user has uncommitted
retunes of `OceanWaves.asset`, `Ocean.mat` and `EarthTerrainSettings.asset`. They are the user's: leave
them alone unless S4 says otherwise. Also leave `TerrainBiomes.mat`, `SandboxContinents.asset` and
`Assets/ForestTiles/` alone.

---

## Why

The waves animate and displace, but the user saw no motion. A check on 2026-11-26 in `EarthStress` used
the user's tuning (16 waves, 1.5–1,200 m, 14.8 m at most, `ShoalFactor` 0.75). The camera was 18.8 m above
the sea, looking 15° down. Each pose was rendered at frozen times, with the displacement off, and with no
waves. The frames are `Logs/OceanB/motion_*`.

- **The waves move, but faintly.** Open-sea frames 0.5–4 s apart differ in 28–37% of their pixels, by a
  mean of only about 2 levels in 255. Turning the displacement off alone changes 29% of the pixels, also
  by about 2 levels.
- **Next to the shore nothing moves.** 60 m off the coast, in 2–14 m of water, turning the displacement
  off changes 0.0% of the near quarter of the frame. `ShoalFactor` 0.75 damps every wave longer than about
  four-thirds of the water depth.
- **The frame looks like a still, even blue.**

The geometry is right. The shading hides it.

- **Displacement is not visible on its own.** Lifting a smooth, evenly coloured surface by a metre, seen
  from above, barely changes a pixel. A wave reads only through what its slope does to the light.
- **The reflection is one colour.** Under `_SONOMA_SKY`, `Frag` reflects the constant `_SonomaSkyAmbient`
  (`SonomaOcean.shader`, after the glint). A tilted normal changes only the Fresnel weight, and only at
  grazing angles.
- **Everything else is constant too.** Over deep water the depth colour is one dark value, and diffuse
  lighting on dark water is weak. The glint shows only roughly towards the sun, and nothing marks a crest.

Real water reads mainly by three cues, and this plan adds them in order of value. The first is the sky
reflected across the slopes, light near the horizon and darker overhead. The second is lighter, greener
water in thin crests. The third is whitecaps where crests pinch.

None of these is geometry. They are per fragment and need no agreement between chunks, so B2's rule does
not bind them. They only have to be continuous in what they read, which the wave normal already is.

---

## Decisions to confirm before building

Each has a recommendation, which the steps assume.

### S1 — Reflect a sky gradient, not a constant

**Recommended.** Under `_SONOMA_SKY`, reflect the view ray off the wave normal, `R = reflect(−V, N)`.
Take its elevation above the local horizon, `e = dot(R, topoUp)`. The reflected sky is
`lerp(horizon, zenith, smoothstep(0, _SkyGradientHeight, e))`.

- **The horizon colour** is `SonomaHazeLight(topoUp, ml)`, what the haze already fades to. The reflected
  horizon then matches the hazed horizon behind it, with no seam at the far sea.
- **The zenith colour** is `_SonomaSkyAmbient`.
- **A ray reflected below the horizon** (`e < 0`, the back of a wave) takes the horizon colour.
- **Why it reads.** At the camera's grazing angles, a slope of 0.05–0.2 swings the reflected ray from the
  horizon to 6–25° above it. That moves each fragment between the light and dark ends of the gradient: the
  dominant cue in real water.
- **The cost.** One `reflect`, a `smoothstep` and a lerp, and `SonomaHazeLight` is computed once and passed
  to the haze too. No new `sincos`.
- **`_SkyGradientHeight`** is a material property. It is the sine of the elevation where the zenith colour
  is reached, 0.35 to start (about 20°).
- **Without `_SONOMA_SKY`,** the shader already samples `SampleSH(reflect(−V, N))`, which varies with the
  normal; nothing changes there.

**A flat sea changes too,** recommended. A flat sea at a grazing angle now reflects the horizon colour,
not the ambient, so `ocean-a`'s frames are no longer reproduced bit for bit with `Waves` null. That
invariant was a check that the waves arrived without side effects, done in Steps 2–4. It is not a
property worth keeping at the cost of a sea that reflects the wrong sky.

**The alternative** keeps the gradient behind `_SonomaWaveCount > 0`, so a flat sea keeps `ocean-a`'s
bits. That leaves a flat sea and a calm wavy one looking unlike each other.

### S2 — Lighter water in the crests

**Recommended.** `SonomaWaveNormal` also returns a crest measure, `crest = Σ a·weight·cos / Σ a`, the
waves it carries at their phase, from −1 in the troughs to 1 on the crests.

- **The colour.** ForwardLit tints the albedo towards `_CrestColor` by
  `_CrestStrength × saturate(crest)` before lighting. That is light passing through a thin crest, and it
  is the second strongest cue.
- **Backlit crests,** optionally. A second term, `_CrestBacklight × pow(saturate(dot(V, −L)), 4) ×
  saturate(crest)`, lights crests seen against the sun.
- **What it reads.** It comes from the normal's sum, not the displacement, so it reaches as far as the
  normals do and fades with them. It reads the same cosines the normal already computes, so it costs a
  few multiply-adds.
- **A signature change.** `SonomaWaveNormal` gains an `out float crest`. With no waves it is 0, and a
  zero `_CrestStrength` adds nothing.
- **Defaults.** The shader's properties default to 0. `Ocean.mat` turns them on at 0.35 and 0 (backlight
  off) to start.

**The alternative** is no crest colour; S1 alone may be enough.

### S3 — Whitecaps where the crests pinch

**Recommended, at a low strength, and off by default in the shader.** A Gerstner surface compresses
horizontally at its crests by `stretch = Σ Q·a·w·cos`, which `SonomaWaveNormal` already sums. That is
the classic Jacobian foam measure.

- **The foam.** ForwardLit blends towards `_FoamColor` by `_FoamStrength × smoothstep(_FoamStart,
  _FoamFull, stretch)`, after lighting by `NdotL` and the ambient, so foam darkens at night.
- **The cost.** It reads a sum already made, so one `smoothstep` and a lerp.
- **Tuning.** With the user's tuning (Q 0.8), `stretch` reaches about 0.45. `_FoamStart` 0.25 and
  `_FoamFull` 0.45 put foam on the sharpest crests only.
- **The risk.** Without a texture it draws smooth white crest lines, which can look drawn rather than
  natural. If it does, ship S3 at strength 0 and leave foam to M7, with the shore foam and breaking the
  parent plan already gives it (Q6).

**The alternative** is to defer all foam to M7.

### S4 — The tuning the check found

These are the user's values, so the plan changes them only on an answer.

- **`ShoalFactor` 0.75 stills every shore.** At 0.25, `ocean-b`'s value, a 2 m deep shore still carries
  waves up to 8 m long. The shoreline stays put at any factor, since water of no depth has no amplitude.
  **Recommended: 0.25.**
- **The other four stress scenes do not start.** They share `OceanWaves.asset` but keep a 1.3 m overlap
  against 14.8 m of waves, which `OceanParams.Create` refuses. Earth's 20 m works, but
  `StressScenesHaveTheirOcean` asks for an overlap within a tenth above the amplitude, so it fails. A
  14.8 m overlap makes every chunk within 14.8 m of the sea Coast, two grids each.
  - **Recommended:** settle the wave asset first. Then set each scene's overlap to its amplitude rounded
    up to a tenth, and measure the Coast cost with the flight driver.
  - **Or:** revert `OceanWaves.asset` to the committed 1.26 m waves and judge the shading on those.
  - The answer decides whether Step 4 re-measures memory.

### S5 — Where it lands

**Recommended: on `ocean-b`, in PR #24, before it merges.** The PR's own play check found this, and its
plan left the tuning by eye open. **The alternative** merges #24 as it is and does this on a branch
after it.

---

## Step 0 — Baselines (no commit)

1. Rebuild the motion check as a temporary, untracked Editor script (`Assets/Editor/OceanBMotionCheck.cs`,
   deleted at the end). It does what the 2026-11-26 check did, on every stress scene with an ocean that
   starts:
   - two poses, deep open sea and 60 m off a coast, 3 m over the fly camera's floor, looking 15° down;
   - frozen times 0, 0.5, 1, 2 and 4 s apart;
   - the same frame with the displacement off (`_SonomaWaveLod` huge) and with no waves (`_SonomaWaveCount`
     0).
2. **What it records per pose.** For the bottom, middle and top halves of the sea area, the standard
   deviation of luminance within one frame (how readable a still frame is), and the mean change between
   frames 2 s apart (how readable the motion is).
3. **Run it** at the user's tuning, then at the committed tuning (in memory, never saved). Those figures
   are the "before".

## Step 1 — The sky gradient (S1) (commit)

1. `SonomaOcean.shader`, ForwardLit, under `_SONOMA_SKY`:
   - compute `SonomaHazeLight` once, for the gradient's horizon and for the haze;
   - reflect, take the elevation, blend the gradient, and keep the Fresnel term as it is.
2. Add the property `_SkyGradientHeight`, in the CBUFFER and in Properties, set in `Ocean.mat`.
3. Tests, in `OceanShaderTests`:
   - the ForwardLit source reflects the view ray under the sky path and reads `SonomaHazeLight` once;
   - `Ocean.mat` sets the property;
   - `OceanPassesCompile` still has no messages.
4. Run the check. Record the figures and look at the frames.

## Step 2 — Crests (S2) (commit)

1. `SonomaWaves.hlsl`: `SonomaWaveNormal` gains `out float crest`. Update the header.
2. `SonomaOcean.shader`: the crest tint, and the backlight if chosen. Add `_CrestColor`, `_CrestStrength`
   and `_CrestBacklight`, defaulting to 0 in the shader and set in `Ocean.mat`.
3. Tests:
   - `OceanWavesMatchTheTable` still holds;
   - a source check that the crest measure is the normal's own sum and that a zero strength skips the
     term;
   - the compile test.
4. Run the check.

## Step 3 — Whitecaps (S3) (commit)

1. Add the foam term and `_FoamColor`, `_FoamStrength`, `_FoamStart`, `_FoamFull`, at 0 in the shader.
2. Set `Ocean.mat` by eye. If the foam looks drawn, leave its strength at 0 and say so in the amendment.
3. Tests: the compile test, and `Ocean.mat`'s properties.
4. Run the check.

## Step 4 — Tuning (S4) and measuring (commit)

1. Apply S4's answer to `OceanWaves.asset` and the stress assets. `StressScenesHaveTheirOcean` must pass.
2. If the amplitude changed from 1.261 m, rebuild the flight driver of `Ocean_B_Plan.md` Step 5. Fly each
   scene's descent and coast, recording the Coast share, vertex memory against the budget, and fps. Fly
   once with the new shading and once without, so the fragment cost of S1–S3 is measured too.
3. Run the motion check a last time, and record its figures against Step 0's.
4. Tune `Ocean.mat` by eye with the user.

## Step 5 — Documentation and the PR

1. **`CLAUDE.md`:**
   - the Ocean section's shader bullet: the sky gradient, the crests, the foam if shipped;
   - the "No waves is `ocean-a`" bullet, if S1 is taken as recommended;
   - a Non-Obvious entry: displacement alone does not read, and the shading carries the waves;
   - test counts.
2. Shader headers: the ocean shader's "Shading, per fragment" list, and `SonomaWaves.hlsl`'s crest output.
3. `Docs/Ocean_B_Plan.md`: an amendment pointing here, and this plan's own amendments.
4. Delete the temporary driver, update PR #24's description, and push.

---

## Definition of done

- [ ] S1–S5 answered, and the answers recorded in this plan's amendments.
- [ ] In a still frame over deep water the waves are visible, and in motion they visibly move, judged by
      the user. The Step 0 figures are recorded before and after.
- [ ] Waves move near a shore at the chosen `ShoalFactor`, and the shoreline stays put.
- [ ] Every stress scene starts, and `StressScenesHaveTheirOcean` passes.
- [ ] `SampleScene` and the terrain shader unchanged; every pass still compiles clean.
- [ ] If the amplitude changed, the frame rate, Coast share and memory are recorded.
- [ ] All tests pass; docs written; PR #24 updated.

---

## Risks

- **The gradient may band at the horizon.** A reflected ray grazes `e = 0` across most of a distant sea.
  The `smoothstep` keeps that continuous, and the haze covers the far field.
- **The crest tint shimmers where the normals fade out.** The crest measure fades with the normal's own
  pixel-footprint weights, so it vanishes where the normals stop resolving a wave. If it still shimmers,
  fade it by the slope variance as well.
- **Foam without texture looks drawn.** S3 says what to do then: ship it at strength 0.

---

## Amendments

Record here what building it changed.
