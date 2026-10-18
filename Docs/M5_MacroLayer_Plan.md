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
   stay out of scope (see the end). But build the maps as if something non-local will write them
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
    - **Deferred: the build's cost.** Fusing the four channels' fBm loops, and writing the lower levels
      from the top level's per-octave partial sums where texels coincide, would save perhaps a third
      of 200 ms of one-off startup. That is recorded under M6 in `SonomaRevisedPlan.md` §5, to be
      measured in a development build first.

## Out of scope for M5

- **Erosion, rivers, tectonics.** §5 makes erosion optional for M5, and it stays out. A non-local pass
  over a cube map needs neighbourhoods that cross faces, through the links, with corners of valence
  three. C2 makes the edges agree *after* such a pass, but running one is a design problem of its own.
  It gets its own plan, once M5's maps exist to run it on.
- **Baking the maps to assets.** Only worth it when generation gets expensive, which is to say with
  erosion.
- **Water and sea level.** A separate system by design (`CLAUDE.md`, Important Design Decisions).
- **Textures per biome, and more than four biomes.** M7, with the triplanar precision limit (M4 C4).
- **Flatten, sharpen and terrace curves.** C7.
- **Detail below the vertex spacing.** Detail normals or textures in the shader are the cheapest
  close-up detail of all, since they never touch the LOD budget. They wait for textures (M7) and for the
  triplanar precision fix (M4 C4) they depend on, which could move earlier on its own.
- **The main-thread performance fixes.** M6 (`SonomaRevisedPlan.md` §5).
- **Retuning `SampleScene`.** Ground rule 1.
