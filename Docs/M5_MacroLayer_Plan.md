# M5 — Macro Layer and Biomes: Exact Implementation Steps

**Audience:** a coding agent working in `G:\UnityProjects\TerrainGen` with no prior context on this project.
**Parent plan:** `SonomaRevisedPlan.md` (repo root), milestone M5 and §4.6.
**Prerequisite:** M4 is complete and merged, and so are the three extra stress scenes (`stress-scenes`:
`SmallPlanetStress`, `RingworldStress`, `TerrariumStress`). All 99 EditMode tests should pass before you
start. Local build-profile changes
in `ProjectSettings/` and `Assets/Settings/Build Profiles/` may be present; leave them alone.

The height function has carried a `MacroSample` argument since M2, holding zero, so that this
milestone would be "a change to one function body rather than a signature change rippling through both
jobs and every test" (`MacroSample.cs`). M5 fills it in. It builds a small, persistent pyramid of maps
per root quad at startup, reads it bicubically inside the sample jobs, adds its base height to the
terrain, and uses its climate fields to pick biomes. The biomes scale the detail octaves band by band,
and they reach the shader as splat weights. M5 also makes **close-up roughness** an explicit goal
(C10). Within a few metres of the ground the planet scenes are millimetre-smooth today, and fixing that
spends the same relief budget the biomes do, so the two are tuned together.

Everything that made M2–M4 work has to survive this. Height stays a pure function of a surface point and
a depth. Same-depth neighbours still agree to the bit, and a fully morphed child is still its parent.
`SampleScene` stays bit-identical. Most of this document is about the places where a naive macro layer
breaks one of those, which reading the parent plan does not show.

---

## Ground rules

1. **`SampleScene` does not change.** It gets no macro layer and keeps its material. With no macro
   layer the height function must return the M4 bits exactly, which is what keeps `TerrainReliefTests`,
   `WorkingSetTests` and every pinned relief figure valid without re-measurement. The vertex buffer's
   bytes may change (C6); its geometry may not.
2. **Two landable branches**, as M2–M4 did.
   - **M5a — `m5a-macro-layer`.** The map pyramid, its generation, the edge rule, the bicubic read,
     base height in the height function, `SurfaceMath.Locate` and a macro-aware `HeightAt`. No biomes,
     no shader change, no vertex-layout change. `EarthStress` gets continents.
   - **M5b — `m5b-biomes`.** `BiomeDefinition`, climate classification, per-band amplitude, splat
     weights in the vertex, the shader's biome path and `EarthStress`'s own material. Then close-up
     roughness per stress scene (C10), sized against each scene's relief allowance.
3. **Nothing new per frame on the main thread.** M4's profiler pass put `TerrainRoot.Update` at a
   median 5.85 ms against a 2 ms budget (`Docs/M4_FloatingOrigin_Plan.md` amendment 25), and fixing that
   is M6's job. M5's main-thread work is a one-off build at startup; everything per chunk runs in the
   sample jobs, on workers. If a design here would add main-thread work per frame, it is the wrong
   design.
4. **Plain macro noise only.** The maps are filled from a pure generator. Erosion, rivers and tectonics
   stay out of scope (see the end; erosion was later planned as M5c, in the amendments). But build the maps as if something non-local will write them
   later: that is the whole reason they are maps and not a function. C2 exists for that reason.
5. **Measure before concluding.** When a figure here disagrees with the Editor, the Editor is right;
   record it in the amendments section.

---

## Conventions and settled decisions

### C1 — The macro layer is band-limited by depth, exactly like the octaves

A map has a texel spacing, and a chunk has a vertex spacing. Where the chunk is coarser than the map,
it point-samples content it cannot represent. That is aliasing, the same defect `HeightParams.MaxOctave`
exists to prevent for the detail octaves. A single 257² map per face, as §4.6 suggests, has a texel
spacing of depth 3's vertex spacing, so it would alias at the coarsest depths. Those are the chunks seen
from orbit, which is exactly where continents are the point.

So each root quad gets a **pyramid**, one level per depth up to `LevelCount − 1`:

```
level L:  n_L = (R − 1) × 2^L texel cells across the quad (n_L + 1 texels, plus a border; C2)
MacroLevel(depth) = clamp(depth, 0, LevelCount − 1)
```

Level `L`'s texel spacing is exactly depth `L`'s nominal vertex spacing, so at every depth up to the top
level the chunk's vertices land *on* texels. The bicubic read there returns the stored value with weights
exactly `(0, 1, 0, 0)` and no interpolation error at all. Deeper chunks interpolate the top level.

Each level is generated **band-limited**, not filtered: the generator sums only the macro octaves whose
wavelength is at least four of that level's texel spacings. Four, not Nyquist's two: Catmull-Rom
interpolation of a component near Nyquist rings. This is the same truncation-by-octave the detail
noise uses, and it gives each level exactly the content its chunks can carry. Read the octave limit off
the IEEE exponent, as `HeightParams.FloorLog2` does, not from `log2`.

`MacroLevel` takes a **depth**, never a measured size, for the reason `MaxOctave` does: same-depth
neighbours must read the same level, or they evaluate different functions along their shared edge. The
coarse sample job reads level `MacroLevel(depth − 1)`, the parent's, so the morph target is the parent's
macro as well as its octaves.

At `R` 33 and `LevelCount` 4, with the macro generator's octave 0 at `S0`:

| level | texels across (+ border) | spacing at Earth radius | macro octaves | finest wavelength |
|---|---|---|---|---|
| 0 | 33 (37) | 325 km | 0–3 | 1,300 km |
| 1 | 65 (69) | 163 km | 0–4 | 650 km |
| 2 | 129 (133) | 81 km | 0–5 | 325 km |
| 3 | 257 (261) | 40.6 km | 0–6 | 163 km |

That is 91,940 texels per root quad and 551,640 on a cube sphere. At one `float4` each it comes to
**8.8 MB**, built once.

### C2 — Shared edges are written once, so the cube's 12 edges cannot crack

A bicubic read near a root-quad edge uses texels from that quad only. Unless the two quads sharing an edge
store identical edge texels, and read them identically, the macro height differs across the edge. That
is a crack, and a large one: it is the *base* height, kilometres of it, not a millimetre of detail.

With a pure generator, the two faces' edge texels would differ only by what 1.3 nm moves a smooth field,
because their parameterisations reach the shared edge 1.3 nm apart. That is harmless. But the maps exist for what a pure generator cannot do
(ground rule 4), and an erosion pass will not keep two faces' edge rows equal by accident. So make
agreement structural, independent of what fills the maps:

- **Ownership.** Every texel on an edge row is written once, by the edge's **owner**, the quad with
  the lower index. This includes the row's one-texel extension past each end, because the along-edge
  stencil near a corner reads it. It is copied into the other quad through the link, reversal included.
  A corner texel is shared by three faces on a cube sphere (four tiles on a plane grid), and takes the
  lowest index among them. Express the rule as a pure function: `MacroLayout.EdgeSource(quad, level,
  i, j)` returns the texel to copy from, or itself. The build job and the tests then apply the same
  rule. It needs a `SurfaceMath.QuadEdgeLink(in SurfaceDef, int quad, Edge)` covering all three
  topologies; `CubeEdgeLink` and the cross-quad branch of `Neighbour` already hold the data. A plane
  grid's outer edges have no link and are not copied. Where one edge row's extension texel is also
  another edge's border texel, the copy wins. That other edge's on-edge reads give it zero weight.
- **A read that is exact under reversal.** On an edge, the perpendicular coordinate is exactly 0 or
  `n_L`, so its weights are exactly `(0, 1, 0, 0)` and only the edge row contributes. Along the edge, a
  reversed link sees `t' = 1 − t` (exact, since `t` is a short dyadic) and the texels in reverse order. On
  a texel both sides take that texel with weight exactly 1. Between texels, two things make the result
  bit-identical anyway:
  - Write the Catmull-Rom weights as `(g(1−t), h(1−t), h(t), g(t))`, with `g(x) = x²(x − 1)/2` and
    `h(x) = (−3x³ + 4x² + x)/2`. Then reversal swaps the weights exactly.
  - Sum in symmetric pairs, `(w0·p0 + w3·p3) + (w1·p1 + w2·p2)`. IEEE addition is commutative, so the
    reversed sum is the same bits.

  The textbook form `w0 = (−t³ + 2t² − t)/2 …` and a left-to-right sum are algebraically equal and lose
  both properties.
- **A border of two texels.** The sample jobs' one-vertex border reaches up to one texel past the edge at
  the level they read (C1 makes `MacroLevel(d) ≤ d`). With `cell = clamp(floor(x), −1, n_L)` and
  `t = x − cell`, every stencil then stays within texels −2 to `n_L + 2`. Border texels hold the
  generator continued in this quad's own parameterisation, the same choice the height border makes.
  That keeps the macro's slope across an edge continuous to within interpolation error, rather than
  leaving each face with a one-sided extrapolation.

The result is that a `MacroSample` at a shared edge point is **bit-identical from both quads**, at every
level. That is stronger than the vertex positions there, which differ by 2e-16 relative. It is also what
an erosion pass later inherits for free. `MacroEdgesAgreeAcrossEveryLink` (Task 5) is the guard.

### C3 — The read is double from texel coordinate to sum

M4's stripes came from narrowing a noise cell's fraction to float: 2⁻²⁴ of a 10,400 km cell is 0.62 m
(`CLAUDE.md`, "axis-aligned lines"). A macro texel at Earth radius is 40.6–325 km, and a float fraction
of one quantises the read to 2.4–19 mm along each texel axis. That is the same staircase species, aligned
with the face grid instead of the world axes. The jobs already have `u` and `v` in double. Keep the texel
coordinate, the fraction, the weights and the sum in double, and narrow once, where `Height` already
does. The stored texels may be float.

### C4 — The maps hold climate, never a biome id

§4.6 lists "biome id" among the maps. A bicubic read of a categorical field is meaningless: halfway
between desert (2) and tundra (4) it reads grassland (3). Store the continuous fields:

| channel | meaning | generator |
|---|---|---|
| x | base height, metres | fBm, wavelength `MacroWavelength0`, amplitude `BaseHeightScale` |
| y | temperature, °C | latitude term (cube sphere only) − lapse rate × max(base height, 0) + fBm |
| z | moisture, 0–1 | fBm mapped to [0, 1] |
| w | ruggedness, 0–1 | fBm mapped to [0, 1]; scales amplitude independently of climate (C7) |

Then classify biomes *per vertex*, from the interpolated climate, with weights that are continuous in it:
inverse-square distance in normalised (temperature, moisture) space, raised to an integer sharpness and
normalised to sum to 1. A nearest-biome rule, or any kernel with finite support and a fallback, puts a
hard edge where the envelopes stop overlapping. The latitude term uses `(y / r)²` from the surface
point, so it needs no trigonometry and is smooth at the equator.

### C5 — `default(MacroSample)` must mean "no macro layer"

