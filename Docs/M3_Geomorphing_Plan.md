# M3 — LOD Selection and Geomorphing: Exact Implementation Steps

**Audience:** a coding agent working in `G:\UnityProjects\TerrainGen` with no prior context on this project.
**Parent plan:** `SonomaRevisedPlan.md` (repo root), milestone M3.
**Prerequisite:** M2 is complete and merged. `git log --oneline -1` should show `6b2240e` or later, `git status --short` should be empty, and the 42 EditMode tests should pass before you start.

M2 delivered the Burst generation pipeline but renders **root quads only** — six chunks on a
cube-sphere, no subdivision anywhere. That was deliberate (validate the pipeline without an LOD policy
on top), but it leaves the terrain with no detail. M3 adds subdivision, and replaces both "T-junction
stitching" and "LOD transition morphing" from the original roadmap with one mechanism: per-vertex
geomorphing, which is the only approach that stays correct when chunks generate asynchronously and out
of order.

Most of the groundwork exists. `ChunkMeshJob` already writes morph targets into `TEXCOORD2` and
`TEXCOORD3` on every vertex, and nothing reads them. `GenerationScheduler` already takes a priority and
already has `Cancel`. M3 is largely "build the selector, then read those attributes".

The numbers in this document were derived and numerically verified before it was written. Do not
re-derive them by hand; port them, then let the tests confirm the port.

---

## Ground rules

1. **Two landable branches**, as M2 did.
   - **M3a — `m3a-lod-selection`.** `LodMath`, `LodSelector`, node state, preload, atomic swap,
     resident budget, settings. Skirts keep covering the seams exactly as they do today. Independently
     useful: the scene gets its detail back.
   - **M3b — `m3b-geomorphing`.** The vertex-shader morph across all four passes, the invariant test,
     then skirts off to prove it.

   Land M3a first and confirm its tests green. It isolates selector bugs from shader bugs, which
   otherwise present identically (both look like cracks).
2. **Keep the risky arithmetic out of `UnityEngine`.** `LodMath` must not import `UnityEngine`, so it
   runs in the out-of-Editor harness. This is the `ChunkMeshLayout` / `ChunkMeshBuffers` split, and it
   is what turned M2's triangle-count bug into a test failure instead of silent memory corruption.
3. **No per-frame allocation in the selector.** It runs every frame over a tree. Reuse collections, as
   `GenerationScheduler` already does with its heap. There is a test for this.
4. **No `Mathf`, no `System.Math`.** Use `Unity.Mathematics`, as the rest of the project does.
5. **Skirts stay in the codebase.** M3b turns them off to *prove* geomorphing works; they remain as the
   fallback for transient states where the tree is briefly more than one depth apart across an edge.

---

## Conventions (fix these first; everything depends on them)

### C1 — Thresholds come from nominal node size, never measured

`SonomaRevisedPlan.md` §4.4 says "split when `D < SplitFactor × NodeWorldSize`". **Do not implement it
that way.** `SurfaceMath.NodeWorldSize` returns the *measured* size, which varies across a cube face at
a fixed depth:

| depth | measured spread | `end_{d−1} / end_d` if fed from measured size |
|---|---|---|
| 3 | 1.1793 | 1.3607 – 1.6047 |
| 5 | 1.2908 | 1.2944 – 1.6709 |
| 8 | 1.3279 | 1.2758 – 1.6942 |

That ratio must be **exactly 2** or the morph ranges stop nesting, and morph factors stop agreeing
across a boundary. Measured concretely at depth 8 with `SplitFactor = 2`, `MorphStartFraction = 0.4`:
two same-depth neighbours at a shared edge compute **k = 1.000 and k = 0.383**. A 0.617 mismatch in
morph factor along a shared edge is a gaping crack.

Derive from depth alone, reusing `HeightParams.S0` (the nominal depth-0 node size, already computed):

```
S_d     = S0 / 2^d
end_d   = SplitFactor × S_d                       // nesting is then exactly 2.0
start_d = end_d × (1 − MorphStartFraction)
```

This is the same rule, in a new place, as `HeightParams.MaxOctave` taking a depth rather than a size.
The *distance* still uses the node's real bounding sphere; it is only the **threshold** that must be
depth-only.

### C2 — `MorphStartFraction` must be ≤ 0.5

At a depth boundary the fine side must be at `k = 1` and the coarse side at `k = 0` at the same
distance. Fine reaches `k = 1` at `end_d`. Coarse stays at `k = 0` until `start_{d−1}`, which is
`2·end_d(1 − frac)`. So:

```
end_d ≤ 2·end_d(1 − frac)   ⟹   1 ≤ 2(1 − frac)   ⟹   frac ≤ 0.5
```

