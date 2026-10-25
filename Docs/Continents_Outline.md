# Continents from a Voronoi map: feasibility and outline

**Source:** `Docs/VoronoiContinentConcept.md` (the idea). This document assesses it against the
current code and outlines how it would fit. It is a high-level outline, not an implementation plan;
an exact-steps plan in the style of the `Docs/M*_Plan.md` files would follow if it goes ahead.
**Written:** 2026-10-21, with M5a and M5b merged and M5c (erosion) and the ocean planned.
**Followed by:** `Docs/Continents_Plan.md`, the implementation plan (2026-10-23). Its amendments record
what the build measured, on all four planet and ring scenes (done 2026-10-24).

---

## Verdict

**Feasible, and a good fit.** A continent map is global, coarse and computed once, which is exactly
what the macro maps exist for ("non-local algorithms belong in the maps, run once and coarsely").
Nothing in it touches the per-chunk contract: height stays a pure function of a surface point and a
depth, same-depth neighbours still agree to the bit, and a fully morphed child is still its parent.

It also fixes a real problem the sea-level measurement found (`Docs/Ocean_Plan.md`, Q2). Today the
**detail noise's coarsest octaves draw the coastlines**, not the macro layer. On Earth the height has
a standard deviation of 6.0 km against the macro base height's 1.2 km, and the two correlate at only
0.24. So climate and biomes, which read the macro maps, do not know where the sea is, and land is
wherever octaves of 1,300 km and longer happen to peak. Moving the continents into the macro layer
puts land, climate and erosion's drainage on one map.

The concept needs six changes to fit this project. None is a reason not to do it:

1. **Hard cell edges become a continuous field.** Thresholding cells gives a step in the base height
   at every land–ocean cell edge. A step breaks C1's band limit (Catmull-Rom rings on it), and a cliff
   kilometres high spends the LOD relief allowance. The concept already asks for a bias rather than a
   hard mask; the field below is how to make the bias continuous.
2. **A 2D unit square becomes the real surface.** Seeds are placed on the sphere, the cylinder or the
   plane, and distances are measured between 3D surface points. That handles a cube sphere's faces
   and a cylinder's wrap with no special cases.
3. **Per-cell random noise becomes coherent noise and a domain warp.** The concept's step 5 adds
   uniform random noise per micro cell, so neighbouring cells are uncorrelated. Fractal noise at the
   cell's seed, plus warping the point before the nearest-seed lookup, gives coastlines that are
   neither polygonal nor speckled.
4. **A fixed threshold becomes a target land fraction.** The threshold is solved at startup so that
   the land cells cover a configured share of the surface by area. "30% land" becomes a parameter
   rather than a sea level to re-measure.
5. **"Replace the coarsest level of heightmap generation"** means dropping the detail octaves longer
   than a cutoff, about 1,000 km on Earth. Their place is taken by the continent field in the base
   height.
6. **All randomness is seeded and ordered.** The BFS's random decay depends on visit order, so it runs
   sequentially, in a fixed order, from a seeded generator. Same seed, same world.

**The main risk is tuning, not structure.** After the five-fold relief, the octaves that remain below
the cutoff are still large: on Earth the 650 km octave alone is 4.8 km tall and the 325 km one 3.4 km.
If the continent field's land–ocean offset is small beside them, the remaining noise floods continents
and raises islands everywhere, and the map is lost. The levers are the field's amplitude, where the
cutoff sits, and damping the low band over deep ocean. All three can be measured offline with the
land-fraction probe before anything is built into a scene.

---

## Prototype results (2026-10-21)

An offline prototype ran Stages A and B on `EarthStress` and `RingworldStress` against the scenes' real
height function, with no scene or asset changed. The detail noise was evaluated octave by octave with
each scene's band gains, so any cutoff could be applied, and the sum reproduced
`TerrainHeightFunction.HeightAtPoint` to 0.5 mm (float rounding) at 200 random points. Settings: 30
macro cells, 5,000 micro cells, 4 continents, `LandFraction` 0.3, a warp of half a micro cell, profile
width half a micro cell, the old macro fBm kept at 0.3. Statistics are over 100,000 area-uniform points.
The field was evaluated directly, not through the texel pyramid, so Stage C's band-limiting is not in
these numbers.