M5b adds per-band amplitude to `MacroSample`. Stored as a multiplier, a default-initialised sample has
amplitude zero and flattens the world, and every path that forgets to set it does so silently. Store a
**gain relative to one**, `BandGain` (low, mid, high), applied as `amp × (1 + gain)`:

- `default` is then the identity;
- `1 + 0` is exactly `1`, and `amp × 1` is exactly `amp`, so the fBm sum is the M4 sum bit for bit;
- the height still narrows once, with `BaseHeight` 0 adding exactly nothing.

Ground rule 1 rests on that, and `HeightIsBitIdenticalWithoutAMacroLayer` pins it. The octave sum keeps
its fixed order from octave 0 up. That is the "coarse evaluation is a strict prefix of a finer one"
property the geomorph target depends on (`LatticeNoise.Fbm`), and the gains, being a function of the
macro alone, are the same at both depths.

The disabled path still hands the jobs a valid map, one texel with `Enabled` false. A job whose
`NativeArray` field was never constructed fails the safety check at schedule time.

### C6 — Splat weights morph, and they take the unused uv slot

Elevation morphs with the geometry because the shader colours by it (`CLAUDE.md`, "Elevation morphs
too"). Splat weights are the same case, and not obviously. Fully morphed, three of every four child cells
collapse, and the surviving cell is drawn *at* the even vertices' positions. Three of its four corners
are odd vertices, though, and their attributes are their own: in cell (1..2, 1..2), vertices (1, 1),
(2, 1) and (1, 2) are drawn at (0, 0), (2, 0) and (0, 2). Unmorphed weights would shade the parent's
triangles with values sampled a fine vertex away, a colour pop at every swap. So the vertex carries a
**morph target splat**: the weights at the even vertex `(i & ~1, j & ~1)` from the coarse sample job, at
the parent's macro level. The shader lerps it on the same `k`, in ForwardLit only, since nothing else reads colour.

`TerrainVertex.Uv` (`TEXCOORD0`, float2) is written by `ChunkMeshJob` and read by nothing. The shader
declares it and never uses it. Retire it, and the two splats fit in its eight bytes as `UNorm8 × 4`
each, so the vertex stays **80 bytes**:

| attribute | format | content |
|---|---|---|
| `POSITION` | Float32 × 3 | unchanged |
| `NORMAL` | Float32 × 3 | unchanged |
| `COLOR` | UNorm8 × 4 | splat weights (biomes 0–3) |
| `TEXCOORD0` | UNorm8 × 4 | morph-target splat weights |
| `TEXCOORD1–3` | Float32 × 4 | unchanged |

Unity requires a stream's attributes in `VertexAttribute` enum order, which puts `COLOR` before
`TEXCOORD0`, so `TerrainVertex`'s fields must be reordered to match. That caps M5 at four biomes; more
needs another attribute or a splat texture, which is out of scope.

### C7 — Biomes scale the octaves by band, and that multiplies the relief demand

Each biome gives a gain per octave band. A band is defined by wavelength in metres, so it means the
same thing on any planet; `HeightParams` turns the edges into octave indices at setup. Biome gains are
blended by the splat weights and then scaled by the ruggedness channel. It is linear in the octaves
and slow in space, so it preserves band-limiting and the strict-prefix property (C5).

It also raises relief, and relief is what the LOD boundary spends its budget on (`CLAUDE.md`, "Terrain
roughness spends the same boundary budget"). The demand `C × HeightScale / S0` becomes
`C × HeightScale × (1 + maxGain) / S0`. Earth's allowance at `SplitFactor` 3 is 0.1146 against a demand
of 0.0130 at `C` 15 (`Docs/M4_FloatingOrigin_Plan.md` C1): **8.8× headroom**, so an effective amplitude
of up to about 4× still leaves 2×. Keep the shipped biome table's largest effective amplitude at or below
3 and put it into `EarthConfigurationClearsTheReliefAllowance`. The selector's end-of-session report of
the roughest chunk stays the arbiter, as in M4.

Non-linear shaping (flatten, sharpen, terrace curves, which §4.6 mentions) is deferred. It changes the
relief tail in ways `C` has not been measured for, and it is not band-limited.

### C8 — `HeightAt` must read the macro layer, so the surface needs an inverse

`TerrainRoot.HeightAt` passes `MacroSample.Zero` today. With continents, that understates the ground by
the base height, kilometres. `TopologyFlyCamera` then believes it is kilometres higher than it is, flies
too fast, and sets its near plane to a hundredth of a clearance it does not have. The near plane then
cuts into the ground.

The map read needs `(quad, u, v)`, and `HeightAt` has only a point. Add **`SurfaceMath.Locate(in
SurfaceDef, double3) → (quad, u, v)`** for all three topologies:

- **Cube sphere:** pick the face by the largest-magnitude axis, project onto its plane, and undo the
  tangent adjustment with `atan`.
- **Plane grid:** tile index by `floor`.
- **Cylinder:** angle and length.

It is a tool path, never a generation path, so its rounding cannot crack anything. A point on an edge
may be located on either face; C2 makes that irrelevant.

### C9 — `EarthStress` gets its own material, and the biome colours are globals

Both scenes use one material today (`Assets/Materials/TerrainTriplaner.mat`). Its elevation bands, 10 m
and 30 m, are `SampleScene`'s tuning, and at `HeightScale` 9000 everything in `EarthStress` above 30 m is
drawn as snow. The biome path is a `shader_feature_local` keyword, `_SONOMA_BIOMES`, enabled only on a
new `TerrainBiomes.mat` for `EarthStress`. Ground rule 1 then holds by construction: `SampleScene`'s
material never compiles the path. It must be `_local` and a `shader_feature`, not a global
`multi_compile`. A material that enables it is what keeps the variant in a build, and a global keyword
would leak between scenes in the Editor.

Under the keyword the ground colour is the splat-weighted biome colour; the cliff overlay stays
slope-driven; the elevation bands are unused. The colours come from the `BiomeDefinition` assets:
`TerrainRoot` pushes them as a global `_SonomaBiomeColors[4]` alongside `_SonomaMorphRanges`, for the
same SRP-batcher reason. Writing them into the material instead would, in the Editor, permanently edit
the `.mat` asset from play mode. Textures per biome wait for M7, with the triplanar precision limit they
inherit (M4 C4).

### C10 — Close-up roughness is a goal, and the relief allowance is its budget

Near the ground the stress scenes are smooth, and not for want of octaves:
`DeepestDepthIsNotOctaveStarved` holds in every scene. At persistence 0.5 each octave's amplitude
halves with its wavelength, so every octave is equally steep, with slope `HeightScale / S0`. Small
features are correspondingly tiny:

| scene | `HeightScale / S0` | features 10 m across | finest octave |
|---|---|---|---|
| `EarthStress` | 0.00087 | ~9 mm | ~1 mm, 1.24 m across |
| `SmallPlanetStress` | 0.0023 | ~23 mm | ~3 mm, 1.24 m across |
| `RingworldStress` | 0.0023 | ~23 mm | ~4 mm, 1.67 m across |
| `TerrariumStress` | 0.019 | ~19 cm | ~2.4 cm, 1.31 m across |
| `SampleScene` (unchanged) | 0.10 | ~1 m | — |

Real ground at 10 m has bumps of tens of centimetres to a metre. M5 gives each stress scene a
roughness target, 10 m features of about **0.2–0.5 m**, and reaches as much of it as the relief
allowance permits. Where it cannot, it records the shortfall and why.

**The allowance reads as a slope.** A chunk's half-relief must stay under `LodMath.MaxHalfRelief`, a
fixed fraction of its size, and for a chunk-wide ramp the half-relief is half the slope times the size:

| LOD settings | allowance | chunk-wide slope it allows | with the 2× margin the tests require |
|---|---|---|---|
| sphere, `SplitFactor` 3, `MorphStartFraction` 0.15 | 0.1146 | 0.23 (13°) | 0.11 (6°) |
| sphere, `SplitFactor` 4, 0.15 | 0.4146 | 0.83 (40°) | 0.41 (22°) |
| cylinder, `SplitFactor` 3, 0.15 | 0.39 | 0.78 (38°) | 0.39 (21°) |

So the two cylinders have room, and the two spheres probably do not: real-ground roughness on a
sphere likely needs `SplitFactor` 4. That costs about 1.7× the chunks; M4 C2 measured 8,794 at depth
19 with preload 1.15, so re-measure at preload 1.0. It also raises the main thread's selection time with
them, which is already over budget and is M6's to fix (`SonomaRevisedPlan.md` §5). **This choice belongs
to the user, made with the numbers:** for each sphere, either `SplitFactor` 4 with its measured working
set, or the roughness `SplitFactor` 3 can carry. Do not make it silently.

**The lever is a two-slope spectrum.** `TerrainSettings` gains `FinePersistence` and
`RoughnessCrossover`, a wavelength in metres. Octaves finer than the crossover take `FinePersistence`;
coarser ones keep `Persistence`. The continents and large-scale relief stay as tuned, and only the
small scales roughen. The default sets `FinePersistence` equal to `Persistence`, the same multiply at
every octave, so the sum is bit-identical. `SampleScene` is untouched, and test 1 covers it. The
biome band gains (C7) then modulate on top: rough mountains, smooth plains.

Raising `Persistence` alone does the same less selectively. On Earth, 0.6 makes 10 m features about
0.33 m, but it compounds over every octave above them. `C` grows roughly as `(2p)^depth`
(`CLAUDE.md`), and a rough extrapolation from that already puts `SmallPlanetStress` past its
allowance at depth 17.

**`C` is measured, not assumed.** Above persistence 0.5 it climbs steeply with depth, so each
scene's `C` must be re-measured at its new spectrum, the way M4 measured Earth's:

- sample 20,000 random nodes at `MaxDepth`, and at the crossover depth, where the growth changes slope;
- record the maximum and the 99.9th percentile;
- set `C` at 1.5× the maximum.

The runtime roughest-chunk report remains the arbiter. `SkirtFraction` follows `C`, since the gap it
hides is `2 × C × HeightScale / S0`, and so may the budget.

A rougher spectrum may make the finest octave's features visible at `MaxDepth`'s vertex spacing. If
it does, a deeper `MaxDepth` is the response, at more chunks again. Measure it; do not assume it.

---

## Task 1 — The pure macro core (M5a)

UnityEngine-free, so the harness runs it (`unity-verification-without-batchmode`):

- `Core/Generation/MacroLayout.cs`
  - level sizes, texel counts and per-quad and per-level offsets into one flat array;
  - `MacroLevel(depth)` (C1);
  - `EdgeSource` (C2), plus the generator's octave limit per level.
- `Core/Generation/MacroGenerator.cs` — `Texel(in SurfaceDef, in RootQuad, in MacroParams, int level,
  int i, int j) → float4`, the four channels of C4 at the texel's surface point. It uses `LatticeNoise`
  with a distinct seed per channel.
- `Core/Generation/MacroSampler.cs` — the bicubic read of C2 and C3:
  - `Weights(double x, int n, out int cell, out double4 w)` per axis;
  - `Combine(...)` over a fetched 4 × 4 neighbourhood.

  The caller fetches the sixteen texels. That keeps the arithmetic in pure code, as `ChunkMeshLayout`
  does for the mesh.
- `Core/Generation/MacroParams.cs` — blittable; validated once in `MacroParams.Create`. It covers level
  count ≥ 1, wavelengths and amplitudes finite, and (in M5b) a biome count of 1–4.
- `Core/Surface/SurfaceMath.cs` — `QuadEdgeLink` (C2) and `Locate` (C8).

## Task 2 — Maps, jobs and the height function (M5a)

- `Core/Generation/MacroMaps.cs` (Unity.Collections; not pure):
  - owns one persistent `NativeArray<float4>`;
  - `Build` schedules a Burst `MacroBuildJob : IJobParallelFor` over every texel, runs the edge copy
    from `EdgeSource`, and completes before returning;
  - `Sample(quad, level, u, v) → MacroSample` fetches the neighbourhood and calls `MacroSampler`;
  - log the build time once. It is a startup hitch, and its size should be known (expect tens of
    milliseconds; record it).
- `MacroSample` gains `Temperature`, `Moisture` and `Ruggedness` beside `BaseHeight`. M5a's height
  consumes only `BaseHeight`, already in the formula; the others are generated and tested now, and
  consumed in M5b.
- `HeightSampleJob` and `CoarseHeightSampleJob` take `[ReadOnly] NativeArray<float4> Macro`,
  `MacroParams` and a level, and replace `MacroSample.Zero` with the read at the vertex's `(Node.Quad, u,
  v)`. The fine job reads `MacroLevel(depth)`, the coarse job `MacroLevel(depth − 1)` (C1). Many jobs
  read the array at once, which the safety system allows for `[ReadOnly]`.
- `GenerationScheduler` passes the maps and level into both jobs.
- `TerrainRoot`:
  - builds the maps in `Start` before the scheduler exists;
  - disposes them in `OnDestroy` **after** `_scheduler.Dispose()`, which completes in-flight jobs.
    Freeing the maps first throws in the Editor and reads freed memory in a build;
  - `HeightAt` locates the point and reads the top level (C8).
- `Systems/Configuration/MacroLayerSettings.cs` — a ScriptableObject holding the generator parameters.
  `TerrainSettings` gains `public MacroLayerSettings Macro;`, where null means no macro layer and the M4
  path. Create assets through the Editor menu, not by hand-writing YAML; M2 left an asset stale doing
  that.

## Task 3 — Biomes and splats (M5b)

- `Systems/Configuration/BiomeDefinition.cs` — a ScriptableObject:
  - display colour;
  - climate centre: temperature in °C, moisture 0–1;
  - gains for the low, mid and high bands (C7).

  `MacroLayerSettings` gains a list of up to four, a sharpness, the band edge wavelengths and the
  ruggedness range. `MacroParams` flattens them into a fixed four-entry table.
- `Core/Generation/Biomes.cs` (pure) — `Classify(in MacroSample, in MacroParams) → float4` (C4), then
  `BandGain(weights, ruggedness, in MacroParams) → float3` (C7). The sample jobs call both and write
  the gains into the `MacroSample` they pass to `Height`.
- `TerrainHeightFunction.Height` applies `BandGain` per octave through a `LatticeNoise.Fbm` overload
  taking the gains and the band edges. `HeightParams` gains `LowBandEnd` and `MidBandEnd`, as octave
  indices computed in `Create` from the wavelengths by exponent. With gains of zero the overload must
  return the original's bits (C5); keep the original for the disabled path anyway.
- Both sample jobs write the splat weights to a new output array. `ChunkMeshJob` packs them into `COLOR`
  and the coarse sample's at the even vertex into `TEXCOORD0` (C6); skirts copy them with the top
  vertex. Update `TerrainVertex`, `ChunkMeshBuffers.VertexAttributes` and `GeomorphTests`' copy of the
  layout together.
- `SonomaTerrainTriplanar.shader`, ForwardLit only:
  - `#pragma shader_feature_local _SONOMA_BIOMES`;
  - under it, `COLOR` and `TEXCOORD0` in `Attributes`, a splat varying lerped on the morph's `k`, and
    a biome-weighted ground colour, renormalised, replacing the band blend;
  - outside the keyword nothing changes;
  - declare `_SonomaBiomeColors[4]` outside `UnityPerMaterial`, beside the other globals, and update
    the header's attribute list.

## Task 3b — Close-up roughness (M5b)

After the biomes, since both spend the relief allowance (C7, C10):

1. `TerrainSettings` gains `FinePersistence` and `RoughnessCrossover`. `HeightParams.Create` turns
   the crossover into an octave index by exponent, as it does `K0`, and validates `FinePersistence`
   like `Persistence`.
2. `LatticeNoise.Fbm`, and its band-gain overload, switch persistence at that octave. At the default
   (`FinePersistence == Persistence`) they must return the original bits.
3. For each stress scene, choose a spectrum aimed at C10's target. Re-measure `C` there by sampling,
   and compare the demand `C × HeightScale × (1 + maxGain) / S0` with the allowance at twice the
   margin.
4. Where a sphere cannot reach the target at `SplitFactor` 3, measure the `SplitFactor` 4 working set
   with the replica scan that set the M4-era budgets. Then bring both options to the user.
5. Move every pinned constant with the retune: `Persistence`, `FinePersistence`, the crossover, `C`,
   `SkirtFraction`, and `SplitFactor` and `Budget` if they change. The pins live in
   `EarthScaleTests`, `StressSceneTests` and their asset tests. Record the per-scene outcome, as
   10 m feature height against target, in the amendments.

## Task 4 — Configuration

| asset | contents |
|---|---|
| `Assets/Settings/EarthMacro.asset` | `LevelCount` 4; base height wavelength `S0`, `BaseHeightScale` ~3000 m; temperature 30 °C equator, −30 °C pole, lapse 6.5 °C/km, noise ±5 °C; moisture and ruggedness fBm; band edges 50 km and 500 m |
| `Assets/Settings/Biomes/*.asset` | four starting biomes, e.g. temperate grassland (15 °C, 0.6), desert (28 °C, 0.15), tundra (−10 °C, 0.4), rainforest (25 °C, 0.85), with gains keeping the largest effective amplitude ≤ 3 (C7) |
| `EarthTerrainSettings.asset` | gains `Macro: EarthMacro` |
| `Assets/Materials/TerrainBiomes.mat` | `_SONOMA_BIOMES` on; `EarthStress`'s `ChunkMaterial` (C9) |

The macro values are **starting points**: tune them by eye from orbit and from the ground, and record
the final values and reasons in the amendments. With base height added, Earth's detail `HeightScale`
of 9000 may want lowering; if it changes, `EarthScaleTests`' copy of it must move with the asset, which
`EarthSettingsAssetTests` enforces. `SampleScene` and `TerrainSettings.asset` are untouched, apart from
the asset gaining `Macro: {fileID: 0}` if re-saved.

The other three stress scenes get a macro layer too, each with its own asset and with base heights scaled
to the world: kilometres on `SmallPlanetStress` and `RingworldStress`, hundreds of metres in
`TerrariumStress`. They can share the biome definitions. On the two cylinders the temperature has no
latitude term (C4), so their climate is noise and lapse rate alone. Each scene's `HeightScale`,
`SkirtFraction` and budget is pinned by `StressSceneTests` and `StressSceneAssetTests`, which move with
any retune. `TerrainBiomes.mat` serves all four.

## Task 5 — Tests

### Pure (run outside the Editor)

1. **`HeightIsBitIdenticalWithoutAMacroLayer`** — over a sample of points and depths on both scenes'
   configurations, `Height` with `default(MacroSample)` equals M4's function body, copied verbatim into
   the test, to the bit. Use the original `Fbm`, not the overload. Run it with `FinePersistence` equal
   to `Persistence` too (C10). This is ground rule 1 as an assertion, and C5's trap.
2. **`MacroEdgesAgreeAcrossEveryLink`** — build every level of a cube sphere's maps with the pure
   generator and `EdgeSource`. For all 24 links, sample both quads at the same edge points, reversed
   where the link says, at several depths and at offsets within texel cells, including the last cell at
   each corner. Assert bit-identical `MacroSample`s. Then substitute the textbook weights with a
   left-to-right sum and confirm it fails (C2), the way `RebaseIsExactAfterAThousandShifts` was checked
   against `position -= shift`. If it still passes, it is not sampling enough of the eight reversed
   links.
3. **`MacroReadIsExactOnTexels`** — at depth `d ≤ LevelCount − 1`, every chunk vertex's read equals
   its texel exactly (C1).
4. **`MacroLevelIsDepthOnly`** and **`MacroLevelsAreBandLimited`** — the level is a function of depth
   alone, and each level's octave limit satisfies the four-texel rule, read by exponent (C1).
5. **`LocateInvertsSurfacePoint`** — `Locate(SurfacePoint(q, u, v))` round-trips on all three
   topologies, to a stated tolerance; points on edges and corners land on a valid quad (C8).
6. **`BiomeWeightsArePartitionOfUnityAndContinuous`** — weights sum to 1 within float rounding,
   are non-negative, and change by a bounded amount for a small climate step everywhere, including far
   outside every biome's centre (C4).
7. **`EarthConfigurationClearsTheReliefAllowance`** and
   **`StressSceneTests.ReliefAllowanceLeavesTwiceTheEstimatedDemand`**, extended — the demand includes
   the shipped table's largest effective amplitude (C7) and each scene's re-measured `C` (C10). Both
   are constants that test 9 and `StressSceneAssetTests` check against the assets.

### Editor

8. **`MacroJobMatchesTheManagedBuilder`** — the Burst build equals the pure builder. Assert to the bit
   first. If it fails only by an ULP in a few texels, the cause is Burst's `tan`, inside `SurfacePoint`,
   against managed `Math.Tan`. Then record it and relax the check to that ULP bound. C2's exactness does
   not depend on Burst and managed agreeing, since both faces read one stored texel.
9. **`EarthStressRunsTheMacroLayer`** — `EarthTerrainSettings` references `EarthMacro`, and its biome
   gains match test 7's constant. `EarthStress` uses `TerrainBiomes.mat` with the keyword on;
   `SampleScene`'s settings have no macro layer and its material has the keyword off. Read through
   `SceneConfig`, as the existing scene checks are.
10. **`ChildAtFullMorphEqualsParent`**, extended — each child vertex's morph-target splat equals the
    parent's splat at the corresponding vertex (C6).
11. **`ShaderMorphsTheSplat`** — ForwardLit declares `COLOR` and `TEXCOORD0` under `_SONOMA_BIOMES`
    and lerps the splat on the morph factor. It is a source check, like `ShaderMorphsInEveryPass`.
12. **`MacroMapsOutliveTheJobs`** — tearing a terrain down mid-generation disposes the maps after the
    scheduler, with no safety-system error.

### Pure, for close-up roughness (C10)

13. **`FineOctavesTakeFinePersistence`** — the octave amplitudes follow `Persistence` down to the
    crossover and `FinePersistence` below it, the crossover falls on the octave the exponent read
    says, and a coarse evaluation is still a strict prefix of a finer one (C5).
14. **`CloseUpRoughnessMeetsItsTarget`** — for each stress scene, the configured spectrum's amplitude
    at the octave nearest a 10 m wavelength. It must be at least the scene's target, or equal to its
    recorded shortfall, so a retune that quietly smooths the ground fails. The figure is an octave
    amplitude, not a measured relief, and the test says so.

## Task 6 — Documentation

- `CLAUDE.md`:
  - status to M5;
  - the macro layer under Height Function and Generation Pipeline;
  - Non-Obvious entries for C1 (pyramid by depth), C2 (edge ownership and the symmetric weights),
    C4 (no categorical maps), C5 (`default` is the identity), C6 (splat morph and attribute order) and
    C8 (`HeightAt` and `Locate`), and C10 (the relief allowance as a slope budget, and the two-slope
    spectrum);
  - the vertex table: `COLOR` and `TEXCOORD0` replace uv, still 80 bytes;
  - test counts.
- `SonomaRevisedPlan.md` §4.6:
  - record corrections, as M3 and M4 did, rather than rewriting: no biome id in the maps (C4); a
    pyramid rather than one 129² or 257² map (C1);
  - splat weights need a morph target (C6);
  - the band edges are wavelengths.
- Shader header: the new attributes, the keyword, the global colour array.

---

## Verification

### Agent-side, without the Editor

Compile `Sonoma.Core`, `Sonoma.Editor` and `Sonoma.Tests.EditMode` with Unity's Roslyn and the saved
`.rsp` arguments, with outputs redirected to scratch. Then build and run the harness. All pure tests
must be green: the 69 that already pass this way, plus tests 1–6, 13 and 14, and the extended test 7. The harness
picks up new files by import, so keep the pure files free of `Unity.Collections`, `Unity.Jobs`,
`Unity.Burst`, `UnityEngine` and `Sonoma.Systems`.

### Human-side, in the Editor

1. Console clean; Test Runner all green; the Burst witness still reports compiled.
2. **`SampleScene` unchanged** — looks the same, and its relief and budget reports at exit match M4's.
3. `EarthStress` (M5a): continents visible from orbit and stable as the camera descends. Fly low along a
   cube edge and across a cube corner: no crack and no step in the ground. The fly camera's clearance
   stays right over high ground: the near plane does not clip it, and speed does not jump at the
   coast.
4. `EarthStress` (M5b): biomes from orbit with smooth borders; no colour pop at LOD swaps (watch one
   biome border while descending through several depths); no colour seam along cube edges.
5. Repeat 3 and 4 in `SmallPlanetStress`, `RingworldStress` and `TerrariumStress`. On the cylinders,
   watch the column seam where the ring closes, the joins between rows, and the open rims.
   Close-up roughness (C10): from 1 to 10 m above the ground in each scene, small relief is visible,
   with no LOD cracks while descending through it, and no relief warning.
6. On leaving play mode, in every stress scene: the roughest-chunk report against the relief allowance
   (C7), the peak working set against that scene's budget, and the macro build time.
7. Profiler, a development build, exported with Tools → Sonoma → Export Profiler Capture. Compare
   worker job time per chunk with M4's (`Docs/M4_FloatingOrigin_Plan.md` amendment 25); it should rise
   by the cost of the read and the classification. Main-thread `TerrainRoot.Update` must not rise
   (ground rule 3).

The pure tests for the macro layer (Task 5) should cover a cylinder as well as a cube sphere. The edge
rule's links differ there: a ring closes on itself between its last column and its first, and a
terrarium's rows meet at four-root corners.

---

## Definition of done

- [ ] `SampleScene` renders and reports exactly as at M4; `HeightIsBitIdenticalWithoutAMacroLayer` passes.
- [ ] Each root quad has a macro pyramid built at startup, band-limited by depth, read in double (C1, C3).
- [ ] Macro samples agree to the bit across all 24 cube links at every level, and the guard fails under the textbook weights (C2).
- [ ] `HeightAt` includes the macro layer through `SurfaceMath.Locate` (C8).
- [ ] Up to four `BiomeDefinition`s classify per vertex from climate, scale the octaves by band, and reach the shader as morphed splat weights in an 80-byte vertex (C4–C7).
- [ ] All four stress scenes fly from altitude to the ground with continents and biomes, and no cracks, seams or colour pops.
- [ ] Each stress scene reaches C10's close-up roughness target within its relief allowance, or has its shortfall and the `SplitFactor` 4 alternative measured, decided with the user and recorded.
- [ ] All tests pass; the macro build time and the per-chunk job cost are recorded.
- [ ] `CLAUDE.md`, §4.6 and the shader header updated; amendments section written.

## Amendments from implementing M5

The plan above is the plan as written. Building it will change some of it; record those changes here
rather than editing them into the tasks, so the plan still reads as what was decided up front.

### M5a (2026-10-18)

1. **`QuadEdgeLink` is a try-form**, `bool QuadEdgeLink(in SurfaceDef, int quad, Edge, out EdgeLink)`,
   because a plane grid's rim and a cylinder's ends have no neighbour. `Neighbour` now crosses quads
   through it, so root-quad adjacency lives in one place and the existing neighbour tests cover the new
   function. `EdgeLink.Face` is the neighbouring quad's index on every topology.
2. **`EdgeSource` returns the source texel and whether it is a copy**, and `MacroLayout.EdgeCopies`
   lists every (destination, source) pair. `MacroMaps.Build` and the tests' managed builder apply the
   same list. Every source is a texel nothing copies into, so the copies run once, in any order;
   `EdgeCopiesReadOnlyOwnedTexels` holds that. A cylinder of one column links its East edge to its own
   West, so ownership is the lower `(quad, edge)` and a corner the lowest `(quad, i, j)`.
3. **The caller does not fetch the sixteen texels.** `MacroSampler.Sample<T>` is generic over an
   `IMacroTexels` struct, a `NativeArray` reader in the jobs and an array reader in the tests. That way
   the whole read, indices included, is pure code, and Burst specialises it, so there is no virtual call.
4. **`MacroSample.BaseHeight` is a double**, so the height narrows once (C3). The climate fields are
   floats. `default(MacroSample)` still adds exactly nothing; `HeightIsBitIdenticalWithoutAMacroLayer`
   compares against frozen copies of M4's `Height` *and* `Fbm`, so a later change to either is caught.
5. **The edge test's mutation check needs deep depths.** At up to two depths past the top level, the
   cell fraction has only a few bits. Every weight, and every product of a weight with a float texel,
   is then exact in double, so the textbook read *passes*. At Earth's depth 19, level 3, the fraction
   has 16 bits and the products round. On reversed links the test samples every edge vertex while an
   edge has at most 1,024 intervals, then the four at each end and a fixed random 128, down to depth 24.
   There the textbook read disagrees on 5,552 of the 34,880 reversed-link samples, and the real one on
   none. Non-reversed links stop two depths past the top level, since both sides do the same arithmetic
   there. The test fills the maps
   with independent random texels rather than the generator, because the edge rule must hold whatever
   fills them; a second test runs it on generated maps.
6. **Exactness across a reversed link assumes `R − 1` is a power of two**, true of every shipped
   resolution. `ChunkGrid.VertexUV` then gives dyadic `u`, and `1 − u` is exact. At other resolutions
   the two sides differ by rounding, sub-micrometre, and bounded, as the vertex positions already are.
7. **The coarse job's depth-0 border reaches two texels past the edge**, one more than the stored
   border allowed for. As first built, the sampler clamped its cell to −1 … n, so every read stayed
   inside the stored −2 … n + 2. That read extrapolated, and this item called it unused, like the rest
   of a depth-0 morph target. **Wrong**, and corrected in review (item 12): roots morph, so it is drawn.
8. **Generator details the plan left open.**
   - The macro's lacunarity is fixed at 2, which `MaxOctave(level) = K0 + level` assumes.
   - Its seeds are offset from the detail noise's. With the same seed, the macro would otherwise reuse
     detail octaves 0–6's lattices at the same scale.
   - Channels are a seed stride apart.
   - Climate is `centre + amplitude × fBm`: moisture and ruggedness centre on 0.5 and are clamped, and
     temperature centres on `EquatorTemperature`, which on a surface with no poles holds everywhere.
   - `MacroParams.Create` refuses a configuration whose top level would carry no octave.
9. **All four stress scenes get a macro layer in M5a**, not just `EarthStress`, so the cylinder edge
   rule (the ring's closing seam, the terrarium's four-root corners) gets a fly-through too. Base heights
   are about a third of the detail `HeightScale` on the planets and the ring, and equal to it in the
   terrarium, where "hundreds of metres" asks for it:

   | scene | levels | `BaseHeightScale` | texels | roughest C, no macro → macro |
   |---|---|---|---|---|
   | `EarthStress` | 4 | 3000 m | 551,640 | 6.23 → 6.65 (depth 19) |
   | `SmallPlanetStress` | 4 | 2000 m | 551,640 | 5.24 → 5.69 (depth 17) |
   | `RingworldStress` | 4 | 1500 m | 735,520 | 5.80 → 6.00 (depth 16) |
   | `TerrariumStress` | 3 | 200 m | 762,208 | 2.98 → 4.63 (depth 6) |

   C was measured on 300 random nodes per depth (600 in the terrarium), at 17 × 17 vertices each, so
   these are samples, not tails. All four stay well inside the pinned C of 15, 15, 15 and 9, and the
   runtime's roughest-chunk report remains the arbiter. The terrarium at 300 m reached 5.74, too close
   to 9 for an estimate. Four levels there would be 47 MB over 32 roots, and level 2's 60 m texels are
   already finer than a macro layer is for, hence three. The cylinders' `EquatorTemperature` is 18 °C,
   since without poles it holds everywhere. The managed builder takes about 1.5 s per scene on .NET 8.

   **Measured in the Editor (2026-10-18).**
   - *Build times.* The Burst build took 188–195 ms on Earth (551,640 texels, 8.4 MB), 224 ms on
     the ring and 215 ms in the terrarium. That is a one-off startup hitch, several times the plan's
     "tens of milliseconds" guess. Each texel is a tangent-adjusted surface point and four
     double-precision fBm sums of up to seven octaves, so about 0.35 µs per texel across the
     workers. A development build will give the real figure.
   - *Peak working sets.* Earth 4,158 against 5,800; ring 4,496 against 5,600; terrarium 2,824
     against 3,600.
   - *Relief.* No stress scene warned about relief.
   - *Export.* The macro map export of `EarthStress` gave base height −3,474 … 2,799 m, against
     detail to octave 7 of −8,147 … 4,618 m and a total of −9,042 … 5,226 m. It gave temperature
     −34.7 … 33.7 °C, and moisture and ruggedness over their full 0–1 range.
   - *Tests.* All tests pass, including `MacroJobMatchesTheManagedBuilder` bitwise, so Burst's `tan`
     matched managed `Math.Tan` on every texel checked and no ULP relaxation was needed.
10. **The assets were written as YAML, not through the Editor menu.** The Editor was open and is the
    user's. `MacroMapsTests.StressScenesRunTheirMacroLayers` loads each asset through `AssetDatabase` and
    checks every field against `MacroTests.StressMacros`. A field the YAML got wrong would load as its
    default, and the test catches exactly that, which is the stale-asset failure the plan warned about.
11. **`HeightAt` adds one `Locate` and one sixteen-texel read per call** on the main thread. The fly
    camera calls it once a frame, which costs microseconds. It is a tool read, not per-node work, so
    ground rule 3 stands.
12. **Review of PR #8 (2026-10-18).**
    - **The depth-0 coarse read is drawn, so it no longer extrapolates.** `LodMath.MorphRange(0)` is a
      real range, so beyond `start_0` every root chunk draws its coarse target. The coarse normals
      at its edge vertices then took a central difference through a Catmull-Rom extrapolation at
      t = −1, weights (2, −3, 3, −1): a faint shading seam along the root-quad edges seen from far out.
      M2's comment that the depth-0 target is unused was wrong too.
      - The fix: the stored border is three texels and the cell clamps to −2 … n + 1, so a read two
        texels out lands on a texel.
      - `MacroReadIsExactOnTexels` now covers the coarse grid at every depth, depth 0 included, and
        fails with the old border of two.
      - Texel counts rise by 2%: Earth 563,736 (8.6 MB), ring 751,648, terrarium 793,184.
    - **The relief and skirt tests count the macro layer.** They sized the demand as
      `C × HeightScale / S0` alone, so nothing responded to `BaseHeightScale`. They now use
      `MacroTests.Demand = (C × HeightScale + C_m × BaseHeightScale) / S0`.
      - `C_m` = 7 is the macro's own relief factor. The roughest base-height-only node over 1,000
        sampled per depth, at 33 × 33, was 4.12 on Earth, 4.70 on the small planet, 3.52 on the ring
        and 2.76 in the terrarium; 7 is half again over 4.70.
      - The tests keep 2× against the detail estimate and ask 1.25× against the sum (the user's
        choice). The sum stacks two padded worst cases on one node, and the terrarium, at 1.31×,
        would otherwise have had to drop to about 40 m of base height. It now fails above about
        220 m, and its measured combined worst, 4.63, is well inside.
      - Skirts cover twice the sum, so `SkirtFraction` went to 0.033 on Earth (from 0.03), 0.09 on the
        small planet and the ring (from 0.08), and 0.66 in the terrarium (from 0.35). Skirts are
        underground, so the cost is culling bounds.
    - **`HeightAt`'s body is `TerrainHeightFunction.HeightAtPoint`**, which the test now calls instead
      of rebuilding. The test also checks that reading level 0, or passing `MacroSample.Zero`, would
      fail it; both mutations were run and failed.
    - **`MacroSampler.Read`** holds the enabled-or-zero read the two jobs, `MacroMaps.Sample` and
      `HeightAtPoint` share.
    - **`MacroMaps.Build` frees the pyramid if the build throws**, instead of leaking it with nothing
      left to dispose it.
    - **`MacroParams.UnreadLevels(maxDepth)`**: levels above `MaxDepth` are built and never read, and
      `TerrainRoot` warns on them, as it does for `IsOctaveStarved`.
    - **The jobs derive their macro level.** Each sample job carried a `MacroLevel` field that callers
      had to set, `MacroLevel(depth)` or `MacroLevel(depth − 1)`. A caller that forgot it read level
      0 silently, and `GeomorphTests` and `WorkingSetTests` already omitted it, harmlessly only
      because their layer was disabled. Both jobs now compute it from `Node` and `Macro.Layout`, so
      the parent-level rule lives in `CoarseHeightSampleJob` and every caller exercises it.
    - **`ChildAtFullMorphEqualsParent` runs over a real macro layer too.** It had run only with the
      layer disabled, so nothing checked that the coarse job reads the parent's level. Reading the
      child's own level would put one more macro octave into the morph target, about 190 m on
      Earth at depth 1, and every swap up to the top level would pop by it. The test now runs once
      with no layer and once over a four-level map, with a depth-3 child, where the two levels
      differ. It also checks that the layer moves more than half the parent's vertices by over
      a metre.
    - **Deferred: the build's cost.** Fusing the four channels' fBm loops, and writing the lower levels
      from the top level's per-octave partial sums where texels coincide, would save perhaps a third
      of 200 ms of one-off startup. That is recorded under M6 in `SonomaRevisedPlan.md` §5, to be
      measured in a development build first.

### M5b, biomes (2026-10-18)

1. **The shipped biome amplitudes are all at most 1, and each scene's ruggedness range sets its
   largest amplitude.** C7 asked only that the largest effective amplitude stay at or under 3. With
   it multiplying the detail noise's relief demand, two scenes have almost no room. Under the relief
   tests' 2× rule on the detail estimate and 1.25× on the sum with the base height, the most
   amplitude each scene can carry is:

   | scene | largest amplitude allowed | ruggedness range | largest amplitude shipped |
   |---|---|---|---|
   | `EarthStress` | 4.4 | 0.5–2.5 | 2.25 |
   | `SmallPlanetStress` | 1.66 | 0.5–1.6 | 1.44 |
   | `RingworldStress` | 5.7 | 0.5–2.5 | 2.25 |
   | `TerrariumStress` | 1.08 | 0.5–1.0 | 1.0 |

   A shared table that amplified would have to fit the terrarium. So the biomes only shape and
   smooth the terrain (grassland 0.7/0.6/0.7, desert 0.6/0.4/0.8, tundra 1.0/0.9/0.9, rainforest
   0.8/0.8/0.6 on the low/mid/high bands), and ruggedness, per scene, lifts the mid and high bands.
   Task 3b's `SplitFactor` decision for the spheres may raise the limits. The detail and combined
   demands use `MacroParams.MaxAmplitude`, so raising a ruggedness range fails a test.
   `SkirtFraction` followed the larger demand: Earth 0.07, small planet 0.12, ring 0.18, terrarium
   unchanged at 0.66.
2. **Ruggedness scales only the mid and high bands.** The low band holds the continental shapes and
   is left to climate. Ruggedness, like the biome gains, is stored as a gain relative to one, so a
   `MacroConfig` that leaves it unset does not flatten the world (C5).
3. **The band edges live in `HeightParams` and are set from the macro asset.**
   `HeightParams.WithBands(low, mid)` turns the two wavelengths into octave counts by exponent, as
   `K0` is. `TerrainRoot` calls it when there is a macro layer. Without one every gain is zero and
   where the bands fall changes nothing. On Earth, 50 km and 500 m put octaves 0–7 in the low band
   and 8–14 in the mid band. The terrarium uses 2 km and 50 m, since its world is 10 km across.
4. **Classification is relative to the nearest biome**, `(d_nearest² / d_b²)^sharpness`, then
   normalised. The largest weight before normalising is exactly 1, so no sharpness overflows or
   underflows a float, and a distance floor of 1e-4 (normalised) keeps a sample on a biome's centre
   finite and continuous. No transcendental functions, so Burst and managed agree.
5. **The splat travels packed.** Both sample jobs write `Biomes.Pack(weights)` as a `uint` per sample,
   which the mesh job copies into `COLOR` and, from the coarse job at the even vertex, into
   `TEXCOORD0`. `GeomorphTests` compares the bytes exactly. Rounding leaves them summing to within two
   of 255, and the shader renormalises after interpolation. A vertex with no weight at all, a scene
   with the biome material but no biomes, falls back to the low band's colour rather than black.
6. **`TerrainBiomes.mat` and the biome assets were written as YAML**, like the macro assets.
   `MacroMapsTests` checks every field against `MacroTests.BiomeTable`. It also checks each stress
   scene's chunk material and keyword, and that `SampleScene`'s material has the keyword off.

7. **The moisture scale went from 0.25 to 0.5, prompted by a fly-through.** On Earth the south pole
   looked green beside a tundra north. That turned out to be lighting: `EarthStress`'s sun shines
   along (−0.32, −0.77, 0.56), so the south pole is on the night side and lit by the skybox's ambient
   alone. The biome export showed tundra there. The investigation did find a real fault, though.
   At a moisture scale of 0.25 the whole 0–1 moisture range counted as four units of climate
   distance, as much as 60 °C. Cold, wet ground at −12 °C and moisture 1.0 then sat as near
   rainforest (24 °C, 0.85) as tundra (−8 °C, 0.40), and the south polar face, wetter in this seed,
   had greenish fringes in the classifier's own output. At 0.5 moisture spans two units, and
   everything below about −5 °C is tundra however wet. `BiomeTests.ShippedBiomesFollowClimate` pins
   the mapping (cold is tundra at any moisture, hot and dry desert, hot and wet rainforest, temperate
   grassland) and fails at 0.25. The macro map export gained `<scene>_biomes.png`, the classifier's
   output over the biome colours, unlit, so a question like this one can be split between climate
   and rendering at a glance.

### M5b, close-up roughness (2026-10-18)

1. **The crossover is set by `HeightParams.WithRoughness(finePersistence, crossover)`**, like the band
   edges, rather than through new `Create` arguments. `TerrainRoot` calls it when
   `TerrainSettings.RoughnessCrossover` is above zero, so 0 means off and `SampleScene` is untouched.
   The crossover counts the octaves at least that long, by exponent. The multiply after octave k is
   `Persistence` while k + 1 is below that count and `FinePersistence` from then on. `Fbm` gained one
   general overload, band gains and two slopes together. With zero gains and equal persistences,
   `Height` still calls the original.
2. **Each scene's C was re-measured on 20,000 random nodes per depth, at the chosen spectrum**, and
   pinned half again over the roughest. On the single-slope spectrum the same method gives 15.4 on
   Earth, which reproduces M4's 15. The tail is long: Earth's roughest of 1,000 nodes at depth 19
   was 31.3, and of 20,000 it was 40.6, against a 99.9th percentile of 31.2. A decision taken on the
   smaller sample would have put Earth's morph fraction at 0.12 rather than 0.10.

   | scene | crossover, fine | 10 m features | roughest C (depth) | pinned C | needs `MorphStartFraction` ≤ | runs |
   |---|---|---|---|---|---|---|
   | Earth | 1 km, 0.8 | 23.0 cm | 40.6 (19) | 61 | 0.109 | 0.10 |
   | small planet | 250 m, 0.8 | 24.0 cm | 15.1 (17) | 23 | 0.138 | 0.13 |
   | ringworld | 1 km, 0.7 | 32.2 cm | 15.1 (16) | 23 | 0.202 | 0.15 |
   | terrarium | 100 m, 0.55 | 28.6 cm | 4.8 (9) | 9 (kept) | 0.170 | 0.15 |

   The terrarium's old estimate of 9 still covers its rough spectrum's 7.1, so it stays: the plan's
   method would have lowered it, and the terrarium is the scene with the least room.
3. **The spheres narrow their morph window instead of moving to `SplitFactor` 4**, the user's choice,
   made with these numbers. The plan offered two options, `SplitFactor` 4 or the roughness
   `SplitFactor` 3 carries at 0.15 (about 15 cm on Earth, 17 cm on the small planet). The third is
   the lever `SampleScene` already uses: at `SplitFactor` 3 the allowance is
   `3 × (0.1882 − MorphStartFraction)`, so 0.10 gives 0.2646 and 0.13 gives 0.1746. That costs no
   chunks. The relief pad grows the worst working set only to 5,230 on Earth and 4,558 on the small
   planet, inside the 5,800 and 5,100 budgets. `SplitFactor` 4 measured 8,310 and 7,246, for budgets
   of about 9,400 and 8,200, roughly 920 MB of vertex data on Earth against 570 MB. The cost of 0.10
   is a morph window a third narrower: at depth 19, 12 m of camera travel instead of 18 m.
4. **Earth is now near the top of its allowance.** At `MorphStartFraction` 0.10, C 61 and the biomes'
   2.25, the 2× rule leaves 1.11×, and Earth's `HeightScale` could rise only to about 10,000. That is
   where the relief went, and the selector's roughest-chunk report on leaving play mode is the check.
5. **`SkirtFraction` followed the larger demand**: Earth 0.27, small planet 0.18, ring 0.28, with the
   terrarium unchanged at 0.66. An absolute 2 m skirt now covers no depth of Earth.
6. **The fly camera's height bound was left alone.** `TopologyFlyCamera` bounds the terrain by
   `HeightScale / (1 − Persistence)`, about 2.6 times the highest ground generated. That still covers
   the macro layer's base height and the biomes' largest amplitude, but it is no longer what the
   comment says it is. It belongs to the horizon arithmetic, not to anything that cracks.

### M5b, hills and mountains (2026-10-18)

1. **Small-scale roughness was not enough.** After the retune above, the user saw small relief at
   10 m and still flat ground from 500–1,000 m up. At persistence 0.5 from a 10,400 km first octave,
   Earth's 5 km features were 4 m, its 20 km features 18 m and its 100 km features 70 m. Real relief
   falls off more slowly at large scales, with persistence around 0.7 above a few kilometres, and at
   about 0.5 below. The two-slope mechanism already allows it the other way round: `Persistence`
   0.7 above a 5 km crossover, `FinePersistence` near 0.5 below it, and a lower `HeightScale` so that
   the slower falloff does not raise the continents.
2. **The relief budget forced `SplitFactor` 4 on the spheres.** Kilometre-scale slopes are what the
   boundary allowance pays for. On 20,000 random nodes per depth, Earth's roughest depth-19 node has
   a half-relief of 0.106 of its size at fine persistence 0.5 and 0.135 at 0.53. The first pass, on
   1,000 nodes, had 0.082, so the tail misled once more. At `SplitFactor` 3 no `MorphStartFraction`
   carries that, and `EarthConfigurationClearsTheReliefAllowance` now asserts it. The user chose
   `SplitFactor` 4 for mountains, on worst working sets measured by the replica scan:

   | scene | worst at `SplitFactor` 3 | at 4 | at 4, every node padded by a tenth | budget |
   |---|---|---|---|---|
   | Earth | 5,118 | 8,198 | 8,394 | 9,400 (was 5,800) |
   | small planet | 4,510 | 7,206 | 7,386 | 8,300 (was 5,100) |

   That is about 1.6× the chunks: on Earth about 920 MB of vertex data at the budget against 570 MB,
   with selection time growing in proportion, already over budget and M6's to fix. The worst case moved
   from `(20/256, 0)` to `(76/512, 0)`. The fresh working set at the face corner, which
   `LodSelectorTests.EarthReplicaWantsWhatTheSelectorWants` pins against the real selector, went from
   4,458 to 7,002. The Editor run caught that pin; it had kept the `SplitFactor` 3 figure.
3. **Earth's biomes were held to 1.44×.** With the biomes' 2.25 Earth would have needed
   `MorphStartFraction` 0.07 and fine persistence 0.5 (10 m features 15 cm). The user chose ruggedness
   up to 1.6 (largest amplitude 1.44), as on the small planet, with a morph fraction of 0.10 and
   10 m features of 26 cm. The ring, which kept `SplitFactor` 3, takes the same 1.44 and so keeps its
   0.15.
4. **The configurations, as shipped:**

   | scene | `HeightScale` | `Persistence` / crossover / `FinePersistence` | 5 / 20 / 100 km features | 10 m | pinned C | `SplitFactor`, MSF | `SkirtFraction` |
   |---|---|---|---|---|---|---|---|
   | Earth | 4000 (was 9000) | 0.7 / 5 km / 0.53 | 79 / 161 / 329 m | 26 cm | 526 | 4, 0.10 | 0.65 |
   | small planet | 2000 (was 6000) | 0.7 / 5 km / 0.53 | 81 / 165 / 336 m | 27 cm | 221 | 4, 0.12 | 0.56 |
   | ring | 2000 (was 4000) | 0.7 / 5 km / 0.5 | 115 / 235 / 480 m | 22.5 cm | 97 | 3, 0.15 | 0.37 |
   | terrarium | 200 | 0.5 / 100 m / 0.55 | — | 29 cm | 9 | 3, 0.15 | 0.66 |

   The feature heights are octave amplitudes before biomes; the most rugged ground multiplies the
   5 km and shorter ones by up to 1.44. The relief tests' margins hold everywhere: Earth's
   `SplitFactor` 4 allowance at 0.10 is 0.6146 against twice its demand, 0.582, and the small
   planet's at 0.12 is 0.5346 against 0.489. `HeightScale` is now near its ceiling on both planets
   (about 4,200 on Earth), and the selector's roughest-chunk report on leaving play mode remains the
   arbiter.
5. **Measuring methods agree.** On M4's spectrum the 20,000-node method gives Earth a pinned C of
   15.4, reproducing M4's estimate of 15. The new pins come from the same method at the new
   spectra.

### M5b, mountain belts and the sky (2026-10-18)

The Editor run showed the mountain spectrum still mostly flat from 500–1,000 m up, and the user
noted that the lighting shows relief only near the terminator. They chose a ×3 mountain band in
belts, and three of four shading fixes: haze, rock on steep slopes, and sky light by local up. They
left out a lower sun at each scene's start.

1. **The fine band was spending the relief budget.** The LOD allowance pays for the steepest node
   anywhere, and a band's slope per metre of height grows as its wavelength shrinks. Ruggedness
   multiplied the mid and high bands together, up to 1.44, so the octaves under 5 km made up most of
   the demand. The 5–150 km band, which is what reads as hills and mountains from a kilometre up,
   was cheap by comparison and was tied to them.
2. **Ruggedness now scales only the mid band, which became the mountain band.** On the planets and
   the ring the band edges moved from 50 km and 500 m to 150 km and 5 km, so the mid band is the
   octaves of 5 to about 100 km. Ruggedness runs it from 0.3 to 3.3 (2.97 with the biomes' largest
   mid amplitude). The high band is left to the biomes, at most 0.9. Measured on 20,000 random nodes
   per depth with the bands at (1, 3, 1):

   | scene | roughest half-relief | pinned C (was) | MSF ceiling | runs |
   |---|---|---|---|---|
   | Earth | 0.173, depth 19 | 674 (526 × 1.44) | 0.124 at `SplitFactor` 4 | 0.10 |
   | small planet | 0.174, depth 17 | 340 (221 × 1.44) | 0.123 at 4 | 0.12 |
   | ring | 0.130, depth 16 | 171 (97 × 1.44) | 0.150 at 3 | 0.14 (was 0.15) |

   The same machinery with the fine band at 0.5 instead of 0.53 would have bought more room. It was
   not needed: the 10 m features stay at 26 cm on Earth. A mid band of 4 would need Earth at
   `MorphStartFraction` about 0.09 (estimated, not measured).
3. **The test model changed with it.** One C scaled by the largest amplitude anywhere
   (`MaxAmplitude`) would charge the fine band for the mountain band's 3 and fail every scene.
   Each scene's C is now measured with the bands at given amplitudes, (1, 3, 1) on the planets and
   the ring and (1, 1, 1) in the terrarium. `MacroTests.DetailDemand` asserts that the macro asset's
   `MacroParams.MaxBandAmplitude` stays under them, so raising an amplitude fails until C is
   re-measured.
4. **The belts are a minority of the world.** At the ruggedness map's old centre, 0.5, 26% of Earth
   saturated at full amplitude and 56% was at twice it or more: mountains everywhere, which reads as
   no mountains. `MacroLayerSettings.RuggednessCentre`, stored as a shift from 0.5 so the default is
   M5a's map to the bit (C5), is 0.2 on the three. That puts 11% of Earth at full belt, 35% at twice
   the amplitude or more, and 30% in plains under 0.6; the ring's figures are 16%, 42% and 24%.
   `MacroTests.MountainBeltsAreAMinority` pins them.
5. **Skirts followed the demand:** Earth 0.65 to 0.58, the ring 0.37 to 0.43; the small planet's
   0.56 still covers it.
6. **The sky, a second shader feature, `_SONOMA_SKY`.** It is on in `TerrainBiomes.mat`, so
   `SampleScene` is untouched. The ambient comes from the local up rather than world-space SH. Aerial
   haze is an exponential atmosphere integrated in four steps, each exact for a linear climb, so a
   path out of the air is not charged the base density all the way. The values are per scene, in
   `TerrainSettings`, pushed as globals every frame:

   | scene | visibility | scale height |
   |---|---|---|
   | Earth | 120 km | 3 km |
   | small planet | 120 km | 3 km |
   | ring | 200 km | 3 km |
   | terrarium | 30 km | 2 km |

   On Earth, ground 5 km away keeps about 87% of its contrast from 1 km up, ground 50 km away about
   25%, and the view straight down from orbit about 90%. The hemisphere ambient is physical and so
   subtle: a 15° slope is under 2% darker.
7. **Rock on steep slopes.** `TerrainBiomes.mat`'s rock threshold was 63°, which no slope reaches.
   It is now 16° to 28°.

### M5c, erosion: the plan (2026-10-20)

M5a and M5b are merged and verified in play. The user then took up §5's optional erosion pass, which
this document had put out of scope, and chose a hybrid. This amendment is its plan. It reopens M5: the
milestone is done when M5c is.

The hybrid has two halves:

- **Macro erosion (M5c-1).** A stream-power pass over the macro maps' top level, once, at startup. It
  erodes the *sum* of the base height and the detail octaves the maps can carry, not the base height
  alone. It stores the correction in the base height channel, and the drainage area in a new channel.
  This is the non-local half. It gives drainage basins, valleys that join, and a river map.
- **A gully term (M5c-2).** A local term in the height function, at every depth, for the scales the
  maps cannot hold. Its strength is shaped by the stored drainage. It stays a pure function of a point,
  so it needs none of the macro half's machinery.

The ground rules above still hold. `SampleScene` stays bit-identical, there is nothing new per frame,
and the Editor is right when it disagrees with a figure here. Erosion runs only where an asset turns it
on, so with it off every scene is M5b's to the bit.

#### Why both halves: the maps are coarse on the planets

Erosion only shapes features a few texels wide, and C1's four-texel rule makes the shortest wavelength
a level holds four of its texels. At each scene's shipped `LevelCount`, the top level's texels are:

| scene | top level, cells per quad | texel | shortest wavelength held | nodes at the top level |
|---|---|---|---|---|
| Earth | 256 | about 40 km | about 160 km | 393,218 |
| small planet | 256 | about 9.8 km | about 39 km | 393,218 |
| ring | 256 | about 4.9 km | about 20 km | 526,336 |
| terrarium | 128 | about 60 m | about 240 m | 525,312 |

Node counts are distinct surface points, with shared edges counted once. A cube sphere with `n` cells
per face has `6n² + 2`. A cylinder of `C × R` quads has `Cn × (Rn + 1)`, since it closes round its
circumference and is open at its rims.

On Earth the mountain band is 5 to 100 km (the "mountain belts" entry above). So macro erosion there
shapes only continental drainage and the broadest structure of the ranges. Everything a person sees
from a kilometre up is below it, which is the gully term's job. The terrarium is the opposite case:
its 60 m texels resolve real valleys, so it is where macro erosion is proved first. The ring sits
between the two. Raising the planets' resolution is possible but costs memory and startup time. It is
decision D3, not part of the plan.

---

#### E1 — Erode the sum, and fold the correction into the base height

The detail noise is the larger part of the terrain: on Earth `HeightScale` is 20,000 m against 3,000 m
of base height. Eroding the base height alone would carve channels into a layer that the detail noise
then covers, uneroded. So, at each top-level node, the pass erodes

```
T = BaseHeight + HeightScale × Fbm(point, octaves 0 .. Oe, band gains from Biomes.Modulate)
Oe = HeightParams.MaxOctave(top level) − 1
```

`MaxOctave(d)` puts depth d's finest octave at about two vertex spacings, so `Oe` is the finest
octave at four or more top-level texels, C1's rule for the detail noise. `T` is exactly what
`TerrainHeightFunction.Height` sums for those octaves at that point, band gains included. Build it
by calling the same `LatticeNoise.Fbm` overload with `Oe + 1` octaves, not by writing a copy.

The pass returns the eroded `T'`. It stores `D = T' − T` by adding it into channel x:
`BaseHeight' = BaseHeight + D`. The height function then needs no change for M5c-1. Within
octaves 0 to `Oe`, `BaseHeight' + detail` is `T'`, and the finer octaves sit on top as before.

- **Climate keeps the uneroded base height.** The temperature lapse and `Biomes.Classify` read the
  climate channels, which are generated before erosion. Re-deriving them from `T'` is possible but
  changes M5b's biome map, so it is left out (it can follow under E7's moisture coupling).
