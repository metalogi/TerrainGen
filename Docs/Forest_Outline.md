# Forests from pre-generated tiles: feasibility and outline

**Source:** the Forest Tile Generator in `G:\GSD\gsd-sandbox` (its `README.md`, `contract/key-contract.md`,
`docs/lod-agreement.md`, `docs/lod-stepthrough.md` and `forest_tile/core/`), at the end of its Phase 2.
The idea assessed: forest density, and possibly tree height and colour, as fields at macro scale; at low
LOD the forest is shader parameters on the terrain (colour and a vertex displacement); close enough to
the camera, LOD groups of pre-generated tiles are instantiated, each tile chosen from about a hundred
keys to match the density field.
**Written:** 2026-11-15, with M0–M5b, the continents, M5c-1, G1 and `ocean-a` merged. This is a
high-level outline, not an implementation plan.
**Since:** R1 and R2 are done in the generator, as its Phase 03.1 (2026-11-16), and R3–R5 are in its
backlog as Phase 999.1. The work belongs to M7 (`SonomaRevisedPlan.md` §5).

---

## Verdict

**Viable, and a good fit, with five changes.** The generator and this project share a philosophy: both
are deterministic and pure functions of their inputs, and both measure LOD agreement instead of
judging it. The scales also line up. On Earth each tile LOD band covers about one doubling of distance,
as each terrain depth does (table below). Nothing in the idea breaks the per-chunk contract: height
stays a pure function, and the forest can ride on the terrain's own nodes, as the ocean does.

The changes:

1. **Slopes: translate each tree vertically, as proposed, and take the ground from the chunk that
   draws it.** Each vertex needs to know its tree, which is a small addition to the tile export (R1).
   The ground height at each tree should come from the drawn chunk's own height grid, fine and coarse,
   blended by the same morph factor as the terrain. Trees then stand on the drawn surface at every LOD
   and through every swap. Sampling the full-detail height function instead leaves trees up to 2.5 m
   off the drawn ground at 1.3–2.5 km.
2. **Compute the key, don't select it.** Draw a *full* tile (key `0xffffffffffffffff`) and mask out
   trees per instance. Each cell's bit comes from the density field at that cell, so the forest follows
   the field at 5 m resolution and stays continuous across tile edges. The library becomes a dozen
   seeds of the full key rather than ~100 keys, and the matching search goes away.
3. **Instanced draws, not LODGroup GameObjects.** The forest is a payload of the terrain's resident
   leaf chunks. It is drawn with indirect instancing, one draw per (variant, LOD), with each tile's LOD
   chosen by distance in a Burst job. The generator's switch distances (122 / 244 / 487 / 974 m) become
   the runtime's table.
4. **Hand over to the terrain shader at 1.5–2 km, not at LOD4's own cull distance of 3 km.** Beyond
   about 1.5 km a tree is under 6 pixels tall. The hand-off from LOD4 to the canopy "shell" (terrain
   colour plus displacement) is the one genuinely new visual problem. Treat it as a sixth LOD and
   measure it like the other pairs (R4).
5. **Forest density is a pure function of surface position, band-limited like height.** Regional cover
   comes from the macro climate. Local terms (treeline, slope, sea, wetness, clearings) come from
   functions, because Earth's macro maps are about 40 km per texel at their top level.

---

## The scales line up

Tile LOD distances are the generator's (`docs/lod-stepthrough.md`: dense tile, 35 m LODGroup size,
60° vertical field of view, Unity's default transition heights). Earth's leaf depth at a distance is
`4 × NominalSize(d) ≤ D < 8 × NominalSize(d)` at `SplitFactor` 4, with S0 ≈ 10,400 km, before
hysteresis, preload, the relief pad and `LodVertical`. At 1080p and 60° a pixel is 1.07 m per
kilometre of distance.