At exactly 0.5 the margin is zero. **Default 0.4.** Anything above 0.5 cracks at every LOD boundary in
the world, so `LodMath` validates it and throws rather than letting it ship.

### C3 — Morph target correspondence (verified, and stronger than expected)

A child's **even** vertices land on parent vertices *exactly*. For a child at depth `d+1` in quadrant
`q` of its parent, child vertex `i` (even) corresponds to parent vertex `(q(R−1) + i) / 2`, and the
worst `|u_child − u_parent|` over all sampled depths and quadrants is **exactly 0.0** — both sides
evaluate the same `math.lerp` on dyadic values, so the doubles are identical bit for bit.

Two consequences:

- The geomorph invariant can be asserted **bit-identical**, not merely close.
- This only holds because `R` is odd, so `R−1` is even and `q(R−1) + i` is even for even `i`.
  `HeightParams.Create` already enforces odd resolution; that guard is load-bearing here, not stylistic.

### C4 — Morph factor, and where distance is measured

```
k     = saturate((dist − start_d) / (end_d − start_d))
posOS = lerp(positionOS, morphPosition.xyz, k)
nrmOS = lerp(normalOS,   morphNormal,       k)
```

`dist` is `|positionWS − _WorldSpaceCameraPos|`, per vertex. Both are in render space (world − origin),
so the difference is true world metres regardless of the floating origin — no `_SonomaWorldOrigin`
correction is needed here, unlike the triplanar sampling in the fragment stage.

Per-vertex, not per-chunk: two chunks sharing a vertex compute the same world position and therefore
the same distance, which is what makes their `k` agree.

### C5 — Node state

M2 deleted `QuadtreeNode`; do not bring it back as a tree of linked objects. `NodeId` is a tested
dictionary key (`NodeIdTests.EqualityAndHashing` exists for exactly this). Use:

```csharp
struct NodeState
{
    public TerrainChunk Chunk;      // null while generating
    public bool         HasChildren; // this node is split
    public float        MinHeight, MaxHeight;  // for the bounding sphere
}
Dictionary<NodeId, NodeState> _nodes;
```

Descend from the roots each frame; the branch that descends is the one containing the camera, so the
wanted-leaf count is O(MaxDepth), not O(4^MaxDepth).

---

## Task 1 — `LodMath` (M3a)

Create `Assets/Scripts/Core/Quadtree/LodMath.cs`. **No `UnityEngine` import.** A struct built once from
`TerrainSettings` and `HeightParams`, copied by value, mirroring `HeightParams`:

```csharp
public struct LodMath
{
    public double S0;
    public float  SplitFactor, MorphStartFraction, Hysteresis, PreloadFactor;
    public int    MaxDepth;

    public double SplitDistance(int depth);              // SplitFactor * S0 / 2^depth
    public double PreloadDistance(int depth);            // PreloadFactor * SplitDistance(depth)
    public void   MorphRange(int depth, out double start, out double end);
    public float  MorphFactor(double distance, int depth);
    public bool   ShouldSplit(double distance, int depth, bool currentlySplit);

    public static LodMath Create(TerrainSettings t, in HeightParams p);  // validates C2
}
```

- `ShouldSplit` applies hysteresis to the **decision only**, never to the morph range: split when
  `distance < SplitDistance(depth)`, collapse when `distance > SplitDistance(depth) * Hysteresis`. The
  morph range is untouched by hysteresis, or ranges stop nesting.
- `Create` throws on `MorphStartFraction > 0.5` with the C2 derivation in the message, on
  `SplitFactor <= 0`, and on `MaxDepth < 0`. Follow the house style: compile-time constant messages, as
  in `SurfaceMath` and `HeightParams`.

## Task 2 — `LodSelector` (M3a)

Create `Assets/Scripts/Core/Quadtree/LodSelector.cs`. Given the camera position, the node dictionary and
`LodMath`, it produces the set of nodes that should be resident and the set that should be visible.

Bounding sphere: centre is the node's surface centre (the same `SurfaceMath.SurfacePoint` call
`GenerationScheduler.Schedule` uses for the anchor); radius is
`0.5 × SurfaceMath.NodeWorldSize(...)` plus the node's stored `MaxHeight`. Distance is
`max(0, |camera − centre| − radius)`. Use measured `NodeWorldSize` here — this is the distance, not the
threshold (C1).

Per frame, for each root, descend:

1. If `depth == MaxDepth` or `!ShouldSplit(...)` → this node is a wanted leaf.
2. Otherwise mark it split and recurse into all four children.
3. Separately, request any child within `PreloadDistance(depth+1)` even if the parent has not split yet,
   so it is usually resident before the camera crosses the split distance.

Then reconcile:

- Wanted and not resident → `GenerationScheduler.Enqueue(node, distance / nodeSize)`. That priority
  keeps near-and-coarse ahead of far-and-fine, which is what the scheduler's heap is ordered on.