- **Clamp `T'` to the range of `T`.** Use the minimum and maximum over the whole surface. Detachment
  only lowers ground, and diffusion averages it, so in exact arithmetic the clamp changes nothing. It
  makes E5's height bound hold by construction.

#### E2 — One graph over the whole surface, built from the links

Water crosses root-quad edges, so the pass runs on one graph, not per quad.

- **Nodes.** Every top-level texel from 0 to `n` on each axis, with shared edge and corner texels
  merged to their owner by `MacroLayout.EdgeSource`. This is the same ownership rule C2 uses, so a
  node's value goes back to every quad that stores it, and the existing `EdgeCopies` pass keeps the
  edge rows identical.
- **Neighbours.** Up to eight per node: the D8 offsets, resolved across an edge through
  `SurfaceMath.QuadEdgeLink`, reversal included, then merged to the owner.
  - A diagonal step across a cube corner, where three faces meet, has no node. Drop it.
  - Two offsets that resolve to the same node count once.
  - A cylinder's rims and a plane grid's outer edges have no neighbour beyond them. They are outlets
    (E3).
- **Lengths and areas from the surface, never the grid.** A cube sphere's texels vary 1.6:1 across a
  face. So the length of each link is the chord between the two nodes' `SurfaceMath.SurfacePoint`s,
  and each node's area is the area of its Voronoi cell: a quarter of the four surrounding cells, each
  measured from its corners. Drainage area is then in square metres, comparable across a face and
  across topologies.