**Agreement** is the share of area where the final terrain is on the same side of sea level as the
continent map. **Flooded** is the share of the map's land below the sea, **islands** the share of its
ocean above it. Amplitudes are a share of `HeightScale` (Earth 20 km, ring 10 km). The ocean low-band
damping (×0.5) is shown where it mattered.

| scene | cutoff | land / ocean amplitude | agreement | final land | flooded | islands | median land / ocean |
|---|---|---|---|---|---|---|---|
| Earth | none | 1 / 4 km | 71% | 23% | 60% | 16% | +3.1 / −6.6 km |
| Earth | none | 3 / 9 km | 82% | 20% | 47% | 6% | +3.5 / −9.8 km |
| Earth | 1,000 km | 1 / 4 km | 88% | 24% | 30% | 4% | +1.4 / −3.2 km |
| Earth | **1,000 km** | **2 / 6 km** | **94.5%** | **26.5%** | **15%** | **1.3%** | **+2.1 / −5.0 km** |
| Earth | 500 km | 2 / 6 km | 96.5% | 28% | 10% | 0.9% | +1.9 / −5.1 km |
| Earth | 250 km | 3 / 9 km | 98.6% | 29% | 4% | 0.4% | +3.0 / −7.9 km |
| ring | none | 1 / 3 km | 87% (91% damped) | 32% | 18% | 11% | +1.9 / −4.0 km |
| ring | 1,000 km | 1 / 3 km | 86% (90% damped) | 31% | 22% | 11% | +1.4 / −2.7 km |
| ring | 500 km | 1 / 3 km | 91% (92% damped) | 28% | 18% | 5% | +1.3 / −2.6 km |
| ring | 250 km | 1 / 3 km | 94% | 28.7% | 12% | 3% | +1.3 / −2.6 km |
| ring | 500 km | 1.5 / 4.5 km | 95.5% | 28% | 11% | 1.6% | +1.9 / −3.9 km |
| ring | 250 km | 1.5 / 4.5 km | 97% | 28.6% | 7% | 0.9% | +1.8 / −4.0 km |

What it shows:

1. **The cutoff is necessary.** With every octave kept, half of Earth's continents are under water at
   any amplitude tried. The coarse octaves are what draw today's coastlines, and they overrule a
   continent map unless they go.
2. **With a cutoff, continents survive.** On Earth a 1,000 km cutoff and 2 / 6 km reach 94.5%, with
   about 1% of the ocean as islands. The 15% flooding reads as inland seas and basins inside the
   continents, not lost coastline. 1 / 4 km gives the most Earth-like heights (+1.4 / −3.2 km medians) at
   88%.
3. **The final land fraction comes out low** (24–29% for a 30% map), because flooding outweighs
   islands. Solve the threshold against the final terrain rather than the map, or raise `LandFraction`
   by the measured deficit.
4. **The ring is harder.** Its octave 0 is already 1,251 km, so "none" and 2,000 km are the same, and
   dropping octave 0 alone made agreement slightly worse. That is chance, not a trend: octave 0 has
   about eight cells across the whole ring, and with this seed its highs happen to sit under the
   continents. That is itself an argument for the cutoff, since keeping the coarse octaves leaves the
   result at the mercy of where they happen to fall. The ring needs a lower cutoff (250 km) or larger
   amplitudes for 94–96%. At 1 / 3 km its ocean keeps a scatter of small islands (3–5%).
5. **Ocean damping matters only when the cutoff is too high.** With a cutoff it moves agreement by a
   point or less. It can stay optional.

What the images show (in the session scratchpad, `continents/out` and `continents/out1000`):

- **The idea reads as intended:** four continents with peninsulas, a few islands, coastlines from the
  remaining octaves layered on the map's shape. The ring wraps cleanly at its seam.
