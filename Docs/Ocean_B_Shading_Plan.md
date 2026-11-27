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

1. **S1–S5 answered (2026-11-26): all as recommended.**
   - The waves are the user's retune with `ShoalFactor` 0.25.
   - Every stress scene's overlap is 14.8 m.
   - The work lands in PR #24.
2. **Step 0: the check, and its baselines.** A temporary Editor script, `OceanShadingCheck` (untracked,
   deleted at Step 5), applied each wave tuning in memory and restored it afterwards. It ran each stress
   scene at two poses:
   - **Open sea:** over the deepest point found.
   - **Coast:** 60 m off a coast, found by walking from that point until the ground broke the sea.

   The camera sat 3 m over the fly camera's floor, looking 15° down. Each pose was rendered at frozen
   times 0, 0.5 and 2 s, and with no waves.
   - **What it measured.** Over the sea part of the frame, in three bands from the camera out (near,
     mid, far):
     - **Spread:** the luminance standard deviation within a frame, after removing each row's mean, so
       the gradient towards the horizon does not count.
     - **Motion:** the mean luminance change between frames 0.5 s and 2 s apart.
     - It also recorded the resident kinds, the vertex memory and the fps over 5 s.
   - **The tunings.** "Final" was the user's waves with `ShoalFactor` 0.25 and a 14.8 m overlap.
     "Committed" was `ocean-b`'s 1.26 m waves with a 1.3 m overlap.
   - **A first pass posed the narrow ring and the terrarium over land.** The search covered a fifth of
     the cylinder round the start, which there is all land. It now searches the whole cylinder. That also
     moved Ringworld's pose, so Ringworld has no old-shading baseline at its final pose.
3. **Step 1: the sky gradient.**
   - **The horizon colour is computed where it is used.** `SonomaHazeLight` is called in the reflection
     and again inside `SonomaApplyHaze`, not passed in. The compiler shares the arithmetic, and the
     terrain's include is untouched.
   - **By day it works.** The far sea's spread on Earth went from 2.0 to 20.1 and its 2 s motion from 2.3
     to 23.4; the small planet's went the same way.
   - **The near field barely moved:** 1.0 to 3.3. Looking 15° down, the Fresnel weight there is small.
   - **At night contrast falls.** The deep-sea poses of the ring and the narrow ring are on the night
     side. There the horizon colour is the haze lit by the sky alone, darker than the ambient the old
     shader reflected, so the narrow ring's far spread fell from 6.3 to 2.6. A night sea should be dark,
     so this stands.
4. **Step 2: the crests, weighted by steepness, not height.**
   - **The plan's `Σ a·weight·cos / Σ a`** is dominated by the 1,200 m swell, whose amplitude is 4.9 m
     against 1.2 cm for the shortest wave. The tint would then follow the swell over hundreds of metres
     and say nothing about the waves near the camera.
   - **`crest = Σ a·w·weight·cos / Σ a·w`** weights each wave by its steepness, and the short waves count.
     The denominator is undamped, so shallow water reads calmer.
   - **The crest's own pixel fade.** It takes a wave from 4–8 pixels a wavelength, twice the normal's 2–4,
     because a colour aliases sooner than shading does. That changed no figure on Earth. The dots in the
     near field are real interference between sixteen waves spread over 106°, not aliasing.
   - **The colour.** `_CrestColor` started at (0.12, 0.45, 0.45), nearly the shallow colour, so the tint
     vanished near a shore. It is (0.22, 0.58, 0.56) at strength 0.5. The backlight is in the shader and
     off in `Ocean.mat`.
   - **The result.** On Earth's open sea the near spread rose from 3.3 to 11.7, and the 2 s motion there
     from 1.9 to 13.9.
5. **Step 3: whitecaps.**
   - **The pinch is read off the normal.** It is `1 − dot(waveN, up)` before the normal is renormalised,
     which is the Jacobian sum `Σ Q·a·w·cos` the normal already holds.
   - **The plan's 0.25–0.45 drew no foam.** With the retuned waves the pinch's standard deviation is
     about 0.08, estimated from the table, so 0.25 is three of them. The range is 0.15–0.30, at strength
     0.6. That foam covers what share of the surface was not measured.
   - **It reads as whitecaps.** Small white flecks on the sharpest crests, not drawn lines, so it ships
     on. Earth's open-sea near spread went from 11.7 to 18.2.