- **Built once, in Burst, as flat arrays:** `NativeArray<int>` neighbours (8 per node, −1 for none),
  `NativeArray<float>` link lengths and `NativeArray<double>` areas. Also keep the managed reference
  builder the tests read, as `MacroGenerator.Build` is for the maps.

The graph is the riskiest new code, because the corner and reversal cases are where it would go wrong.
`ErosionGraphTests` (see Tests below) pins it before any erosion runs on it.

#### E3 — Stream power, implicit, with fixed outlets

Use the implicit stream-power scheme of Braun and Willett (2013), with the slope exponent `n = 1` and no
uplift. Each iteration:

1. **Routing.** Each node's receiver is its steepest-descent neighbour by `(h − h_r) / length`. Ties go
   to the lower node index, so routing is deterministic.
2. **Pits.** Routing runs on a depression-filled copy of the surface, made by a priority flood from
   the outlets (Barnes et al. 2014, with an epsilon gradient, ties broken by node index). Every node
   then drains to an outlet, and a lake routes over its spill point. Erosion modifies the unfilled
   surface, so lakes stay as hollows, for a water system to fill later.
3. **Order and drainage area.** Build the stack: nodes ordered outlet-first, from the receiver tree.
   Walking it in reverse, add each node's area to its receiver's. That gives the drainage area `A`.