- **A cellular texture inside land and ocean.** `(dO − dL) / 2` does not saturate far from the coast:
  inside a land cell `dL` still varies by up to half a micro cell. So the profile carries an 8% ripple
  at the micro-cell scale, about 160 m on land and 480 m on the ocean floor at 2 / 6 km. The detail
  noise hides it in the final terrain. It should still be fixed in a real build: measure the exact
  distance to the land–ocean Voronoi border (the bisector form), or keep the profile width well inside
  the distance cap.
- **Coastlines follow the micro cells** at their 320 km scale on Earth. A warp of half a cell softens
  the polygons but does not hide them in the map. In the final terrain the remaining octaves draw their
  own coastline over it, so this is a taste question, settled by a larger warp if needed.
- **Interiors flatten as the cutoff drops.** At 500 km Earth's continents are one plateau of uniform
  hills. Today's continent-scale highlands and basins come from exactly the octaves the cutoff removes.
  At 1,000 km there is more variety (basins, inland seas). If more is wanted: the old macro fBm at a
  higher gain on land only, a larger BFS-elevation term, or the coarse octaves kept at a reduced gain
  rather than dropped.

Not measured by the prototype: the LOD relief cost of the continental slope (6 km over about 160 km on
Earth, a slope up to about 0.04 at the coast), skirts, the band-limited pyramid (Stage C), and Burst
cost. Stage A took 0.7–1.0 s managed: brute-force blue noise, 400,000 adjacency samples and 400,000
area samples. That is startup time, and would need a bucket grid and fewer samples, or caching, in a
real build.

**Follow-up (2026-10-23), the ripple fix.** Two changes, both now in `Docs/Continents_Plan.md` (K2):
- The squared distance `(dO² − dL²) / 2D` removes the cell ripple entirely: inside a region the capped
  term dominates, and the profile is exactly flat.
- An odd quintic `t (15 − 10t² + 3t⁴) / 8` replaces `tanh`. It is a polynomial, so a Burst build can
  match it to the bit.

Together they also raise agreement: Earth at 1,000 km and 2 / 6 km goes from 94.5% to 95.6%, and the
ring at 250 km and 1.5 / 4.5 km from 97.2% to 97.9%. The even smootherstep was tried too and lost 1.8
points on Earth, because its zero slope at the coast leaves a flat band at sea level.

**As built (2026-10-24), through the real height function at `MaxDepth`** (`Docs/Continents_Plan.md`,
amendments): Earth 96% and the ring 93.7%, against the prototype's 95.6% and 97.9%. Neither the small
planet nor the narrow ring was prototyped. The small planet reached 95.5% at 1,000 / 3,000 m with a
250 km cutoff. The narrow ring reached 92.5% at 600 / 1,800 m, and only with a 150 km cutoff: without
one it reached 86%.

**Recommended starting point for a plan:** a 1,000 km cutoff on the planets and 250 km on the ring, land
and ocean amplitudes of 0.10 / 0.30 of `HeightScale` on Earth and 0.15 / 0.45 on the ring, the threshold
solved against the final terrain, and the border-distance fix.

---

## How it fits

### What changes, by layer

| layer | today | with continents |
|---|---|---|
| detail noise (`TerrainHeightFunction.Height`) | octaves 0 … `MaxOctave(d)` | octaves from the cutoff to `MaxOctave(d)` |
| macro base height (channel x) | 3 km of fBm at the same scales as detail octaves 0–6 | the continent field, plus the old fBm (smaller) for interior variety |
| climate (channels y, z) | lapse rate from base height above 0 | lapse from base height above sea level; optionally moisture from distance to the sea |
| ruggedness (channel w) | noise | noise; optionally raised where two continents meet |
| sea level | measured per scene for a target land fraction | the field's zero; land fraction is a parameter |

### Stage A — the cell maps (startup, managed, small)

This runs once, before the macro build, on the main thread, in double. It is a few thousand points, so
cost is not a concern; determinism is.