| tile LOD | distance | a 9 m tree on screen | Earth leaf depth | terrain vertex spacing |
|---|---|---|---|---|
| LOD0 | 0–122 m | over 69 px | 19 | 0.62 m |
| LOD1 | 122–244 m | 69–35 px | 19–18 | 0.6–1.2 m |
| LOD2 | 244–487 m | 35–17 px | 18–17 | 1.2–2.5 m |
| LOD3 | 487–974 m | 17–9 px | 17–16 | 2.5–5 m |
| LOD4 | 974 m–3.0 km | 9–3 px | 16–14 | 5–20 m |

Two consequences. A tile is about one depth-18 node (39.7 m nominal on Earth), not one finest node.
And at LOD4 distances the terrain's own vertex spacing, 5–20 m, already matches the tree spacing, so a
terrain-side shell can carry the forest from there out.

---

## 1. Slopes

**Tilting or shearing the whole tile is wrong.** On a 20° slope a 40 m tile rises 14.6 m. Laid flat at
its centre height, its edges float or sink by 7.3 m. Tilted to the slope, every tree leans 20°. Trees
grow vertically, so the right deformation is a rigid vertical translation of each tree to the ground
at its own base. That also handles curvature within the tile: a 10 m bump moves the trees on it and
leaves the others alone.

**Each vertex has to know its tree (R1).** The vertex shader needs the tree's cell bit, to index
per-tree data and test the mask, and its base point in tile space, to scale or remap the tree about
it. Both are constant over a tree, so mesh import's welding, splitting and reordering leave them
correct.

**Where the ground height comes from**, three options:

| option | how | error against the drawn ground |
|---|---|---|
| (a) | `HeightAtPoint` at full detail, per tree, on the CPU | The drawn chunk omits its finest octaves. On Earth that is up to about 2.5 m at depth 15 (1.3–2.5 km), from octaves of 10 m down to 1.2 m, and 0.56 m at depth 17. That is 1–2 pixels everywhere, and it does not follow the morph. |
| (b) | Barycentric lookup in the carrying chunk's `Heights` and `CoarseHeights`, by the mesh's own triangulation; the shader blends the two by `SonomaMorphFactor` at the tree's base | Exact at k = 0 and k = 1, so trees are continuous through the atomic swap: a fully morphed child is its parent. In between it is bounded by the relief within one parent cell; measure it in the spike. |
| (c) | Sample a GPU heightmap in the tree shader | There is no GPU heightmap, and building one is a larger change than (b). |

**Recommended: (b).** The heights exist only while a chunk is in flight: the scheduler's `Pending`
disposes `Heights` and `CoarseHeights` after the mesh build. So the placement is a job beside
`ChunkMeshJob`, reading the same arrays. It costs microseconds per tile.

**Trunks have to reach below the ground (R2).** A vertical trunk on slope θ leaves a gap of
`r × tan θ` under its downhill side. The trunk radius is 0.09–0.14 of the crown radius, so 0.16–0.36 m,
which gives 0.25 m at 35° at LOD0. A sink of half a metre also hides option (b)'s mid-morph error.
LOD3 and LOD4 stand on a single pedestal point, so they need nothing.

**A slope limit belongs in the density, not the geometry:** trees fade out above about 35–40°.

---

## 2. Choosing the trees: masks, not a key library

**Matching a continuous field from ~100 keys is the weakest part of the idea.**

- **Too few keys at the dense end.** Exactly one key has 64 trees and only 64 keys have 63, so dense
  forest gets its variety from seeds alone.
- **Too many shapes to cover.** Describing a tile by four quadrant densities at five levels gives 625
  classes, 165 under rotation. A hundred keys cannot cover those with variety.
- **Seams.** A forest edge drawn from library tiles disagrees with its neighbour along every tile edge,
  which shows from the air as a 40 m staircase.