4. **Incision.** In stack order, downstream first,
   `h_i ← (h_i + F h_r) / (1 + F)` with `F = K Δt A_i^m / length_ir`. With `n = 1` this is exact and
   unconditionally stable, so a few tens of large steps reach the result.
5. **Hillslope diffusion.** `κ Δt ∇²h` on the graph, as a few Jacobi sweeps. Each node reads only
   last sweep's values, so the sweeps are parallel and deterministic.

**Outlets** are the boundary condition that makes this work on a closed surface:

- Every node at or below a `BaseLevel` (metres, per scene) is an outlet. Outlets are not eroded.
- On a cylinder, the rim nodes are outlets too.
- A cube sphere has no rim, so it needs a `BaseLevel` that leaves some ground below it. The macro base
  height is zero-mean, so `BaseLevel` 0 makes roughly half the sphere outlet: the continents' rivers
  run to the "sea", even though there is no water yet.

**Determinism.** Steps 1 and 5 are per node and parallel. Steps 2 to 4 are sequential passes in one
Burst `IJob`, in double, with every tie broken by index. Same seed, same maps. The managed reference
must match the Burst build bit for bit, as `MacroBuildJob` does.

**Parameters**, in a new `ErosionSettings` asset that `MacroLayerSettings` references. A null reference
means no erosion.