1. **Macro seeds.** About 20–50 points spread evenly over the surface: random directions on a sphere,
   uniform angle and length on a cylinder, uniform `(x, z)` on a plane. Use a blue-noise placement
   (best-candidate or Poisson-disc) rather than plain random, so no cell is a sliver.
2. **Adjacency.** Rasterise the nearest-macro-seed id over the top macro level's texels and record
   every pair of different ids that touch. That is robust on every topology, wraps included, and needs
   no Delaunay triangulation on a sphere.
3. **Roll for land, and the BFS.** As the concept describes: K continent centres at elevation 1, then a
   breadth-first spread with a seeded random decay per step, the larger value winning where two meet.
   The queue order is fixed and ties go to the lower index.
4. **Micro seeds.** About 1,000–10,000 points, blue-noise again. Each takes its parent macro cell's
   elevation, plus fractal noise sampled at the seed's own point. Each is land or ocean by a
   threshold. The threshold is the area-weighted quantile that makes the land share equal the
   configured `LandFraction`, with areas from the same rasterisation.
5. **Tables for the job:** micro seed positions, land flags, parent elevations, and a 3D bucket grid
   over them, as `NativeArray`s.

**Counts per scene.** Each scene's top macro level has about 0.4–0.7 million texels, so about 5,000
micro cells puts every micro cell 8–12 texels across in all five stress scenes. That is comfortably
inside C1's four-texel rule. The concept's upper count, 10,000, is about 5–6 texels on Earth, which is
near the limit.

| scene | surface area | macro cell, at 30 seeds | micro cell, at 5,000 seeds | top-level texel |
|---|---|---|---|---|
| Earth | 5.1 × 10⁸ km² | about 4,100 km | about 320 km | about 40 km |
| small planet | 3.2 × 10⁷ km² | about 1,030 km | about 80 km | about 9.8 km |
| ring | 1.25 × 10⁷ km² | about 650 km | about 50 km | about 4.9 km |
| narrow ring | 1.4 × 10⁶ km² | about 220 km | about 17 km | about 1.5 km |
| terrarium | 1,900 km² | about 8 km | about 600 m | about 60 m |

In the terrarium "continents" are islands and lakes a few hundred metres to kilometres across; it may
be better left without a continent map.

### Stage B — the continent field (inside `MacroBuildJob`, per texel)

The field has to be continuous, and a pure function of the texel's surface point and the Stage A
tables, so both quads of a shared edge compute it identically before C2's edge copies.

1. **Warp the point:** `p' = p + warp(p)`, an fBm displacement of about half a micro cell. This is
   the concept's step 5, and what stops coastlines following polygon edges.
2. **Distance to each class.** `dL` is the distance from `p'` to the nearest *land* micro seed and
   `dO` to the nearest *ocean* seed, found through the bucket grid (27 buckets).
3. **Signed coast distance:** `s = (dO − dL) / 2`. It is positive on land, exactly zero on the
   Voronoi edge between a land cell and an ocean cell, and continuous everywhere: a difference of two
   distance functions has no steps. It replaces the concept's hard threshold with the "bias" it asks
   for.
4. **Profile.** The base height is a smooth function of `s`: up from the coast to a land plateau,
   down across a shelf and a continental slope to an abyssal depth. Scale the land side by the parent
   macro cell's BFS elevation, blended smoothly between macro cells (the blend Biomes uses for
   climate works), so continent interiors stand higher than their margins. Then add the old macro fBm
   at reduced amplitude.

On Earth the top level holds about 415,000 texels, and the nearest-seed searches are a few dozen
distance checks each. Next to the build's 188–224 ms this is tens of milliseconds, to be measured.

### Stage C — band-limit and the pyramid

The field is continuous but has creases (where the nearest seed of one class changes) and a sharp
shelf break. So it is computed at the top level only, lightly low-pass filtered there, and the lower
levels are **derived by filtering down**, with halo fill across root-quad edges and the edge copies
after each level.

That is exactly the machinery M5c's erosion needs (its E4). **Build it here, once, and let M5c use
it.** It also changes the ocean plan's C8 bound in the same way M5c does: the macro term of
`Residual(d)` can no longer assume a coarser level is the top level with octaves left out.