**A mask computed per tile at runtime removes all three.** Each cell's bit is `density(cell centre) >
threshold(cell)`. The threshold is a hash of the tile's `NodeId` and the cell, or a rotated
blue-noise matrix for evenly spread trees. Neighbouring tiles then agree along their shared edge, the
density is followed at 5 m, and the "distribution" becomes a parameter: white noise clumps, blue noise
spreads. The key keeps its meaning as the contract's bit order, but nothing stores it, so the
orientation is no longer a one-way door for stored keys.

**It keeps the LOD agreement.** `lod_fit.fit_scale(spec, profile, reference)` fits each tree on its
own, so removing trees from a tile changes no tree's fit.

**It costs vertex work, not triangles.** A masked tree's vertices collapse to a point, so its
triangles are degenerate and culled before rasterisation. A full LOD4 tile is 384 vertices and a LOD0
tile about 3,900 (64 × 61, before Unity splits them for flat normals). At 30% cover that is about three
times the vertex work actually needed, which is small at these counts.

**Variety comes from seeds, rotations and looks.** Use a dozen seeds of the full key, and rotate each
instance by a multiple of 90°, chosen from a hash. Do not mirror: a negative-determinant instance flips
the winding. Low-density "open" looks (shorter trees with wider crowns) are a second generator
settings set, chosen by density.

**Height and colour fields fit the same way.** A per-tree uniform scale about the tree's base keeps
the LOD fit, because the fit matches areas and volumes, both of which scale. Keep it within about
0.6–1.3, or crowns crowd. A per-tree tint goes on foliage only. To do that the shader needs each
vertex's part and normalised height (R1), and those also let it recolour trees by biome.

---

## 3. The forest rides on the terrain's nodes

**A tile is a node at a fixed forest depth**, chosen per scene so that the node is 30–46 m across:

| scene | tile depth | nominal tile | cell (÷ 8) |
|---|---|---|---|
| `EarthStress` | 18 | 39.7 m | 5.0 m |
| `SmallPlanetStress` | 16 | 39.7 m | 5.0 m |
| `RingworldStress` | 15 | 38.2 m | 4.8 m |
| `NarrowRingStress` | 12 | 45.8 m | 5.7 m |
| `TerrariumStress` | 8 | 30.7 m | 3.8 m |

Nodes partition the surface exactly, root-quad edges included, and a `NodeId` gives each tile a stable
hash for its variant, rotation and thresholds. The generator's `cell_size` should be set to each
scene's cell size (R5).

**The tile is fitted to its node by moving trees, not vertices.** Across a cube face, real node sizes
at one depth vary by up to 1.33:1, and nodes are not square. A per-instance 2×2 matrix maps each
tree's base point onto the node's footprint in the tangent plane, so spacing follows the node and every
tree keeps its shape.

**Every resident leaf chunk within forest range carries the tiles inside it**, exactly as `ocean-a`
made the water a payload of the same `NodeId`. A depth-15 chunk on Earth carries 64 tiles, a depth-17
chunk 4 and a depth-18 chunk 1. A depth-19 chunk carries its quadrant of its parent's tile: the same
mesh, masked to its 16 cells. The forest then inherits selection, residency, eviction, the atomic
swap, culling and the floating origin, and it survives slow generation. When the fine chunks lag, the
coarse chunk still draws, and so do its trees.

**Occupancy must not depend on the carrying chunk.** If a tree's existence were decided from the
chunk's own interpolated density, trees would appear and vanish at every swap. So the mask is computed
per tile from the tile's `NodeId` at full detail, whichever chunk carries it, and cached by tile. Only
the ground heights come from the carrying chunk.

---

## 4. Drawing: instancing, not LODGroup GameObjects

**Why not GameObjects.** At full cover Earth has about 4,500 tiles within 1.5 km and 17,900 within
3 km. Each tile as a GameObject with a `LODGroup` and five renderers means 22,000 to 90,000 renderers,
created and destroyed with every chunk swap. Each one would also need per-tree data, which a
`MaterialPropertyBlock` supplies only by breaking SRP batching. The GPU Resident Drawer is off
(`m_GPUResidentDrawerMode: 0`) and does not take per-instance custom data either.

**The route.**

1. **Prototype:** `Graphics.RenderMeshIndirect`. Per-instance data goes in a `GraphicsBuffer`, with one
   draw command per (variant, LOD): 12 seeds × 5 LODs is 60 per pass. A Burst job culls the resident
   tiles each frame, picks each tile's LOD by distance in metres, and writes the visible lists.
2. **Later:** `BatchRendererGroup`, if per-view culling of shadow cascades matters.

**Per-instance data** is about 450 B per tile: a 64 B header (pivot relative to its chunk's anchor,
frame, footprint matrix, mask, carrying depth) plus 6 B per tree (fine and coarse ground height, scale
and tint). At a 1.5 km cut-off that is 2 MB resident. A rebase rewrites the pivots, five times per
descent.

**Load per frame**, with every tile forested, a camera on the ground and a 90° horizontal view, at the
generator's worst-case triangle budgets (108 / 70 / 36 / 16 / 8 per tree; the means are about 15%
lower):

| forest cut-off | tiles in view | triangles in view | LOD4's share |
|---|---|---|---|
| 1.5 km | 1,100 | 1.05 M | 0.33 M |
| 3.0 km (LOD4's own cull) | 4,500 | 2.8 M | 2.05 M |

The last 1.5 km of LOD4 doubles the triangles to draw trees 3–6 pixels tall. That, more than the
instance count, is why the cut-off belongs at 1.5–2 km.

**The tree shader follows the terrain shader's rules.**

- **Every pass.** The per-tree deformation (mask, ground offset, scale) runs in ForwardLit,
  ShadowCaster, DepthOnly and DepthNormals, or shadows and the depth prepass use undeformed trees.
- **Shared includes.** It takes `SonomaCommon.hlsl` for the morph factor and `SonomaSky.hlsl` for
  hemisphere ambient and haze. A tree hazed by other arithmetic than the ground under it shows a seam
  at every forest edge.
- **One distance.** Tile LOD and the hand-off depend on screen size, so they use the true distance.
  Push the true camera position as a global for that, since `_WorldSpaceCameraPos` is unreliable in
  the shadow passes, as the morph found. The ground blend uses `_SonomaViewPosition`, because it
  follows the terrain.

**Shadows.** The PC URP asset's shadow distance is 50 m, so only LOD0 tiles can cast. Mark the LOD1–4
draws `ShadowCastingMode.Off`. Beyond 50 m the forest floor's darkening has to come from the terrain
shader's density term.

---

## 5. The far field and the hand-off

**The shell is terrain.**

- **A vertex attribute.** The terrain gets one `UNorm8 × 4`: density, the morph target's density,
  canopy height scale and tint. That makes `TerrainVertex` 84 bytes, 5% more vertex memory. The
  density morphs on k, for the splat's reason (C6): otherwise colour pops at every swap.
- **Colour.** The albedo blends towards `coverage(ρ) × canopy + (1 − coverage(ρ)) × floor`. That is
  the mean colour a tile shows from above, so the two agree at the hand-off.
- **Displacement** is along the local up and only beyond the hand-off. It runs in all four passes,
  after the morph factor is computed from the undisplaced position. It is also padded into the
  culling box.

**Fit the height to the side view and the colour to the top view.** The displacement only shows at
forest edges and on ridgelines, seen from the side. From above, colour carries the forest. Seen at a
grazing angle a cone forest reads as a wall near its tops, so the shell's height should be the tiles'
side-silhouette mean height at that density, not their volume per area. The generator already
rasterises both views, so it can publish these as curves over density (R4).

**The hand-off itself.** Tiles fade in over a band, about 1.5–2 km on Earth, while the shell's colour
and displacement fade out. Both sides compute the fade with one shared function of position. Two ways
to fade:

- **Dither.** URP's LOD cross-fade dithering is on in the PC asset.
- **Geometric**, in the spirit of the geomorph. Trees grow from flat to full height inside the shell
  first, then the shell sinks. The silhouette never drops below the shell's, but it bulges in the
  middle.

Measure both. Agreement at the hand-off is the same problem the generator solved between LOD pairs.

---

## 6. The density field

- **Regional cover from the macro climate.** Temperature, moisture, ruggedness and wetness are already
  in the maps, so cover can be derived at sample time, as biomes are classified, without a new channel.
  A stored channel is only needed once something non-local generates forests.
- **Local terms as functions.**
  - Treeline altitude.
  - Slope from the coarse octaves' analytic gradient, as G1's gully term computes it, so the slope is
    the same at every LOD.
  - Zero below `SeaLevel` and on Ocean chunks.
  - More cover by rivers, from wetness.
  - Clearing noise at 50–500 m.
- **Band-limited by depth, as height is.** Shell vertices at coarse depths read the density's coarse
  octaves only, so it does not alias. Cells read it in full. Where both draw, at the hand-off, they
  disagree only in features the shell cannot carry anyway.

---

## Requests to the tile generator

In priority order. R1 and R2 are needed for anything; R3–R5 for the full system.

- **R1. Per-vertex tree identity.**
  - **Channels.** Two FBX UV channels: (base x, base z) in Unity tile space, after the
    `(x, y, z) → (−x, z, −y)` hop; and (cell bit, normalised height, with the trunk or foliage part in
    the sign).
  - **Contract.** Document both in `contract/key-contract.md` with vectors.
  - **Unity import.** Generate Lightmap UVs must stay off, because it writes UV channel 1.
  - **Fallback.** The instanced path needs no `LODGroup`, which was FBX's main reason. If the UV path
    proves awkward, a raw binary per tile with a `ScriptedImporter` would bypass the hop and the
    importer entirely.
- **R2. Trunk sink.** The trunk prism continues below z = 0 by a setting, defaulting to about 0.5 m.
  The pivot stays the ground point, and the invariants and agreement metrics clip at z = 0.
- **R3. A per-tile sidecar.** One JSON per tile with, for each tree, its cell bit, base point (Unity
  axes), height, crown radius, trunk radius, trunk height and lean, plus each LOD's bounds. It feeds
  colliders near the player (M6) and culling bounds. Phase 3's manifest is the natural home.
- **R4. A far-field descriptor per variant.** Curves over density ρ from 0 to 1, from random masks of
  the full tile: top coverage, side-silhouette mean height, volume per area, and mean top and side
  colour. Ideally `measure_agreement.py` also gains an LOD4 ↔ shell pair, with the shell a heightfield
  built from those curves.
- **R5. A library of (key, seed) pairs.** Phase 3's batch takes one seed per run. The library here is
  one key at many seeds, at a per-scene `cell_size` (5.0, 4.8, 5.7 and 3.8 m), so the key file should
  take a seed and a settings set per line.

The generator's own LOD switch distances and its `LODGroup` layout need no change: the runtime reads
the meshes and supplies its own distances.

---

## Risks and open questions

1. **The frame budget.** `TerrainRoot.Update` is already at a median 5.85 ms against 2 ms. The forest
   adds a culling and LOD job each frame and buffer uploads when chunks change. It is M7 work ("Content:
   vegetation") and should be measured after M6's fixes.
2. **The hand-off** is the one part with no precedent in either project. Until it is measured, the
   cut-off and the shell's height are guesses.
3. **Overdraw and micro-triangles.** Dense forest at grazing angles overdraws, and LOD4's 8 triangles a
   tree are a few pixels each.
4. **The mid-morph ground error** under option (b) is bounded but not measured.
5. **Pines only.** The generator's scope ends at evergreens, so rainforest and deciduous looks need
   species work (M7).
6. **The narrow ring** gets 45.8 m tiles with 5.7 m cells, or 22.9 m with 2.9 m. Neither is the 32 m
   the generator was tuned at.

---

## Suggested order

1. **Generator:** R1 and R2, then R5. These are small changes.
2. **A spike in a copy of `EarthStress`** (as `ContinentSandbox` is), the cheapest test of the riskiest
   part, slopes and swaps:
   - full cover on land, masks from a hash, one seed;
   - `RenderMeshIndirect`;
   - ground from the carrying chunk, blended by k.
   Judge trees on slopes and through chunk swaps by eye, and measure the mid-morph error.
3. **The density function**, masks from it, rotations and variants.
4. **The shell**, colour first, then the displacement and the hand-off, measured with R4.
5. **Shadows, colliders and `BatchRendererGroup`**, with M6.