- Resident and no longer wanted → `GenerationScheduler.Cancel(node)` and release the chunk to
  `ChunkPool`. `Cancel` already disposes results for nodes nobody wants; calling it is what keeps the
  §2.4 orphan-chunk leak fixed under a moving camera.

**Atomic swap.** A split node keeps its own chunk *visible* until all four children are resident, then
flips in a single frame: children on, parent off. Never a frame with both. The parent's chunk stays
resident (just hidden) so a collapse is instant and so `WorldOriginSystem` still rebases it — that is
why `TerrainChunk.AllChunks` exists alongside `AllActive`.

**Resident budget.** `MaxResidentChunks` counts *every* resident chunk, hidden parents included. When
over budget, evict the furthest **complete sibling groups of four** — never a lone node, which would
leave its region uncovered. Collapse the group into its parent, which is already resident.

## Task 3 — `TerrainRoot` wiring (M3a)

Modify `Assets/Scripts/Core/Quadtree/TerrainRoot.cs`. Replace the six-root `Enqueue` loop in `Start`
with the selector, driven from `Update` before `_scheduler.Update()`. Own the `Dictionary<NodeId,
NodeState>`, and record `MinHeight`/`MaxHeight` in `OnChunkReady` — the scheduler already has these from
the mesh job's `HeightBounds`, so surface them on the `ChunkReady` event rather than recomputing.

Update the class comment: it currently explains why M2 has no subdivision. That reasoning is spent.

## Task 4 — Settings (M3a)

`TerrainSettings`: add `SplitFactor` (default 2), `MorphStartFraction` (0.4), `PreloadFactor` (1.5),
`SkirtsEnabled` (true); rename `MaxActiveChunks` → `MaxResidentChunks`; remove `LodDistances`.

**Migrate `Assets/Settings/TerrainSettings.asset` in the same commit.** M2 changed the C# fields and
left the asset carrying three deleted ones and none of the five new; it was caught in review, not by a
test. A ScriptableObject asset does not migrate itself.

## Task 5 — The shader morph (M3b)

Modify `Assets/Shaders/SonomaTerrainTriplanar.shader`.

Add to the `HLSLINCLUDE` block, so there is one implementation rather than four:

```hlsl
float4 _SonomaMorphRanges[32];   // per depth: (start, end, 0, 0)

void SonomaMorph(inout float3 positionOS, inout float3 normalOS,
                 float4 morphPosition, float3 morphNormal)
{
    float3 wp   = TransformObjectToWorld(positionOS);
    float  dist = distance(wp, _WorldSpaceCameraPos);
    float2 r    = _SonomaMorphRanges[(int)morphPosition.w].xy;
    float  k    = saturate((dist - r.x) / max(r.y - r.x, 1e-5));
    positionOS  = lerp(positionOS, morphPosition.xyz, k);
    normalOS    = lerp(normalOS,   morphNormal,       k);
}
```

**Apply it in all four passes.** The shader has `ForwardLit`, `ShadowCaster`, `DepthOnly` and
`DepthNormals`, each with its own `Attributes` struct — `DepthOnly` currently declares only `POSITION`
and needs `TEXCOORD2`/`TEXCOORD3` added. Morphing only the forward pass leaves shadows and depth on the
unmorphed geometry, which presents as shadow acne and depth-test artefacts that look nothing like the
actual cause.

Set the range array once per frame from `TerrainRoot` with `Shader.SetGlobalVectorArray`. **Not** a
per-chunk `MaterialPropertyBlock` — that would break SRP batching, which is the reason depth is packed
into a vertex attribute rather than passed as a material property.

Update the shader's header comment: it documents `uv2` and `_SonomaWorldOrigin` and should now also
document the morph attributes and the global range array.

## Task 6 — Turn skirts off and confirm (M3b)

Set `SkirtsEnabled = false` and fly the scene. In the steady state there should be no cracks at any LOD
boundary. Cracks that appear only while chunks are still streaming in are the transient
more-than-one-depth case and are expected; that is what skirts are for. Leave skirts on by default when
you are done.

---

## Tests

### `LodMathTests.cs` — pure, runs outside the Editor (M3a)

1. **`MorphRangesNestExactly`** — `end_{d−1} / end_d == 2.0` exactly for every depth 1..MaxDepth. Assert
   exact equality, not a delta. The headline guard for C1.
2. **`MorphFactorIsOneAndZeroAtEveryBoundary`** — at each boundary the depth-`d` side is exactly 1 and
   the depth-`d−1` side exactly 0. **Mutation check:** substituting measured `NodeWorldSize` for
   nominal `S_d` must fail this. Reference figures: measured gives 1.000 vs 0.383 at depth 8.