### Stage D — the height function

- **`HeightParams` gains a continental cutoff** (metres; detail octaves longer than it are dropped),
  turned into a first octave by exponent, like the band edges. Dropping the same octaves at every
  depth keeps the strict-prefix property the geomorph relies on. A cutoff of 0 takes the existing
  `Fbm` path, so `SampleScene` and every scene without continents stay bit-identical.
  - A gain instead of a hard drop (keep a fraction of the coarse octaves) is a one-field
    generalisation, stored relative to one as C5 requires.
- **Damping the low band over deep ocean** (optional): a continentalness term in `Biomes.Modulate`'s
  low-band gain gives smoother abyssal plains, and is one of the tuning levers.
- **`MaxAbsHeight`** sums the detail octaves from the cutoff, and bounds the base height by the
  profile's extremes plus the reduced fBm, times `MacroSampler.MaxOvershoot`.

### Stage E — configuration and tools

- A `ContinentSettings` asset referenced from `MacroLayerSettings`; null means no continents and M5b's
  maps to the bit. Fields: seed counts (or cell sizes), continent centres, the BFS decay range,
  `LandFraction`, the warp, the profile heights and widths, and the detail cutoff.
- `MacroMapExport` writes `<scene>_continents.png` (the land and ocean cells, the macro cells and their
  BFS elevation) and `<scene>_coast.png` (the signed distance). The land-fraction probe gains a second
  number: the share of area where the final height's side of sea level agrees with the continent
  map's. That **agreement** is the tuning metric for the main risk above; it should be high (around
  90%) but not 100%, or the coastline has no detail of its own.

### Tests, in outline

- Determinism: two builds give the same tables and texels; the Burst build equals a managed reference.
- The land fraction comes out at `LandFraction` (by area, to a stated tolerance).
- The signed distance is continuous: no step larger than its Lipschitz bound between neighbouring
  texels.
- Seams: `MacroEdgesAgreeAcrossEveryLink` on maps with continents.
- Identity: no continent asset gives M5b's maps; a cutoff of 0 gives M5b's heights.
- Geomorph: `ChildAtFullMorphEqualsParent` with continents and a cutoff.
- Re-measured and pinned per scene: `C` (dropping coarse octaves lowers it a little), `C_m` (continental
  slopes raise it), the skirt shares, the working sets and `MaxAbsHeight`. `C_m` is the one to watch:
  the terrarium and the narrow ring have the least margin in `MacroTests.CombinedMargin`.

---

## Where it goes in the order

**Before M5c.** Erosion erodes the sum of the base height and the coarse detail octaves, and drains to
sea level, so it should run on the continents, not on terrain about to be replaced. The pyramid
derivation it needs is built here. The order recorded in both plans would become:

1. **Continents** (this outline), which also sets each scene's land fraction, and so the sea level.
2. M5c-1, macro erosion, with `BaseLevel` at sea level.
3. M5c-2, the gully term.
4. `ocean-a`, `ocean-b`.
5. M6.
6. `ocean-c`.

The 30% sea levels recorded on 2026-10-21 (ocean plan Q2, M5c D1) stand until then. With continents
the field's zero is the coast, so `SeaLevel` would be 0 and `LandFraction` 0.3.

## Decisions for the user

- **Go or no go.** The offline prototype (above) found the idea works once the coarse octaves are cut,
  at 94–96% agreement on Earth and the ring.
- **The detail cutoff**, about 1,000 km on Earth, and whether the coarse octaves are dropped or kept
  at a reduced gain.
- **Which scenes get continents.** The terrarium probably not.
- **Optional extensions:** moisture from distance to the sea (dry interiors), and mountain belts
  where continents meet (ruggedness raised along macro cell boundaries between two continents'
  spreads).

## Out of scope

- Plate tectonics proper (plate motion, subduction, uplift histories). The macro cells are templates,
  not plates.
- Rivers and lakes as features (M5c's wetness is their source).
- Changing `SampleScene`, which has no macro layer.