| field | meaning |
|---|---|
| `Iterations` | implicit steps (start at 30) |
| `IncisionDepth` | metres a channel draining a whole quad's area is cut down over the run. The asset stores this rather than `K`, which has units of `1/(time × m^(1−2m))`, and `ErosionParams.Create` derives `K Δt` from it. |
| `AreaExponent` | `m`, 0.4 to 0.5 |
| `Diffusion` | hillslope `κ Δt` per step, as a fraction of a texel² |
| `BaseLevel` | metres; outlets at or below it |

#### E4 — A second array for drainage, and a pyramid derived downwards

**A new channel.** Channel x carries the folded base height (E1), but the drainage area needs a home,
and the `float4` texel is full. Add a second persistent array: `NativeArray<float2>`, with the same
`MacroLayout` indexing and C2's edge rule, holding:

- x: **wetness**, `log(A / A_node) / log(A_quad / A_node)`, saturated to 0..1. 0 is a ridge and 1 is a
  river draining a quad's area.
- y: **`D` itself**, the erosion correction. The gully term does not use it; it is kept for the map
  export and the tests.

It costs half the main array: 4.4 MB on Earth. `MacroSample` gains `Wetness`, a float whose default 0
means "no erosion data" and must leave the gully term at its identity (C5's rule). `MacroSampler.Read`
reads the second array only when erosion is on.

**The levels below the top are derived, not regenerated.** Eroding only the top level would leave the
planet seen from orbit uneroded. Level L is level L + 1 filtered by the binomial `[1 4 6 4 1] / 16` on
each axis, then read at the coincident texels: L's `(i, j)` is L + 1's `(2i, 2j)`. This applies to `D`
(added into channel x at each level) and to both drainage channels. The filter needs L + 1's border
filled with real neighbours' values, not the generator continued, so before each level is filtered:

- **Halo fill.** Fill each quad's border texels from the neighbour quad's interior through the same
  link resolution the graph uses (E2). The diagonal block at a cube corner has no neighbour. Fill it
  by repeating the corner node's value. Seams do not depend on this choice: C2's agreement is exact
  on the edge line, which is where shared vertices lie, and a border texel only shapes the read just
  inside one quad.
- **Edge copies.** Run `EdgeCopies` after each level, so both quads store the owner's bits on every
  shared edge.

The binomial filter is not C1's hard band limit. It passes a quarter of the amplitude at the coarser
level's Nyquist and about three quarters at four texels. Overshoot is still bounded by
`MacroSampler.MaxOvershoot`, whatever the content, and the tests below measure the ringing instead of
assuming none. If it shows, use a wider filter (`[1 6 15 20 15 6 1] / 64`).

**Startup cost.** The pass runs at startup, after `MacroBuildJob` and before anything generates. At
about half a million nodes, 30 iterations of sequential Burst passes is estimated at a few hundred
milliseconds. That is an estimate, to be measured. `TerrainRoot` logs it beside the build time. If it
runs past about a second, caching the maps to disk keyed by a hash of the settings, which this
document left out of scope, becomes worth doing (decision D4).

#### E5 — The height bound and the relief allowance

**`MaxAbsHeight` stops being purely analytic.** E1's clamp keeps `T'` within the range of `T`, but the
stored channel x is `T' − detail(0..Oe)`, which can exceed the uneroded base height by up to the detail
bound. The tight bound is the one measured on the built maps:

```
baseTerm = MacroSampler.MaxOvershoot × max over stored texels |BaseHeight'|
```

So `TerrainHeightFunction.MaxAbsHeight` gains an overload taking the built `MacroMaps`. `MacroMaps`
records the maximum during the build, and `TerrainRoot` calls the overload; it already builds the maps
before it computes the envelope. The analytic form stays for scenes without erosion, so M5b's
envelopes stay to the bit, and so do `LodVertical`'s anchors. With erosion on the envelope grows, so
it moves the flattened world's anchor too. The tests re-measure the terrarium's working set, which is
the scene most sensitive to the anchor (a fifth of its radius already).

**The relief allowance.** Channels steepen the macro layer. `C_m` = 7 was measured on smooth
generated maps, and on eroded ones it is a new number per scene. Measure `C_m` on the real built maps
of each scene: 20,000 random nodes per depth, C10's method, pinned half again over the roughest. Then
re-run `MacroTests.CombinedMargin`. Where a scene fails, the levers are its `IncisionDepth`, its
`MorphStartFraction` or its `BaseHeightScale`. That is a user decision, as `SplitFactor` was.

**Skirts.** Each chunk's skirt is sized from its own measured relief (the `SkirtDepth` entry in
`CLAUDE.md`), so it follows automatically. But the transient-gap shares (0.48 to 0.76 of a chunk's
relief) were measured on M5b's terrain and need re-measuring with `StressSceneTests.TransientEdges`.