3. **`MorphStartFractionAboveHalfIsRejected`** — and `0.5` exactly is accepted, so the bound is the real
   one rather than an off-by-one costing a usable configuration.
4. **`SplitAndCollapseDoNotThrash`** — collapse distance strictly exceeds split distance for every depth,
   and a node oscillating across the split distance changes state at most once.
5. **`ChildEvenVerticesCoincideWithParentVertices`** — for depths 0..4 and all four quadrants, the child's
   even-vertex `(u, v)` is **bit-identical** to the corresponding parent vertex's, and so is the height.
   Verified at planning time as exactly 0.0 error. This is the substance of the geomorph invariant, and
   it is testable without a mesh.

### `LodSelectorTests.cs` / `GeomorphTests.cs` — Editor (M3a / M3b)

6. **`ParentStaysVisibleUntilAllFourChildrenAreResident`** — no frame with a parent and any child both
   visible, and no frame with a hole.
7. **`EvictionTakesCompleteSiblingGroups`** — over budget, never evicts a lone node.
8. **`SelectorDoesNotAllocatePerFrame`** — run the selector many times and assert no GC allocation.
9. **`ChildAtFullMorphEqualsParent`** — the milestone's headline test. Build a child and its parent
   through the real job path, apply morph factor 1 to the child on the CPU, and assert the surviving
   (non-degenerate) child triangles equal the parent's vertex-for-vertex. This is the invariant M2 wrote
   morph targets for and had no way to check.

**Determinism note:** seed every randomised test with a fixed constant, as the rest of the suite does.

---

## Verification

### Agent-side, without the Editor

```bash
git diff --stat main..m3a-lod-selection -- Assets/Shaders
```

Must print nothing — M3a does not touch the shader.

Then compile `Sonoma.Core` and `Sonoma.Tests.EditMode` with Unity's own Roslyn and `.rsp` arguments
(outputs redirected to scratch, never Bee's artifacts while the Editor is open), and compile every
UnityEngine-free source plus a reflection runner and execute it. All pure tests must pass, including
the 42 that already exist. See the `unity-verification-without-batchmode` note for the setup.

### Human-side, in the Editor

1. Console free of compile errors and Burst fallback warnings; the Burst witness test still reports compiled.
2. Test Runner: all green.
3. `SampleScene` plays; terrain subdivides as the camera descends and collapses as it climbs.
4. No visible popping at LOD transitions (that is the morph working) and no overlap flicker at splits
   (that is the atomic swap working).
5. With `SkirtsEnabled` off: no cracks in the steady state.
6. Profiler: main-thread terrain work stays within `UploadBudgetMs`, and the GC Alloc column is zero
   while flying.

---

## Definition of done

- [ ] `LodMath` derives split distance and morph range from nominal `S_d`, validates `MorphStartFraction ≤ 0.5`, and imports no `UnityEngine`.
- [ ] `LodSelector` descends per frame without allocating, with preload margin, atomic parent/child swap, and sibling-group eviction against `MaxResidentChunks`.
- [ ] Nodes that stop being wanted are cancelled and released, not leaked.
- [ ] The morph runs in **all four** shader passes, fed by a global range array, with SRP batching intact.
- [ ] `TerrainSettings` updated **and** `TerrainSettings.asset` migrated in the same commit.
- [ ] 9 new tests pass; the 42 existing ones still pass; test 2 fails under the measured-size mutation.
- [ ] `SampleScene` subdivides, morphs without popping, and shows no cracks with skirts disabled.
- [ ] `SonomaRevisedPlan.md` §4.4 corrected on the split-distance point; `CLAUDE.md` updated.

## Corrections to the parent plan

Fold into `SonomaRevisedPlan.md` as part of the final commit.

1. **§4.4, "Split when `D < SplitFactor × NodeWorldSize`".** Must be nominal `S0 / 2^d`, not measured
   `NodeWorldSize`, or morph ranges stop nesting and every LOD boundary cracks. See C1 for the measured
   figures.
2. **§4.4 does not state the `MorphStartFraction ≤ 0.5` constraint**, which follows directly from its own
   requirement that the fine side be at `k = 1` where the coarse side is at `k = 0`. See C2.

## Out of scope for M3

- **2:1 quadtree balancing.** Optional, M6 at the earliest. Skirts cover the transient states.
- **Colliders, debug overlay, heightmap cache, profiling pass.** M6.
- **The macro layer and biomes.** M5; `MacroSample` stays zero.
- **The floating-origin stress test and the texture-swimming decision.** M4.
- **Lazy infinite plane-grid roots.** Deferred since M1; not M3's problem unless the fly-through needs it.
- **Removing skirts from the codebase.** They stay as the transient-state fallback; M3 only turns them
  off to prove the morph.
