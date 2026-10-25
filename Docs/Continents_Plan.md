# Continents — a Voronoi Continent Map in the Macro Layer: Exact Implementation Steps

**Audience:** a coding agent working in `G:\UnityProjects\TerrainGen` with no prior context on this project.
**Parent documents:** `Docs/VoronoiContinentConcept.md` (the user's idea), `Docs/Continents_Outline.md`
(its assessment and the offline prototype's results), and `Docs/M5_MacroLayer_Plan.md` (the macro layer
this builds on; its C1, C2 and C5 are assumed here).
**Prerequisite:** main at `ce56f27` or later, with M5a, M5b and `NarrowRingStress` merged, and every
EditMode test green. Local changes in `ProjectSettings/` and `Assets/Settings/` (the URP assets and
`Build Profiles/`) may be present; they are the user's, so leave them alone.
**Where it sits:** before M5c (erosion) and the ocean. The order in `Docs/Ocean_Plan.md` Q10 becomes:
continents, M5c-1, M5c-2, `ocean-a`, `ocean-b`, M6, `ocean-c`.

Today the stress scenes' coastlines are drawn by the **detail noise's coarsest octaves**, not by the
macro layer. Measured over 200,000 points on Earth, the height has a standard deviation of 6.0 km
against the macro base height's 1.2 km, and the two correlate at 0.24 (`Docs/Ocean_Plan.md`, Q2). So
climate and biomes, which read the macro maps, do not know where the sea is. This plan moves the
continents into the macro layer:

- a **Voronoi continent map** is built once at startup;
- it becomes a smooth **continent height** in the macro maps' base-height channel;
- the detail octaves longer than a **cutoff** are dropped, since the continent map now carries those
  scales.

The rest of the detail noise, the biomes, and later erosion all sit on top.

An offline prototype ran this against the real height function of `EarthStress` and `RingworldStress`
(`Docs/Continents_Outline.md`, "Prototype results", and the last two rows of the table in K2 below). With
the field defined as in K2, the final terrain sits on the same side of sea level as the continent map
over 95.6% of Earth's area and 97.9% of the ring's.

Everything that made M2–M5 work has to survive this:

- Height stays a pure function of a surface point and a depth.
- Same-depth neighbours still agree to the bit, and macro samples still agree to the bit across every
  root-quad link.
- A fully morphed child is still its parent.
- `SampleScene`, and every scene without a continent map, stays bit-identical.

---

## Ground rules

1. **Nothing changes without a continent map.** `SampleScene` has no macro layer. The terrarium keeps
   its macro layer and gets no continent map (decision D2). Both, and any scene whose
   `MacroLayerSettings.Continents` is null, must build M5b's maps and heights to the bit. The new
   fields all default to the identity (C5's rule: gains relative to one, flags off by default).
2. **Three landable branches.**
   - **`macro-pyramid`** (Task 1): halo fill, the binomial filter and the filter-down that derives a
     level from the one above, on any scalar channel in `MacroLayout`'s index space. Nothing in a scene
     uses it yet. It is the machinery `Docs/M5_MacroLayer_Plan.md`'s M5c E4 calls for, so M5c will
     reuse it rather than build it.
   - **`continents-a`** (Tasks 2–8): the detail cutoff, the continent map, the field, the build
     pipeline, configuration, tools, and continents on `EarthStress` and `RingworldStress`, every
     pinned figure re-measured for those two.
   - **`continents-b`** (Task 9): `SmallPlanetStress` and `NarrowRingStress`, calibrated and re-pinned,
     and the documentation.
3. **Nothing new per frame.** All of it is startup work inside `MacroMaps.Build`. The target is under
   200 ms on Earth in a Burst build, logged by `TerrainRoot` beside the macro build time. If a design
   choice here adds main-thread work per frame, it is the wrong choice.
4. **Deterministic, and the Burst build equals the managed one to the bit.** That is the standard
   `MacroBuildJob` meets (`MacroMapsTests.MacroJobMatchesTheManagedBuilder`). So the decision-making
   arithmetic (seeds, distances, the profile, the elevation blend) uses only `+ − × ÷` and `sqrt`, which
   IEEE rounds identically everywhere, plus `LatticeNoise`, which is polynomial. No `exp`, `tanh`, `log`,
   `sin` or `cos` in new code. Every tie goes to the lower index. No iteration over a hash container
   decides anything.
5. **Measure before concluding.** When a figure here disagrees with the Editor, the Editor is right;
   record it under "Amendments".
6. **The core stays UnityEngine-free**, so it runs in the out-of-Editor harness: `ContinentMap`,
   `ContinentField` and `MacroPyramid` live in `Core/Generation/`, with no `UnityEngine`,
   `Unity.Collections` or `Unity.Jobs` import. Their Burst jobs live in separate files, as
   `MacroGenerator` and `MacroMaps` are split today. Reads go through a generic struct accessor, as
   `IMacroTexels` does, so managed arrays and `NativeArray`s share one code path.

---

## Conventions and settled decisions

### K1 — The pipeline, inside `MacroMaps.Build`

The continent map is an input to the macro generator, not a pass over its output. That way the climate
fields are computed from the final base height, and the existing edge copies still run last:

1. **Stage A, the continent map** (`ContinentMap.Build`; mostly managed, the per-texel scans in Burst):
   seeds, the macro graph, the BFS, micro scores and the land threshold (K3).
2. **Stage B, the field at the top level** (`ContinentFieldJob`, Burst, parallel): the continent height
   at every stored texel of the top level, borders included, into a temporary
   `NativeArray<float> continent` in `MacroLayout` index space (K2).
3. **Stage C, band-limit and derive** (`MacroPyramid`, Task 1):
   1. filter the top level once;
   2. apply the edge copies for that level, then halo-fill its border;
   3. then for each level from the top down, filter, decimate into the level below, apply the edge
      copies there and halo-fill (K5).
4. **The generator** (`MacroBuildJob`, extended): each texel's base height becomes
   `BaseHeightScale × fBm + continent[index]`, and the temperature lapse is computed from that sum.
5. **The edge copies**, as today.
6. **Dispose** the temporary arrays, in a `finally`, as `MacroMaps.Build` already does for its roots
   array.

The continent array is temporary, one float per texel over every level: 2.3 MB on Earth and 4.0 MB on
the narrow ring, plus a scratch copy of the top level for the filter, all freed before the build
returns.

### K2 — The field: a squared signed distance, a polynomial profile

At a texel's surface point `p`:

```
D      = micro-cell spacing = sqrt(surface area / MicroCells)
p'     = p + WarpAmplitude · D · (Fbm(x, 3, 0.5, 2, seedA), Fbm(x, …, seedB), Fbm(x, …, seedC)),
         x = p / (WarpWavelength · D)
dL, dO = distance from p' to the nearest land seed and to the nearest ocean seed, each capped at 2D
s      = (dO² − dL²) / (2D)                       positive on land, zero on the coast
t      = min(|s| / CoastWidth, 1)
f      = t (15 − 10 t² + 3 t⁴) / 8                odd quintic: f(0) = 0, f'(0) = 15/8, flat at t = 1
E      = Σ wᵢ eᵢ / Σ wᵢ,   wᵢ = (1 − (|p − mᵢ| / R)²)²  for |p − mᵢ| < R      (macro seeds mᵢ, BFS elevations eᵢ)
height = s ≥ 0 ?  LandHeight · (1 − InteriorRise/2 + InteriorRise · E) · f
               : −OceanDepth · f
```

Why each choice. The prototype's numbers for the first and last are in the table below.

- **Squared, not linear.** The concept's threshold gives a step. The linear form `(dO − dL)/2` is
  continuous, but it does not saturate inside a region: `dL` still swings by about half a cell inside a
  land cell. So it carried an 8% cell-shaped ripple on every plateau and ocean floor. The squared form
  is exactly the distance to the bisector of an adjacent land–ocean pair `D` apart. Inside a region the
  capped `dO² = 4D²` dominates, so `s ≥ (4D² − dL²)/2D`, which reaches `CoastWidth = D` whenever
  `dL ≤ 1.41 D`. Stage A checks that bound (K3). Past it the profile is exactly flat, with no ripple
  at all.
- **The odd quintic, not `tanh`.** It is a polynomial, so Burst matches it to the bit (ground rule 4),
  and it is exactly flat at `|s| ≥ CoastWidth`. Its slope at the coast is nonzero. The prototype first
  tried the even smootherstep, whose zero slope at the coast left a flat band at sea level that the
  noise flooded or raised at random: 1.8 points less agreement on Earth and twice the islands.
- **A polynomial blend kernel for `E`, not a Gaussian:** the same reason. Continuity needs `Σw > 0`
  everywhere, which holds when `R` exceeds the largest distance from any point to its nearest macro
  seed. Stage A measures that **coverage radius** over the top-level texels, and `R` is 1.5 times it.
- **The warp** breaks the micro cells' polygon edges (the concept's step 5). Three `LatticeNoise.Fbm`
  channels, each seeded a stride apart, as `MacroGenerator` seeds its channels.
- **The land side scales with `E`**, so continent interiors near a centre stand higher than their
  margins (`InteriorRise` 0.8 makes it 0.6–1.4 times `LandHeight`).

Measured with the prototype, the odd quintic over `CoastWidth = D`, `InteriorRise` 0.8, the old macro
fBm at 0.3:

| scene | cutoff | land / ocean | agreement | final land (map 30%) | flooded | islands | median land / ocean |
|---|---|---|---|---|---|---|---|
| Earth | 1,000 km | 1 / 4 km | 90.0% | 23.7% | 27% | 2.5% | +1.5 / −3.6 km |
| Earth | 1,000 km | 2 / 6 km | 95.6% | 27.2% | 12% | 1.0% | +2.3 / −5.6 km |
| Earth | 500 km | 2 / 6 km | 97.3% | 28.4% | 7.5% | 0.7% | +2.2 / −5.8 km |
| ring | 250 km | 1 / 3 km | 95.1% | 28.6% | 11% | 2.5% | +1.4 / −2.9 km |
| ring | 250 km | 1.5 / 4.5 km | 97.9% | 28.9% | 5.4% | 0.7% | +2.0 / −4.4 km |
| *linear `s`, tanh* | | | | | | | |
| Earth | 1,000 km | 2 / 6 km | 94.5% | 26.5% | 15% | 1.3% | +2.1 / −5.0 km |
| ring | 250 km | 1.5 / 4.5 km | 97.2% | 28.6% | 7% | 0.9% | +1.8 / −4.0 km |

The field is a pure function of `p` and Stage A's tables. Both quads of a shared edge therefore compute
it at points 1.3 nm apart, and the edge copies (K1, step 5) make the stored texels bit-identical.

### K3 — The continent map: deterministic, on the surface, from the texel grid

**Seeds.**
- Macro seeds (`MacroCells`, default 30) and micro seeds (`MicroCells`, default 5,000), each placed by
  best-candidate blue noise: 8 candidates, keep the farthest from those placed. The nearest-seed
  queries use the bucket grid below, not brute force; the prototype's brute force took 1 s.
- Points come from `Unity.Mathematics.Random`, seeded from `ContinentSettings.Seed`:
  - **Sphere:** rejection sampling in the cube `[−1, 1]³`, keeping `10⁻⁶ < |v|² ≤ 1`, then `v/|v| × R`.
    That needs only `sqrt`, unlike the prototype's Gaussian (`log`, `cos`).
  - **Cylinder and plane:** a uniform quad index and uniform `(u, v)` through `SurfaceMath.SurfacePoint`.
    Its trigonometry is the geometry every chunk already depends on.

**The bucket grid.**
- A dense 3D grid over the surface's bounding box, one micro spacing per cell, stored as CSR arrays
  (`cellStart[cells + 1]`, `items[]`): about 40³ cells on a sphere, 64 × 64 × 25 on the ring, 142 × 142
  × 12 on the narrow ring.
- Class distances search the 5 × 5 × 5 block of cells around the point, which is exact within the 2D
  cap.
- Nearest-seed queries search outward until the cell block covers the best distance, as the prototype's
  `Grid.Nearest` does.

**The texel scan** (a Burst job over the top level, with a managed reference). For every stored texel in
`[0, n]²`, record:
- its nearest macro seed, unwarped;
- its nearest micro seed, unwarped;
- its area, `|∂p/∂u × ∂p/∂v| / n²`, by central differences of `SurfacePoint` at a hundredth of a cell.

From the scan:
- **Adjacency:** two macro cells are adjacent if any texel and its `+i` or `+j` neighbour within one quad
  have them as nearest. Edge rows are stored by both quads, so adjacency across root-quad edges comes for
  free.
- **Micro areas:** the sum of the areas of the texels nearest to each micro seed, counting only texels
  their own quad owns (`MacroLayout.EdgeSource` returns false), so shared edges count once.
- **The coverage radius** for `R` (K2): the largest nearest-macro distance seen.
- **The saturation bound:** the largest nearest-micro distance seen, which must be at most 1.41 D (K2).
  Stage A throws if it is not; at 5,000 blue-noise seeds it is expected near 0.7 D.

**The BFS**, as in the concept:
- `ContinentCount` macro cells, chosen by a partial Fisher–Yates shuffle, start at elevation 1.
- A FIFO queue spreads them to neighbours, multiplying by a decay drawn uniformly in
  `[DecayMin, DecayMax]` at each step. A new elevation is kept if it beats the neighbour's and exceeds
  `ElevationFloor`.
- Adjacency lists are sorted ascending, and every draw comes from the one seeded generator in visit
  order.

**Scores and the threshold.**
- Each micro cell's score is its parent's elevation (the nearest macro seed to its own seed point) plus
  `ScoreNoise × Fbm(seed point / macro spacing, 3, 0.5, 2, seedD)`.
- Micro cells are sorted by score, descending, ties by index. Area accumulates down the list until it
  reaches `LandFraction`; the last cell's score is the threshold, and land is `score ≥ threshold`.

**`LandFraction` is the map's share, not the final terrain's.** The remaining noise floods more land
than it raises from the sea, so the final terrain comes out low: 27.2% for a 30% map on Earth at 2 / 6
km, 28.9% on the ring. Each scene's asset therefore carries a map fraction calibrated so the **final**
terrain is at its target (Task 6). A test holds the final fraction to the target within ±1.5 points.
Solving for the final fraction at runtime would mean iterating the whole field, for no gain over
calibrating offline.

### K4 — The detail cutoff: dropping the coarse octaves

`HeightParams` gains:

```
public int   FirstOctave;   // octaves below it are the continental band; 0 = none
public float CoarseGain;    // their amplitude relative to one (C5): -1 drops them

public HeightParams WithContinentalCutoff(double cutoff, float coarseAmplitude)
    // FirstOctave = BandEnd(cutoff): every octave at least `cutoff` long, counted by exponent
    // as the band edges are. CoarseGain = coarseAmplitude - 1. A cutoff of 0 leaves both at 0.
```

`LatticeNoise.Fbm`'s general overload takes `firstOctave` and `coarseGain`. For `k < firstOctave` it
multiplies the octave by `1 + coarseGain` on top of its band gain. When `coarseGain == -1` it **skips
the `Gradient3D` call** and only advances `amp` and `q`. On Earth that saves four of 24 octaves per
vertex.

- **Identity.** With `firstOctave` 0 the loop is the original's to the bit. `TerrainHeightFunction.Height`
  already chooses the original `Fbm` when nothing is set; extend that condition so a cutoff of 0 still
  takes the original path.
- **Strict prefix.** The same octaves are dropped at every depth, so a coarse evaluation is still a
  prefix of a finer one, and `GeomorphTests.ChildAtFullMorphEqualsParent` holds.
- **`MaxOctave` and `IsOctaveStarved` are unchanged.** At depths where `MaxOctave(d) < FirstOctave` the
  detail is zero and the macro layer carries everything, which is the point.
- **`MaxAbsHeight`** multiplies octave `k < FirstOctave` by `|1 + CoarseGain|` in its detail sum.

Configured on `ContinentSettings` (`DetailCutoff`, metres; `CoarseOctaveAmplitude`, default 0).
`TerrainRoot.Start` applies it after `WithBands`, which is where the macro layer's band edges already
reach `HeightParams`. The tests' scene parameters do the same (Task 7).

Cutoffs by scene. Each drops the same octaves relative to the root, so the scenes stay alike in scale:

| scene | octave 0 (one root across) | cutoff | octaves dropped |
|---|---|---|---|
| Earth | about 10,400 km | 1,000 km | 0–3 |
| small planet | about 2,600 km | 250 km | 0–3 |
| ring | about 1,250 km | 250 km | 0–2 |
| narrow ring | about 188 km | none | none: its octaves are all shorter than its continents |

### K5 — Band-limit and the pyramid: `MacroPyramid`

The field is continuous, but its coast transition and its warp are sharp beside a coarse level's texels.
Each level therefore holds the field low-pass filtered to its own spacing, derived from the level above.
That is a Gaussian pyramid, and the same machinery M5c needs for its erosion correction and wetness.

- **Filter.** Separable binomial `[1 4 6 4 1] / 16`, summed in double with integer weights and one
  division by 16. A constant passes exactly. The weights are positive, so the output stays within the
  input's range, which keeps `MaxAbsHeight`'s analytic bound valid (K7).
- **Decimate.** Level L's texel `(i, j)`, for `i, j ∈ [0, n_L]`, is the filtered level L + 1 at
  `(2i, 2j)`. The filter at the edge reads up to two texels past it, within L + 1's three-texel border.
- **Edge copies, per level,** straight after decimation: `MacroLayout.EdgeCopies` restricted to that
  level, so both quads store the owner's bits before the halo reads them.
- **Halo fill, per level.** Each border texel takes its value from the neighbouring quad, through
  `SurfaceMath.QuadEdgeLink`:
  - For a West-border texel `(i, j)` with `i < 0` and `0 ≤ j ≤ n`: take the neighbour's edge texel at
    `along = j` (reversed if the link is), then step `−i` texels inward from that edge. Inward is `+j`
    from South, `−j` from North, `+i` from West and `−i` from East. Likewise for the other three borders.
  - **Corner blocks** (both coordinates outside `[0, n]`) have no neighbour on a cube, where three faces
    meet. They take the halo source of the texel with the along-edge coordinate clamped into `[0, n]`.
  - **Rims** (an edge with no link: a cylinder's ends, a plane grid's outer edges) take the own quad's
    nearest edge texel.

  Every source is a texel in `[0, n]²` of some quad, which nothing in this pass writes, so the fill is
  one gather in any order. That matters, because C2 makes reads exact on the edge line, where shared
  vertices lie. A border texel only shapes reads just inside one quad, so this rule needs only to be
  continuous, not exact.
- **The top level** has its border computed directly by Stage B, at the points the parameterisation
  continues to. It is filtered on `[0, n]²`, then gets its edge copies and a halo fill, so its border
  matches its neighbours' filtered values.
- `MacroLayout.HaloSources(in SurfaceDef)` returns the `(destination, source)` index pairs for every
  quad and level, as `EdgeCopies` does. Burst jobs: `PyramidFilterJob` (one level into a scratch level),
  `PyramidDecimateJob` and `GatherJob` (edge copies and halos from an `int2` list). `MacroPyramid`
  holds the managed reference.

The filter passes about a quarter of the amplitude at a level's Nyquist and three quarters at four
texels. That is not C1's hard truncation. The tests measure the energy left below four texels per
wavelength instead of assuming it (Task 1). If ringing shows in the bicubic read, use the 7-tap
`[1 6 15 20 15 6 1] / 64`.

### K6 — Climate follows the coasts

The generator's lapse term already reads `max(baseHeight, 0)`. With continents, base height 0 is the
coast, so temperature falls with elevation above the sea, and climate, biomes and the ruggedness map
all stand on the continent map. This also answers most of `Docs/Ocean_Plan.md`'s Q7 for continent
scenes, since sea level is 0 there (K8).

One consequence needs the user's decision (D4). At 2 / 6 km on Earth the median land is at +2.3 km, so
at 6.5 °C per kilometre interiors run about 15 °C colder than M5b's land. The biome export
(`MacroMapExport`'s `<scene>_biomes.png`) shows the effect before anything is flown.

### K7 — The relief budget, the bounds and the pinned figures

- **`MaxAbsHeight`.** `MacroParams` gains `ContinentBound`, the profile's largest magnitude:
  `max(LandHeight · (1 + InteriorRise/2), OceanDepth)`, and 0 without continents. The base-height term
  becomes `(ContinentBound + BaseHeightScale × octave sum) × MaxOvershoot`. The pyramid's filter never
  leaves the field's range, so the bound stays analytic. The detail term drops the cut octaves (K4).
  On Earth, at Task 6's starting values, the envelope falls from 84.4 km to an estimated 37 km. The
  detail loses 50.7 km with octaves 0–3. The base-height term grows from 9.3 to 12.2 km: 6 km of
  ocean depth plus 0.9 km of fBm summed over its octaves, times the overshoot. That moves `LodVertical`'s anchor, and so the working
  sets.
- **Re-measure per scene**, with continents on (Task 7):
  - the detail relief factor `C`, at the cutoff (expected lower);
  - the macro relief factor `C_m`, on the real built maps, 20,000 nodes per depth (C10's method). It
    becomes per scene: `MacroTests.SceneMacro.ReliefFactor`, replacing the shared
    `MacroReliefFactor = 7`, which was measured on smooth fBm. The coast's slope is about 6 km over one
    micro cell on Earth.
  - `CombinedMargin`;
  - the worst working sets, with the flattened replica;
  - the skirt shares, with `StressSceneTests.TransientEdges`;
  - the scenes' `MaxResidentChunks`, if a worst case moves past its budget's margin.
- **Not expected to move:** the close-up roughness pins (the high band is untouched), the mountain-belt
  shares (the ruggedness map is untouched), and `HeightAtReadsWhatTheChunksRead`, which tests the
  function rather than a copy.

### K8 — Sea level, erosion, the ocean

- **Sea level is 0 in continent scenes**, because the profile's zero is the coast. The 30% sea levels in
  `Docs/Ocean_Plan.md` Q2 and the M5c amendment's D1 are superseded for those four scenes; the
  terrarium keeps its +68 m. Continent scenes target 30% land through `LandFraction` (K3).
- **M5c** erodes the sum of the base height and the detail octaves up to its `Oe`, which now includes
  the continent height, and drains to sea level 0. Its E4 machinery is `MacroPyramid`.
- **The ocean's C8 bound:** the macro part of `Residual(d)` can no longer assume a coarser level is the
  top level with octaves left out; derived levels are filtered. This is the same change M5c would have
  made. `Docs/Ocean_Plan.md` gets a note (Task 10).

### K9 — Configuration

`ContinentSettings` (a ScriptableObject in `Systems/Configuration/`), referenced from
`MacroLayerSettings.Continents`; null means none. Sizes are counts and multiples of the micro spacing,
so the same asset means the same shapes on any world. Heights are in metres.

| field | default | meaning |
|---|---|---|
| `Seed` | 1 | the continent map's seed, offset internally from the macro and detail seeds |
| `MacroCells` | 30 | the concept's macro cells |
| `MicroCells` | 5,000 | micro cells; at least 4 top-level texels across each (warned) |
| `ContinentCount` | 4 | BFS centres |
| `DecayMin`, `DecayMax` | 0.5, 0.75 | BFS decay per step |
| `ElevationFloor` | 0.1 | BFS stops spreading below it |
| `ScoreNoise` | 0.25 | micro score noise amplitude |
| `LandFraction` | 0.30 | land share of the **map**, calibrated per scene (K3) |
| `WarpAmplitude`, `WarpWavelength` | 0.5, 2 | in micro spacings |
| `CoastWidth` | 1 | in micro spacings: the profile's half-width |
| `LandHeight`, `OceanDepth` | 0, 0 | metres: per scene (Task 6) |
| `InteriorRise` | 0.8 | land height from 0.6 to 1.4 times `LandHeight` with BFS elevation |
| `DetailCutoff` | 0 | metres: detail octaves at least this long are the continental band; 0 none |
| `CoarseOctaveAmplitude` | 0 | their amplitude: 0 drops them (decision D3) |

As `MacroLayerSettings` does, it converts to a plain `ContinentConfig` struct (`ToConfig`), and
`ContinentParams.Create(in SurfaceDef, in MacroParams, in ContinentConfig)` resolves and validates it
once. It resolves the spacings, the cap and the coast width in metres, and the channel seeds. It
refuses non-finite values, `DecayMin > DecayMax`, counts below 1, `ContinentCount > MacroCells`, and
`LandFraction` outside (0, 1).

### K10 — Logging and warnings

`TerrainRoot.BuildMacro` logs, beside the macro build:
- the continent map's counts;
- the land fraction by area it reached;
- the coverage and saturation radii;
- Stage A, B and C times.

It warns, naming the lever:
- when micro cells come out under four top-level texels across (raise `LevelCount` or lower
  `MicroCells`);
- when the detail cutoff drops octaves the macro layer's top level cannot carry. That is a cutoff
  shorter than four top-level texels, which leaves a band nothing draws.

---

## Task 1 — The pyramid machinery (`macro-pyramid`)

1. `MacroLayout.HaloSources(in SurfaceDef s)`: the halo rule of K5 as `(destination, source)` pairs,
   for every quad and level. Also `EdgeCopies(in SurfaceDef s, int level)`, restricting the existing
   list to one level (keep the existing all-levels overload; `MacroMaps` uses it).
2. `MacroPyramid` (pure): `Filter(level)`, `Decimate(fromLevel)`, `Apply(int2[] pairs)`, and
   `DeriveDown(channel, topLevel)` running K5's sequence. It works on a generic accessor over one scalar
   per texel.
3. Burst jobs `PyramidFilterJob`, `PyramidDecimateJob` and `GatherJob`, in a separate file, which
   `MacroMaps` will call.
4. Tests, in the pure `MacroPyramidTests`:
   - every halo source lies in `[0, n]²` of some quad;
   - for a smooth function of the surface point, every halo texel matches the function at its own
     continued point, to the smooth function's slope times the continuation's error (a few texels'
     worth at most);
   - a constant passes exactly, and the output stays within the input's range;
   - after `DeriveDown`, `MacroEdgesAgreeAcrossEveryLink`'s check passes on the derived channel
     (bit-identical edges);
   - the energy at wavelengths under four texels is below a stated share, measured on a random field;
   - the managed reference equals the Burst jobs (Editor, in `MacroMapsTests`).

## Task 2 — The detail cutoff

`HeightParams.FirstOctave`, `CoarseGain` and `WithContinentalCutoff` (K4); the `Fbm` overload with the
skip; `TerrainHeightFunction.Height`'s path choice; `MaxAbsHeight`'s detail sum. Tests in
`HeightFunctionTests`:
- a cutoff of 0 is bit-identical to M5b (extend `HeightIsBitIdenticalWithoutAMacroLayer` and its
  siblings);
- the strict prefix holds with a cutoff;
- the dropped octaves contribute exactly nothing, and a `CoarseOctaveAmplitude` of 1 is the original
  sum to the bit;
- `FirstOctave` is read by exponent: a cutoff exactly at an octave's wavelength drops that octave.

## Task 3 — The continent map (Stage A)

`ContinentConfig`, `ContinentParams`, `ContinentSettings` (K9); `ContinentMap` with `Build` (managed
orchestration), the bucket grid, blue noise, `ContinentTexelScanJob` and its managed reference, the
BFS, scores and the threshold (K3). Tests in a pure `ContinentMapTests`:
- two builds give identical tables;
- the map's land share by area is `LandFraction` to within the last cell's area;
- adjacency is symmetric, and the macro graph is connected on a sphere;
- every macro cell has at least one neighbour;
- blue noise keeps every pair of micro seeds at least 0.5 D apart;
- the saturation bound and the coverage radius are as Stage A reports them;
- the class distances equal brute force on random points, within the cap;
- `ContinentParams.Create` refuses each invalid configuration listed in K9.

## Task 4 — The field (Stage B)

`ContinentField.Height(p, tables, params)` (pure) and `ContinentFieldJob` (Burst) over the top level's
stored texels. Tests in a pure `ContinentFieldTests`:
- continuity: neighbouring texels differ by at most a Lipschitz bound times their distance. The test
  derives the bound from the parameters:
  - the profile's slope, 15/8 of the larger amplitude per `CoastWidth`;
  - the squared distance's gradient, at most 2.3 inside the transition, given every point is within
    0.7 D of a seed;
  - the warp's stretch;
  - `E`'s slope on the land side;
- the height is exactly 0 on a land–ocean bisector between an adjacent pair, with no warp;
- wherever `|s| ≥ CoastWidth` it is exactly the plateau: `−OceanDepth` at sea, and the `E`-scaled
  `LandHeight` on land. In particular, a land cell's interior shows no cell ripple;
- every value lies within `[−OceanDepth, LandHeight · 1.4]`;
- the managed reference equals the Burst job to the bit (Editor).

## Task 5 — Into the maps

1. `MacroBuildJob` and `MacroGenerator.Texel` take the continent value per texel and a
   `HasContinents` flag. With the flag off the base height is computed exactly as today; do not add a
   zero, which can turn a `−0.0` into a `+0.0` and break the bit-identity test. A disabled build passes
   a one-element array, as `MacroMaps.Disabled()` does for the texels.
2. `MacroMaps.Build` runs K1's sequence when `ContinentParams` are given, and records Stage A–C times.
   `MacroGenerator.Build`, the managed reference, does the same in managed code.
3. `MacroParams.ContinentBound` (K7), and `TerrainHeightFunction.MaxAbsHeight` reads it.
4. `TerrainRoot.BuildMacro` reads `Settings.Macro.Continents`, applies the cutoff to `_params` (K4),
   builds, logs and warns (K10). The cutoff must reach `_params` before `LodMath` and the scheduler see
   it, so apply it beside `WithBands` in `Start`.

Tests:
- with no continent asset, every scene's maps equal M5b's (`MacroGenerator.Build` on the M5b
  configuration);
- `MacroJobMatchesTheManagedBuilder` with continents (Editor);
- `MacroEdgesAgreeAcrossEveryLink` and `GeneratedMacroEdgesAgreeAcrossEveryLink` with continents;
- `HeightAtReadsWhatTheChunksRead` with continents;
- `GeomorphTests.ChildAtFullMorphEqualsParent` over a real macro layer with continents and a cutoff
  (Editor);
- `MacroMapsOutliveTheJobs` unchanged, and a failed Stage A, B or C frees every temporary array.

## Task 6 — Calibration and the assets (Earth and the ring)

1. **The probe**: the prototype's method as a test-side helper, `ContinentProbe`. Area-uniform points
   go through the real path: `MacroMaps` built in managed code, then `HeightAtPoint` at `MaxDepth`. It
   reports agreement, final land, flooding, islands and the median land and ocean heights.
   - `ContinentTests.ContinentsSurviveTheDetailNoise` runs it at 20,000 points per continent scene. It
     asserts agreement of at least 90% and final land within ±1.5 points of the scene's target.
   - An `[Explicit]` variant at 100,000 points is the retuning tool.
2. **The assets**: `EarthContinents.asset` and `RingworldContinents.asset`, referenced from
   `EarthMacro.asset` and `RingworldMacro.asset`. The values, from the prototype, with the amplitudes
   as D1 settled them:

   | scene | `DetailCutoff` | `LandHeight` / `OceanDepth` | `BaseHeightScale` (the macro fBm) | expected agreement |
   |---|---|---|---|---|
   | Earth | 1,000 km | 2,000 / 6,000 m | 900 m (0.3 of today's) | 95.6% |
   | ring | 250 km | 1,500 / 4,500 m | 450 m | 97.9% |

   Then calibrate `LandFraction` so the final terrain is at 30% (expected about 0.33 on Earth and 0.31
   on the ring) and record the values.
3. `MacroMapExport` (Task 8) on both scenes before any play session, to check the biome map (D4).

## Task 7 — Re-measure and re-pin (Earth and the ring)

Everything K7 lists, for the two scenes, with each pinned constant's comment rewritten to say what it
was measured on and when. The places:
- `EarthScaleTests` and `StressSceneTests.Scene`: `ReliefFactor`, `Worst*`, `Budget`, `SkirtFraction`;
- `MacroTests.SceneMacro`: the new `ReliefFactor` and the continent copies;
- `MacroMapsTests.StressScenesRunTheirMacroLayers`: hold the new assets to the copies;
- `StressSceneAssetTests` and `EarthSettingsAssetTests`: any setting that changed.

The tests' scene `HeightParams` must apply the cutoff exactly as `TerrainRoot` does. Route all of them
through one helper next to `MacroTests.MaxAbsHeight`, so no test can build a scene's parameters without
it. A copy that misses the cutoff would measure the old terrain and stay green.

## Task 8 — Tools

`MacroMapExport` writes two more images:
- `<scene>_continents.png`: the micro cells by class and parent elevation, macro cell edges, centres
  tinted;
- `<scene>_coast.png`: `s` on a diverging scale, with the coast contour.

The existing `_base` image now shows the continent height. The export already builds the maps without
play mode, so it has Stage A's tables.

## Task 9 — The small planet and the narrow ring (`continents-b`)

Tasks 6 and 7 for `SmallPlanetStress` and `NarrowRingStress`, which start at the same shares of
`HeightScale` as D1 set for Earth and the ring:
- the small planet: a cutoff of 250 km, and 0.10 / 0.30 of its 10,000 m, which is 1,000 / 3,000 m;
- the narrow ring: no cutoff, and 0.15 / 0.45 of its 4,000 m, which is 600 / 1,800 m, adjusted by the
  probe. Neither was in the prototype. Also confirm the narrow ring's band-limit warning stays quiet:
5,000 micro cells are about 11 texels across there.

## Task 10 — Documentation

- `CLAUDE.md`:
  - the status;
  - the pipeline's Stage 0;
  - the height-function and macro-layer bullets;
  - a Non-Obvious entry for the squared distance and the odd quintic;
  - one for the cutoff's identity path;
  - one for the pyramid and halo rule;
  - the test count and list;
  - the memory figures.
- `Docs/M5_MacroLayer_Plan.md`: M5c's E4 now names `MacroPyramid`, and its D1 sea levels are superseded
  for the continent scenes.
- `Docs/Ocean_Plan.md`: Q2's sea levels are superseded for the continent scenes (sea level 0, 30% by
  `LandFraction`); Q10's order begins with continents; C8's `Residual(d)` gets the note in K8.
- `SonomaRevisedPlan.md` §5: where continents sit.
- `Docs/Continents_Outline.md`: link this plan; record the squared-distance and odd-quintic results
  under its prototype section.
- This plan's amendments.

---

## Tests, gathered

**Pure (run outside the Editor):** `MacroPyramidTests` (Task 1); the cutoff tests in
`HeightFunctionTests` (Task 2); `ContinentMapTests` (Task 3); `ContinentFieldTests` (Task 4); the
identity, edge and `HeightAt` tests with continents (Task 5); `ContinentTests.ContinentsSurviveTheDetailNoise`
(Task 6); every re-pinned constant (Task 7).

**Editor:** the pyramid's, the texel scan's and the field's Burst-against-managed checks; the macro
build's with continents; the geomorph invariant with continents; the asset and scene holds.

The probe at 20,000 points costs about a second per scene under .NET 8 and several under the Editor's
Mono. If that proves slow, move its sampling onto the Burst `HeightSampleJob`, as `WorkingSetTests`
does.

## Verification

**Agent-side, without the Editor:** the pure tests through the out-of-Editor harness (the
`unity-verification-without-batchmode` note); the probe's numbers against K2's table; the startup
timings measured in the harness, managed, as an upper bound.

**Human-side, in the Editor:**
1. All tests green, the Burst checks included.
2. `MacroMapExport` on each continent scene: the continents, the coast, the base height and the
   biomes look right (D4).
3. Play each continent scene:
   - from orbit, the continents are the map's;
   - from 1 km up, the coasts have their own detail and there is no cell texture on the plateaus;
   - climate follows the coasts.
4. The startup log: Stage A–C times (target under 200 ms on Earth in a build), the land fraction and
   the radii.
5. On leaving play mode in each scene: no relief warning, and the peak working set under budget.
6. `SampleScene` and `TerrariumStress` unchanged.

## Definition of done

- [x] `macro-pyramid` merged: halo fill, filter and filter-down, tested, Burst equal to managed.
- [x] `continents-a` merged: the cutoff, the map, the field, the pipeline, the assets and re-pinned
      figures for Earth and the ring, the export.
- [ ] `continents-b` merged: the small planet and the narrow ring, the documentation.
- [x] Every scene without a continent map builds M5b's maps and heights to the bit.
- [x] Agreement at least 90% and final land within ±1.5 points of target in every continent scene.
- [x] The Editor checks under "Verification" done and recorded.

## Decisions for the user

- **D1 — Earth-like heights or crisp continents.** On Earth, 1 / 4 km gives median land at +1.5 km and
  ocean at −3.6 km, with 90% agreement and more inland seas. 2 / 6 km gives +2.3 / −5.6 km and 95.6%.
  The ring's equivalents are 1 / 3 km (95.1%) and 1.5 / 4.5 km (97.9%).

  **Decided (2026-10-23): crisp continents.** Earth runs `LandHeight` 2,000 m and `OceanDepth` 6,000 m,
  and the ring 1,500 m and 4,500 m: Task 6's starting values, now the settled ones. The small planet
  and the narrow ring start at the same shares of their `HeightScale` (Task 9). D4 follows from this:
  Earth's median land at +2.3 km is the colder case.
- **D2 — The terrarium.** Default: no continent map. In a 10 km world they would be lakes and islands of
  a few hundred metres.
- **D3 — Drop the coarse octaves, or keep them at a reduced amplitude.** Dropping is what the prototype
  measured. A small amplitude (`CoarseOctaveAmplitude` 0.2, say) restores some continent-scale
  variation inside the continents, at some cost in agreement; measure with the probe before choosing it.
- **D4 — The lapse rate.** Continents put land kilometres above the sea, so interiors cool by 10–15 °C
  against M5b. Keep 6.5 °C/km and accept colder continents, lower `LapseRate` per scene, or choose D1's
  lower amplitudes.
- **D5 — Extensions, later:** moisture from distance to the sea (dry interiors), which needs `s` stored
  as a channel; and mountain belts where two continents' spreads meet (ruggedness raised along those
  macro-cell boundaries).

---

## Amendments from implementing the continents

The plan above is the plan as written. Record here where building it proved it wrong, rather than
editing the tasks, so the plan still reads as what was decided up front.

### Task 1, `macro-pyramid` (2026-10-23)

1. **One job, not three.** K5 named `PyramidFilterJob`, `PyramidDecimateJob` and `GatherJob`. The
   sequence runs level by level and each level needs the one above finished, so there is nothing to run
   in parallel across steps. A single-threaded `MacroPyramidJob` calls the same static
   `MacroPyramid.Run` as the managed reference, which makes the two bit-identical by construction rather
   than by care. The whole pyramid is a 5 × 5 stencil per texel, about 13 million weighted adds on
   Earth's four levels.
2. **The halo is exact only where the quads continue into each other.** Measured at 8 and 32 cells, the
   distance from a border texel's continued point to its source is:
   - inside a cylinder or a plane grid: 0, corner blocks included (after the review, below);
   - across a cube edge: 0 at an edge's midpoint, at most 0.567 texels per texel of depth over the
     middle half, and exactly 1.0 at the ends, one texel from the cube corner. There the two faces'
     grid lines are different great circles. The cube's corner blocks come within 0.70–0.86.

   K5 expected this ("needs only to be continuous, not exact"), and the test pins both regimes. Ignoring
   a link's reversal lands 70–83% of the cube's reversed-link side texels more than two texels per
   texel away, up to 25: the mutation the test keeps.
3. **The filter leaves 25.7% of a derived level's energy under four texels per wavelength** on white
   noise, against white noise's own 50.4%. That is close to the 24% the binomial's `cos⁴` response
   predicts. K5's fallback, the 7-tap filter, waits for ringing to show in a real channel.
4. **The review of PR #15 found seven things, all fixed:**
   - **The halo and the edge copies do not commute.** The copies read the owners' extension texels,
     which the halo fills. On a cube the halo gives the two quads' extension texels different values,
     so copies run before it are overwritten: 48 copied texels end unequal. K5's "one gather in any
     order" was right for the halo alone, not for the pair. No read catches the wrong order, because
     derived levels are read only on texels, where extension texels weigh nothing, so the mutation test
     checks the stored texels.
   - **Corner blocks walk to the diagonal quad.** K5's clamp rule read the side neighbour's texel, off
     by `depth` along the edge, where four quads meet; crossing both edges lands on the overlapping
     texel. With it, the copies change nothing inside a cylinder or plane grid. They are load-bearing
     on the cube alone: without them its derived edges disagree in 176 samples, the others in none.
     Before the corner fix the others disagreed too (60–672), which this amendment first recorded as
     the copies being load-bearing everywhere.
   - **`MacroPyramidTop`** makes the caller's top border explicit: `FilterAndFill`, `Fill` or `Keep`.
     K5's sequence is `FilterAndFill`; M5c's erosion correction will want `Fill`.
   - **Both `Run` overloads validate** the channel and the plan against the layout (`MacroPyramid.Validate`).
     The plan now carries its layout.
   - The texel loops step from a per-level origin instead of calling `MacroLayout.Index`. The all-levels
     `EdgeCopies` concatenates the per-level lists. `HaloSources` walks the border ring only. The plan's
     lists are built straight into pre-sized arrays.
5. **Tests:** seven pure ones in `MacroPyramidTests`, and `PyramidJobMatchesTheManagedPyramid` and
   `PyramidJobRefusesAMismatchedChannelOrPlan` in `MacroMapsTests`. That makes 149 EditMode tests, 110
   of them pure; all 110 pass outside the Editor. In the Editor (2026-10-23) all 149 were green,
   including the Burst job against the managed reference in all three top-level modes, the new scripts
   imported cleanly from their hand-written `.meta` files, and the scenes played without errors. `MacroTests.Surfaces`, `Real`, `Sampler` and
   `EdgeDisagreements` became `internal` so the derived channel is held to the same C2 check as
   generated maps.

### Tasks 2–8, `continents-a` (2026-10-24)

1. **A design review before building changed seven things.**
   - **`MaxAbsHeight` keeps its old association.** It adds `ContinentBound × MaxOvershoot` as a separate term. K7's `(ContinentBound + BaseHeightScale × Σ) × MaxOvershoot` would have reassociated the product and moved the terrarium's envelope by an ULP.
   - **A cutoff of 0 never reaches `BandEnd`.** `OctaveWavelength0 / 0` is infinite, which `FloorLog2` refuses. `CoarseGain` is 0 whenever nothing is dropped.
   - **K9's validation gains six checks:**
     - `DecayMin > 0`;
     - `DecayMax ≤ 1`, since above 1 a step can beat the elevation it came from and the BFS never settles;
     - `ElevationFloor ≥ 0`;
     - `InteriorRise` in [0, 2], since above 2 the land multiplier goes negative;
     - no negative heights;
     - `CoastWidth` in (0, 2), since a profile two spacings wide never reaches the plateau under the 2D cap.
   - **One configuration.** `ContinentParams.Create(s, mp, in MacroConfig)` reads `MacroConfig.Continents` through the validator `MacroParams.Create` also calls. Every build checks that the bound it was given is the field's.
   - **Areas come from one table**, built in managed code from quad 0, since root quads are congruent. No land-threshold input comes from Burst's trigonometry. Seeds come from `Random.CreateFromIndex`, whose state is never 0; a zero state hangs the sphere's rejection loop in a player.
   - **`Demand`'s macro term is `C_m × (ContinentBound + BaseHeightScale) / S0`.** Pricing it by `BaseHeightScale` alone would have cut it by 70% when the fBm fell to 900 m, while the coasts added 6 km. Without continents the bits are unchanged.
   - **`LazyTexels` needs the continent channel.** It refuses a macro layer with continents without one, so no test can read the fBm alone and stay green.
2. **Saturation is wider than K3 expected, and the check is now exhaustive.**
   - Best-candidate placement with eight fixed candidates leaves the farthest texel 1.20 micro spacings from its nearest micro seed on both Earth and the ring (unwarped, inside the quads). K3 expected about 0.7. That is still under the √2 limit, with less margin.
   - With the class distances capped as squares, the plateau check is exact. Stage B writes each texel's own-class `d²` where the other class is capped, and throws past `ContinentParams.PlateauLimit2`. The worst warped point is 1.13 D on Earth and 1.20 D on the ring.
   - Placement guarantees no 0.5 D minimum either. The closest pairs on the small test maps are 0.41–0.56 D, pinned as measured.
3. **The saturation check covers the texels inside the quads only.**
   - Past a cylinder's open rims, the border texels lie off the surface and far from every seed: 1.52 D on the small test ring.
   - A border value reaches a drawn texel only through the filter's outer taps, and the halo replaces it afterwards.
   - The coverage radius still spans the border, so the blend's `Σw > 0` holds everywhere Stage B evaluates it.
4. **Land is chosen by rank,** scores descending with ties to the lower index, rather than `score ≥ threshold`. The threshold rule would also take every later cell that ties the last one.
5. **One scan job and one field job.** Each calls the per-texel function the managed reference loops over (`ContinentMap.ScanTexel`, `ContinentField.AtTexel`). The reductions stay managed, in texel order. `ContinentWitnessJob` lets the Editor's equality tests prove that Burst actually ran.
6. **Earth, with continents.**
   - **Timings, managed under .NET 8, as an upper bound:** Stage A 368 ms, B 717 ms, C 172 ms.
   - **`LandFraction` 0.322.** It reaches 30.0% final land on 50,000 points and 29.7% on an independent 100,000. Agreement is 95.8–96.1%, flooding 9.6–10.3%, islands 1.2%, and the medians are +2,330 and −5,752 m. The prototype had 95.6% and +2.3 / −5.6 km.
   - **C does not move with the cutoff.** On 20,000 nodes per depth the roughest is 411 with it and 410 without, so 674 stands.
   - **C_m is 53.7 at the roughest, pinned at 81.** The combined demand is 0.2699 against an allowance of 0.6146.
   - **The envelope falls from 84.4 to 36.6 km** (K7 estimated 37).
     - The worst working set moves back to the face edge: 8,222 at (76/512, 0), with the corner at 8,046.
     - The fresh corner count is 7,002, down from 7,062.
   - **The worst skirt share is 0.605** (0.628 before), so `SkirtFraction` 1 stands.
7. **The ring, with continents.**
   - **Timings, managed:** Stage A 229 ms, B 678 ms, C 55 ms.
   - **`LandFraction` 0.351, not the expected 0.31.** It reaches 30.0% final land on 50,000 and 100,000 points.
   - **Agreement is 93.7%, under the prototype's 97.9%.** Flooding is 16.4% against its 5.4%, and the medians are +1,780 and −4,221 m.
     - **Part of the gap is climate.** The prototype took its band gains from M5b's climate, and with continents the ring's land is 9 °C colder. A lapse rate of 0 recovers 1.3 points: 94.8%.
     - **The rest is open.** Stage C's filter (seven texels to a micro cell) and the different map are the suspects. It clears the 90% floor.
   - **C is 113.4 with or without the cutoff,** so 171 stands. **C_m is 48.7, pinned at 73.**
   - **The envelope falls from 50 to 32.2 km.** The worst working set is 4,980 at (21/512, 245/512).
   - **The worst skirt share is 0.774** (0.760 before), so `SkirtFraction` 1.2 stands.
8. **D4: the lapse rate stays at 6.5 °C/km** (user's decision, on Earth's evidence).
   - **On Earth the colder-interiors premise did not hold.** Land averages 3.9 °C with continents against −0.7 °C for M5b's land, taken as the 30% above its sea level. Tundra takes 41% of the biome weights against 54%. Most likely the continents sit at lower latitudes than M5b's land happened to, which outweighs their height; not measured.
   - **On the ring it did hold.** A cylinder has no latitude, so land averages 7.7 °C against 17.0 °C, and tundra rises from 1% to 21%. `ContinentTests.ClimateOverLand` prints both.
9. **The export builds its height parameters as `TerrainRoot` does,** bands, roughness and cutoff included. Its detail image had ignored the roughness spectrum since C10.
10. **The first Editor run found the jobs a ULP apart.** Burst's builtin sin, cos and tan run at `FloatPrecision.Standard`, 3.5 ULP, not managed code's. So `SurfacePoint` inside a job differed in its last bits.
    - **Why it surfaced only here.** The float macro texels never showed it; the continent map's squared distances, compared as doubles, did. The saturation and plateau radii were one ULP apart, while every decision (adjacency, areas, land, elevations, parents) agreed.
    - **The fix.** `ContinentMap.TopPoints` computes the top level's texel points once in managed code, and both stages' jobs read them, so neither evaluates trigonometry. The two builds are now equal by construction, not by luck.
    - **Test order.** The equality tests compare texels before radii, so a future difference names how many texels and by how much.
    - **The measuring tools also ran in that Editor run,** despite `[Explicit]`. They took 53 minutes, and three hit the Test Runner's 180 s timeout. They now skip unless `SONOMA_MEASURE=1`. The suite itself took 33 s.
11. **Tooling and tests.**
    - The measuring code is committed as `[Explicit]` tests: `ReliefMeasurements` (C, C_m, skirt shares, the land-fraction bisection, working sets), `ContinentTests.ContinentsSurviveTheDetailNoiseAtFullSample` and `ClimateOverLand`. `SONOMA_SCENES` (comma-separated) limits them to the named scenes, and they run only with `SONOMA_MEASURE=1`.
    - There are 168 EditMode tests, 125 of them pure, plus the seven explicit tools. All 125 pure tests pass outside the Editor.
12. **In the Editor (2026-10-24), after item 10's fix,** every check under "Verification" passed. The user reports:
    - all 168 tests green, the measuring tools skipped as intended;
    - the macro map export on both scenes looking right;
    - the play-throughs looking right, with no warnings;
    - no relief or budget warning on leaving play mode.

    The startup log, with Burst in the Editor:

    | | Earth | Ring |
    |---|---|---|
    | Stage A / B / C | 221 / 148 / 28 ms | 225 / 207 / 35 ms |
    | The whole macro build | 585 ms (235–256 ms without continents) | 705 ms |
    | Land share of the map; saturation (warped) | 32.2%; 1.20 (1.13) | 35.1%; 1.20 (1.20) |
    | Height envelope | 36.6 km | 32.2 km |
    | Peak working set (budget) | 6,994 (9,400) | 4,716 (5,600) |

    - **Ground rule 3's target is not met in the Editor.** It asks for under 200 ms on Earth in a build; Stages A–C take 397 ms here. No build has been measured. Stage A is mostly managed code: placement, `TopPoints` and the reductions after the scan. It is the place to start if a build is over too.
    - **One rebase on the ring took 16.9 ms** over 4,700 chunks, where rebases elsewhere took 1–5 ms (Earth's slowest this session was 3.6 ms). Continents do not touch the rebase, so a one-off hitch is the likely cause; not investigated.

### Task 9 and Task 10, `continents-b` (2026-10-24)

1. **The small planet took Task 9's starting values unchanged.** It runs 1,000 / 3,000 m and a 250 km cutoff, which drops detail octaves 0–2, with the macro fBm at 600 m.
   - **`LandFraction` 0.312.** It reaches 30.0% final land on 50,000 points and 29.8% on an independent 100,000. Agreement is 95.5%, flooding 9.4%, islands 2.3%, and the medians are +1,161 and −2,885 m.
   - **C does not move.** On 20,000 nodes per depth the roughest is 214.5 with the cutoff and 215.3 without. That falls short of the 226.5 its 340 was pinned from, so 340 stands, and `MorphStartFraction` 0.12 still has room to 0.123.
   - **C_m is 51.4 at the roughest, pinned at 77.**
   - **The envelope falls from 48 to 23.2 km.** The worst working set moves to the face corner: 7,302, under the 8,300 budget by 14%.
   - **The worst skirt share is 0.622**, so `SkirtFraction` 1 stands.
2. **The narrow ring needed two changes from Task 9.**
   - **`CoastWidth` 0.8, not 1.** At 1, Stage B threw. On the open rims a point has seeds on one side only, and the warp took four rim texels to 1.46 micro spacings from their class, past the plateau's √2. Away from the rims the worst is 1.24.
     - The three levers the error names were measured. `WarpAmplitude` 0.3 clears it (1.38 against 1.41) but changes every coast's shape. Doubling `MicroCells` does not help (1.45), since the rim effect is scale-free.
     - `CoastWidth` 0.8 raises the limit to 1.55 and leaves the map alone. It makes coasts a quarter steeper, which costs little at 600 / 1,800 m.
     - Unwarped, the rims' saturation is 1.32 against Earth's and the ring's 1.20, so a narrower band would need the same.
   - **A 150 km cutoff, not none.** Without one agreement was 86% and 28% of the map's land flooded, under the 90% floor. On 30,000 points at roughly calibrated land fractions:

     | heights | no cutoff | 150 km (octave 0) | 80 km (octaves 0–1) |
     |---|---|---|---|
     | 600 / 1,800 m | 86.2% | 92.2% | 95.2% |
     | 800 / 2,400 m | | 94.6% | |
     | 1,000 / 3,000 m | 92.9% | 96.1% | 97.9% |

     150 km keeps D1's shares of `HeightScale` and drops only octave 0, the 187.5 km one in the low band. 80 km would also drop the 94 km octave, in the mountain band, which every other continent scene keeps whole.
   - **`LandFraction` 0.300.** It reaches 30.0% final land on 50,000 and 100,000 points. Agreement is 92.5%, flooding 12.6%, islands 5.3%, and the medians are +747 and −1,764 m. Micro cells are 11.5 top-level texels across, so the band-limit warning stays quiet, as Task 9 asked.
   - **C rose, and with it the morph start fell.** On 20,000 nodes per depth the roughest is 48.4 with the cutoff and 48.5 without. October's pin of 67 was half again over 44.2, a sample of the same terrain that missed this tail; the continents did not move it. 73 is half again over 48.5.
     - At 73, `MorphStartFraction` 0.14 leaves an allowance of 0.420 against the 0.441 that twice the detail demand needs. The narrow ring therefore runs 0.13, with 0.133 the most both margins allow.
     - That is a morph window a little narrower, the same lever `SampleScene` took at 0.09.
   - **C_m is 24.9 at the roughest, pinned at 37.** The envelope is 27.9 km. The worst working set is 4,320 at the pinned camera, (37/512, 267/512) and its mirror, under the 4,900 budget by 13%.
   - **The worst skirt share is 0.788** on 400 edges, against 0.677 on the 200 its 1.1 was sized from, so `SkirtFraction` is 1.2.
3. **D4, on the two new scenes.** The small planet behaves like Earth. Land averages 10.2 °C with continents against 0.6 °C for M5b's land, and tundra falls from 52% to 26% of the biome weights. The narrow ring behaves like the ring, mildly. Its land averages 13.8 °C against 15.4 °C, and tundra stays at 2%. The lapse rate stays at 6.5 °C/km.
4. **Documentation (Task 10):**
   - `CLAUDE.md`: the status, the systems, Stage 0, the continents' height-function entry and table, the Non-Obvious entries (the squared distance and odd quintic, the cutoff's identity path, and Burst's trigonometry), the tests, the envelopes, working sets, skirts and `C_m`;
   - `Docs/M5_MacroLayer_Plan.md`: E4 names `MacroPyramid`, and D1's sea levels are superseded for the continent scenes;
   - `Docs/Ocean_Plan.md`: Q2, Q10 and C8;
   - `SonomaRevisedPlan.md` §5;
   - `Docs/Continents_Outline.md`.
5. **Tests.** There are no new tests. The continent tests iterate over every scene with continents, so the two scenes joined them through their `MacroTests.StressMacros` entries. That leaves 168 EditMode tests, 125 of them pure, plus the seven explicit tools; all 125 pure tests pass outside the Editor.
   - **Not yet run in the Editor:** the Burst checks and asset holds for the two new assets, `MacroMapExport` on both scenes, and the play-throughs.
6. **In the Editor (2026-10-24).** The user reports the macro map exports on both scenes looking good and the play-throughs looking right. The startup and exit logs, with Burst in the Editor:

   | | Small planet | Narrow ring |
   |---|---|---|
   | Stage A / B / C | 239–245 / 155–187 / 27–28 ms | 288 / 322 / 51 ms |
   | The whole macro build | 628–678 ms | 987 ms |
   | Land share of the map; saturation (warped) | 31.2%; 1.20 (1.13) | 30.0%; 1.32 (1.46, under the 1.55 `CoastWidth` 0.8 allows) |
   | Detail octaves from; height envelope | 4; 23.2 km | 1; 27.9 km |
   | Peak working set (budget) | 6,402 and 5,310 over two sessions (8,300) | 2,852 (4,900) |
   | Slowest rebase | 3.76 ms over 5,182 chunks | 1.03 ms |

   - No relief, budget, band-limit or plateau warning in either scene.
   - The narrow ring's macro build is the slowest of the five, because its three levels hold the most texels (991,480). Stage B there is the costliest stage of any scene.
   - All 168 EditMode tests are green in the Editor, the Burst continent checks and the new assets' holds included.

---

## Out of scope

- Plate tectonics proper: plate motion, subduction and uplift histories. The macro cells are templates,
  not plates.
- Rivers, lakes and water surfaces (M5c's wetness, and the ocean plan).
- Continents in `SampleScene` or the terrarium (ground rule 1, D2).
- Storing `s` or a continent id as a macro channel (D5).
- Retuning the biome table for the new climate, beyond the lapse rate (D4).