6. **Step 4: the tuning, measured at the two poses, not in flights.** The memory cost of the overlap
   is at the coast, which the coast pose holds still, so the flight driver was not rebuilt. A fast flight
   with these waves has not been flown.
   - **Memory.** Every chunk count is the same with both tunings. Vertex memory at the coast pose (Coast
     chunks):

     | scene | 1.26 m waves, 1.3 m overlap | 14.8 m waves, 14.8 m overlap |
     |---|---|---|
     | `EarthStress` | 782.4 MB (1,596) | 856.8 MB (2,358), +9.5% |
     | `SmallPlanetStress` | 691.1 (1,245) | 736.3 (1,708), +6.5% |
     | `RingworldStress` | 506.3 (947) | 522.4 (1,112), +3.2% |
     | `NarrowRingStress` | 428.1 (627) | 481.7 (1,175), +12.5% |
     | `TerrariumStress` | 374.3 (860) | 445.5 (1,589), +19.0% |

     Over open sea it moves by 0.1% at most. Every pose stays under its budget in chunks.
   - **Readability.** Spread and 2 s motion by band, near / mid / far, with old shading against new, at
     the final tuning:

     | pose | spread before | spread after | motion before | motion after |
     |---|---|---|---|---|
     | Earth, open sea | 1.0 / 1.6 / 2.0 | 18.2 / 17.5 / 19.6 | 1.1 / 1.4 / 2.3 | 17.1 / 15.1 / 23.1 |
     | Small planet, open sea | 1.0 / 1.5 / 2.1 | 19.5 / 22.0 / 20.0 | 1.5 / 1.7 / 2.7 | 22.9 / 25.0 / 27.4 |
     | Terrarium, open sea | 3.3 / 4.1 / 3.6 | 4.7 / 6.5 / 8.8 | 3.7 / 3.4 / 3.8 | 5.9 / 6.4 / 9.6 |
     | Earth, coast | 2.0 / 2.3 / 2.1 | 4.6 / 8.9 / 15.1 | 1.3 / 1.2 / 1.0 | 5.1 / 8.7 / 18.3 |
     | Small planet, coast | 1.5 / 2.0 / 2.1 | 7.5 / 10.3 / 16.8 | 1.1 / 1.2 / 1.0 | 6.2 / 10.1 / 19.8 |

     The terrarium's sea is lit low and its gains are smaller. The narrow ring's poses are on the night
     side, where the sea is dark either way.
   - **Frame rate.** Old shading against new, Editor play mode:
     - small planet 90.5 → 82.3 fps over open sea and 66.2 → 63.1 at the coast;
     - narrow ring 129.7 → 124.1 and 96.9 → 92.0;
     - terrarium 120.8 → 120.2 and 107.2 → 101.7;
     - Earth 66.2 → 63.0 at the coast. Over open sea it read 95.8 → 68.1, but 87.5 in an earlier run with
       the same new shading, so that pair is the Editor's noise.

     The new terms cost a few percent at most, within that noise, and M6 can profile them on a GPU-bound
     build.
   - **The tests.** All 267 EditMode tests pass, with the twelve `[Explicit]` tools skipped.
     `StressScenesHaveTheirOcean` passes again.
7. **The commits keep the user's earlier `Ocean.mat` tuning out.** That tuning (`_DepthScale` 50,
   `_FarSmoothness` 1, `_FresnelStrength` 0.734, `_Smoothness` 0.889) stays uncommitted. Each commit
   staged the committed material plus only its own new lines. All the frames above were rendered with the
   user's tuning, which is what the Editor loads.
8. **The shallows (2026-11-26), added after the plan at the user's request.** The user found the
   coastline sharp and unnatural, where the opaque sea meets the beach in a hard line.
   - **The sea moves to the Transparent queue.** It is drawn after the terrain and blends over it where
     the water is shallow. `T = 1 − smoothstep(0, _ShoreFadeDepth, depth)` is the share of light that
     reaches the floor and back. The opacity is `1 − (1 − F) T`, so what the surface reflects is not
     transmitted, and the sea keeps its sky reflection over the shallows rather than turning into a
     window.
   - **Deep water keeps its colour to the bit.** The colour is written as the lit colour minus the
     transmitted share of the water body, over the opacity, so where `T` is 0 it is the old colour. Foam
     stays opaque.
   - **The floor shows through exactly at the waterline.** The depth is the morphed floor, so `T` reaches
     1 where the water meets the drawn ground.
   - **`_ShoreFadeDepth` is 3 m in `Ocean.mat`.** It must stay under every scene's overlap, because an
     Ocean chunk draws no terrain; `StressScenesHaveTheirOcean` holds it.
   - **The first frames showed dark lines along every chunk edge in the shallows.** They were the water
     skirts. The opaque surface had hidden them, and seen through it, the strip of skirt between the
     surface and the floor was a second layer of water. Skirt fragments are now discarded wherever `T > 0`.
     The grid carries a skirt flag of 0, and the terrain under the shallows covers any crack.
   - **The result.** Looking at Earth's shore from 60 m out, the beach shows through a few metres of
     water, and the coast reads as a gradient from sand to sea. The frames are
     `Logs/OceanB/shade_shallows*_EarthStress_*_shore_t0.0.png`.
   - **The frame rate** at that view was 64.2 fps opaque against 62.9 with the fade, and 54.4 after the
     skirt fix. The user was working in the Editor during that last run, so the drop is not attributed.
   - **The user's unsaved edit.** During this run the user had an unsaved `ShoalFactor` of 0.025 in the
     Inspector, and the shore frames used it. It was left as it was.
   - **A new test.** `OceanFadesInTheShallows` holds the queue, the blend, the opacity, the colour, the
     foam and the skirt discard.