#### E6 — The gully term: local, pure, and steered only by world-space quantities

The finer octaves, everything below `Oe`, are untouched by macro erosion. On the planets that is the
whole visible landscape. The gully term reshapes them inside `LatticeNoise.Fbm`, as the band gains do.
It is two mechanisms, built in this order:

1. **Slope damping (G1).** Each octave's amplitude is divided by `1 + E |g|²`, where `g` is the
   accumulated slope of the octaves before it, in metres per metre. Steep ground loses fine detail and
   ridges and flats keep it, which reads as weathered slopes and sharp crests. This needs the noise's
   analytic gradient: a `Gradient3DD` returning the value and its `double3` derivative, from the
   quintic fade's derivative. Project the derivative onto the tangent plane by subtracting its
   component along the surface normal `normalize(p − centre)` for a sphere (the analogue for a
   cylinder, `+Y` for a plane).
2. **Steered gullies (G2).** Above a slope threshold, add a stripe pattern of a set wavelength running
   *down* the accumulated gradient `g`, its amplitude growing with slope. This follows Rune Skovbo
   Johansen's erosion filter (2023) and its phacelle noise (2025): cells of cosine stripes aligned per
   cell to the local gradient and blended. Several octaves of it nest into branching patterns. Build it
   only if G1 alone reads as too smooth: the user judges G1 in play first (decision D2).

**Coupling to the macro half.** Wetness from E4 scales `E` up on valley floors, so floors carry
deposit-smooth ground and their sides keep their detail. It also scales G2's amplitude down there,
since a valley floor is not gullied. At wetness 0, the default, the coupling is the identity.

**What the term must not read: the macro layer's derivatives.** It is tempting to steer gullies by the
gradient of the eroded macro surface. But a bicubic read's derivative *across* a root-quad edge comes
from each quad's own border texels, so the two quads disagree. The gully pattern would then differ
across the edge and the terrain would crack, far beyond the cube edge's 1.3 nm. The term reads only:

- macro **values**, which agree to the bit on every edge (C2);
- the detail noise's own **gradient**, which is a function of the 3D point and so agrees as positions
  do.

A test holds the macro's influence to its values alone (see Tests below).

**The invariants it keeps:**

- **Strict prefix.** Octave k's damping depends only on octaves below k, so a band-limited sum is
  still a prefix of a finer one, and a fully morphed child is still its parent. Extend
  `HeightFunctionTests`' prefix test and run `GeomorphTests.ChildAtFullMorphEqualsParent` with gullies
  on.
- **Identity when off.** With `E` 0 and no G2, `TerrainHeightFunction.Height` takes the existing path
  (the same switch the band gains use), so `SampleScene` and every scene with gullies off are M5b's
  bits.
- **The bound.** The damping factor is at most 1, so it never raises `MaxAbsHeight`. G2 adds at most
  its amplitude per octave; add that term.

**Its cost.** The gradient costs about twice a plain octave. It runs in the sample jobs, on workers,
so frame time is not at stake, but chunk throughput is: compare chunks per second during a descent
before and after. Slope damping lowers relief, and gullies raise it at fine scales, so each scene's C
is re-measured (E5's method) and its pinned value, `CloseUpRoughnessMeetsItsTarget` and the skirt
shares updated.

**Parameters** live in `TerrainSettings`, since the term is part of the height function:

| field | meaning |
|---|---|
| `GullyDamping` | `E`; 0 off |
| `GullyStartWavelength` | metres; damping applies to octaves at or below it (read off by exponent, like the band edges) |
| `GullyAmplitude`, `GullyWavelength`, `GullySlope` | G2; amplitude 0 off |
| `WetFloorDamping`, `WetGullyFade` | the wetness coupling, both 0 off |

#### E7 — Optional: wetter valleys

In `Biomes.Modulate`, add `WetnessMoisture × wetness` to the moisture before classification, so river
valleys read greener. Off by default. It changes splat weights only, and the coarse job computes
wetness at the parent's level, so the morphed splats still match (C6).

---

#### Tasks

**M5c-1 — `m5c-macro-erosion`**

1. `ErosionSettings` and `ErosionParams` (with `Create`, deriving `K Δt` from `IncisionDepth`).
   Reference it from `MacroLayerSettings`. A null reference disables erosion and changes nothing.
2. `ErosionGraph`: nodes, neighbours, lengths and areas (E2), as a managed builder and a Burst job.
3. `ErosionJob`: the flood, routing, stack, area, incision and diffusion (E3), with a managed reference.
4. The second array, wetness and `D` (E4); `MacroSample.Wetness`; and the read in `MacroSampler.Read`.
5. The derived pyramid: halo fill, the binomial filter, the edge copies per level (E4).
6. `MacroMaps.Build` runs it all and records the maximum `|BaseHeight'|`. Add the `MaxAbsHeight`
   overload, and log the erosion time from `TerrainRoot`.
7. `MacroMapExport` writes `<scene>_erosion.png` (`D`, diverging colours) and `<scene>_wetness.png`.
8. Turn erosion on in `TerrariumStress` first, then the ring, then the planets, tuning each with the
   export before play.

**M5c-2 — `m5c-gullies`**

1. `LatticeNoise.Gradient3DD`, with a test of its derivative against central differences of
   `Gradient3D`.
2. The tangent projection per topology, and slope damping in a new `Fbm` overload (G1).
3. The `TerrainSettings` fields; the identity path in `Height`; the wetness coupling.
4. G2, if decision D2 asks for it.
5. Re-measure C, the skirt shares and the working sets per scene; update the pinned figures.

M5c-2 can land before M5c-1, with wetness at its default 0. The order here puts the non-local proof
first because that was §5's purpose for the optional pass.

#### Tests

Pure where possible, as before.

- **Graph.** The node count against `6n² + 2`, `Cn(Rn + 1)` and `(Cn + 1)(Rn + 1)`. Neighbour
  symmetry (a in N(b) if and only if b in N(a)). No node its own neighbour. Every reversed link and
  every cube corner exercised. Lengths within the face's 1.6:1 spread of the nominal spacing.
- **Routing.** After the flood, every node reaches an outlet along strictly decreasing filled height,
  and the receivers form a forest.
- **Drainage, analytically.** On a tilted plane grid with one outlet rim, each node's area equals the
  area upslope of it. On a cone, area grows with the distance from the apex squared.
- **Incision.** With diffusion off, `D ≤ 0` everywhere, and the incision grows with `A`.
- **Determinism.** Two builds give the same bits. The Burst build equals the managed one (Editor).
- **Range and bound.** `T'` lies within the range of `T`. The measured `MaxAbsHeight` bounds
  `HeightAtPoint` over random points on every eroded scene, overshoot included.
- **Seams.** `MacroEdgesAgreeAcrossEveryLink` on eroded maps, both arrays. Gullies across every
  root-quad link agree to the cube edge's existing bound. Mutation check: steering by the macro
  derivative breaks it.
- **The macro's influence through values only.** Change a border texel off the edge line; the edge
  heights must not change.
- **Pyramid.** Each derived level's interior texels against the filtered level above; the edge rows
  identical across links; the ringing (energy at wavelengths under four texels) measured and bounded.
- **Identity.** With erosion off, every scene's maps are M5b's to the bit. With gullies off, `Height`
  is the M4 and M5b bits (`HeightIsBitIdenticalWithoutAMacroLayer` and its M5b counterparts).
- **Geomorph.** `ChildAtFullMorphEqualsParent` over eroded maps with gullies on.
- **Relief and budgets.** Each scene's re-measured C and `C_m` pinned; `CombinedMargin` passes; the
  replica's working sets within budget; the skirt shares re-measured.

#### Verification

- **Agent-side, without the Editor:** the pure tests, including the graph, routing and drainage tests
  over managed builds.
- **Human-side, in the Editor:**
  - all tests green;
  - the erosion time in the log, and the cache decision taken on it (D4);
  - the map export reviewed per scene: does wetness look like rivers, and does `D` cut valleys rather
    than speckle;
  - fly-throughs: the terrarium's valleys from 500 m, the ring's from 5 km, and the planets'
    continental drainage from orbit and gullies from 1 km;
  - no relief warning, and peak working sets under budget, on leaving play in each scene.

#### Decisions for the user, before or during the build

- **D1 — outlets per scene.** `BaseLevel` on the planets (0 drains about half the sphere). For the
  ring and the terrarium: rims only, or rims plus a base level.
- **D2 — G2 or not.** Built only if G1 in play reads too smooth.
- **D3 — finer erosion on the planets.** One more level on Earth (20 km texels, 80 km
  wavelengths) takes the main array from 9 to 35 MB and the erosion graph to about 1.6 M nodes. Two
  more levels (10 km texels) take it to 137 MB and 6.3 M nodes, with startup in seconds. A separate,
  finer erosion-only grid that stores only `D` and wetness is cheaper (68 MB at 10 km on Earth) but
  needs its own addressing. Either is worth deciding only after the terrarium and ring show what
  erosion buys.
- **D4 — cache the maps to disk.** Depends on the measured erosion time.

#### Out of scope for M5c

- Uplift and tectonics: the pass erodes the noise's terrain, it does not grow mountains.
- Sediment deposition beyond diffusion (Yuan et al. 2019 add a deposition term to the same implicit
  scheme, and it would slot into step 4 later).
- Rivers as features (vector channels, water surfaces). Wetness is the map they would be drawn from.
- Thermal erosion (talus): diffusion covers its smoothing role at these texel sizes.

## Out of scope for M5

- **Tectonics, and rivers as features.** Erosion is now M5c (above). Tectonics and drawn rivers stay
  out.
- **Baking the maps to assets.** Only worth it when generation gets expensive, which erosion may
  make it (M5c, D4).
- **Water and sea level.** A separate system by design (`CLAUDE.md`, Important Design Decisions).
- **Textures per biome, and more than four biomes.** M7, with the triplanar precision limit (M4 C4).
- **Flatten, sharpen and terrace curves.** C7.
- **Detail below the vertex spacing.** Detail normals or textures in the shader are the cheapest
  close-up detail of all, since they never touch the LOD budget. They wait for textures (M7) and for the
  triplanar precision fix (M4 C4) they depend on, which could move earlier on its own.
- **The main-thread performance fixes.** M6 (`SonomaRevisedPlan.md` §5).
- **Retuning `SampleScene`.** Ground rule 1.
