# M1 — Surfaces and Addressing: Exact Implementation Steps

**Audience:** a coding agent working in `G:\UnityProjects\TerrainGen` with no prior context on this project.
**Parent plan:** `SonomaRevisedPlan.md` (repo root), milestone M1.
**Prerequisite:** M0 is complete and merged. `git log --oneline -1` should show `168abf9` or later, and `git status --short` should be empty.

M1 builds the geometric foundation everything else stands on: a topology abstraction with cube-sphere support, integer node addressing, and cross-quad neighbour lookup. It generates no terrain and changes no rendering. Its real deliverable is a **tested** surface layer that M2's Burst generation can rely on without re-deriving any of this maths.

The numbers and the adjacency table in this document were derived and numerically verified before it was written. Do not re-derive them by hand; port them, then let the tests confirm the port.

---

## Ground rules

1. **M1 is purely additive.** Create a new `Sonoma.Core.Surface` namespace under `Assets/Scripts/Core/Surface/`. Do **not** modify `QuadtreeManager`, `CoordinateTransform`, `BaseMeshQuad`, `BaseMeshFactory`, `HeightmapGenerator`, `TerrainChunk`, or the shader. The existing prototype must still play in `SampleScene` unchanged when M1 lands. The switchover happens in M2.
2. **No Burst jobs yet, but stay Burst-compatible.** Use `struct` types, static methods and `Unity.Mathematics` types. **Do not use interfaces, abstract classes, or virtual dispatch for the surface abstraction** — Burst cannot devirtualise them. Dispatch on a `SurfaceType` enum with a `switch`, exactly as the existing `CoordinateTransform` already does.
3. **Doubles for position, floats for direction.** Surface points are `double3`. Normals are `float3`. This matches the existing code and is what M2's precision work requires.
4. **No `Mathf`, no `System.Math`** in the new code except where a `double` trig function is needed — use `math.` from `Unity.Mathematics` (it has double overloads for `tan`, `sin`, `cos`, `sqrt`) so the code is Burst-ready as written.
5. Work on a branch: `git switch -c m1-surfaces`.

---

## Conventions (fix these first; everything depends on them)

### Edge numbering

```csharp
public enum Edge : byte { South = 0, East = 1, North = 2, West = 3 }
```

- `South` is `v = 0`, `East` is `u = 1`, `North` is `v = 1`, `West` is `u = 0`.
- The parameter running along an edge is `u` for South/North and `v` for East/West, always increasing.

**Warning:** the existing `QuadtreeManager.EdgeDirection` enum is ordered `{ North, South, East, West }`, which is **not** the same ordering. The two enums coexist during M1 and must never be cast to each other. M2 deletes the old one.

### Node addressing

A node covers `u ∈ [x/2^d, (x+1)/2^d]`, `v ∈ [y/2^d, (y+1)/2^d]` on its root quad, with `x, y ∈ [0, 2^d)` and `y` increasing with `v`.

### Cube face basis

Six faces, each with a centre direction and two tangent axes, satisfying `right × up == centre` (right-handed). **Verified.**

| Face | Name | centre | right | up |
|---|---|---|---|---|
| 0 | +X | (1, 0, 0) | (0, 0, −1) | (0, 1, 0) |
| 1 | −X | (−1, 0, 0) | (0, 0, 1) | (0, 1, 0) |
| 2 | +Y | (0, 1, 0) | (1, 0, 0) | (0, 0, −1) |
| 3 | −Y | (0, −1, 0) | (1, 0, 0) | (0, 0, 1) |
| 4 | +Z | (0, 0, 1) | (1, 0, 0) | (0, 1, 0) |
| 5 | −Z | (0, 0, −1) | (−1, 0, 0) | (0, 1, 0) |

Note faces 2 and 3 have non-obvious `up` vectors. They are what make the basis right-handed; changing them invalidates the adjacency table below.

### Cube-sphere projection

```
a = 2u − 1,  b = 2v − 1                    // face-local, [−1, 1]
a' = tan(a·π/4),  b' = tan(b·π/4)          // tangent adjustment (optional, on by default)
dir = normalize(centre + a'·right + b'·up)
point  = Radius · dir
normal = (float3)dir
```

`tan(±π/4) = ±1`, so corners land exactly on cube corners either way and the adjustment never changes face boundaries — only the interior distribution.

**Measured effect** (16×16 cells on one face, ratio of largest to smallest cell diagonal):

| | ratio |
|---|---|
| without tangent adjustment | 2.733 |
| with tangent adjustment | **1.621** |

Keep it configurable via a `TangentAdjust` flag so the test can assert both numbers. (`SonomaRevisedPlan.md` §4.1 predicted "under 1.5:1"; the measured figure is 1.62:1. Correct that line in the parent plan as part of Task 6.)

---

## Task 1 — Core types

Create `Assets/Scripts/Core/Surface/NodeId.cs`:

```csharp
using System;

namespace Sonoma.Core.Surface
{
    public enum Edge : byte { South = 0, East = 1, North = 2, West = 3 }

    /// Addresses any quadtree node without walking the tree.
    /// Quad indexes into the surface's root quad array.
    public readonly struct NodeId : IEquatable<NodeId>
    {
        public readonly int Quad, Depth, X, Y;

        public NodeId(int quad, int depth, int x, int y)
        { Quad = quad; Depth = depth; X = x; Y = y; }

        public int  Span   => 1 << Depth;                 // nodes per axis at this depth
        public bool IsRoot => Depth == 0;

        public NodeId Parent   => new NodeId(Quad, Depth - 1, X >> 1, Y >> 1);
        // i: 0 = SW, 1 = SE, 2 = NW, 3 = NE  (bit 0 = +u, bit 1 = +v)
        public NodeId Child(int i) => new NodeId(Quad, Depth + 1, (X << 1) | (i & 1), (Y << 1) | ((i >> 1) & 1));

        public double UMin => (double)X / Span;
        public double UMax => (double)(X + 1) / Span;
        public double VMin => (double)Y / Span;
        public double VMax => (double)(Y + 1) / Span;

        public bool Equals(NodeId o) => Quad == o.Quad && Depth == o.Depth && X == o.X && Y == o.Y;
        public override bool Equals(object o) => o is NodeId n && Equals(n);
        public override int GetHashCode() => HashCode.Combine(Quad, Depth, X, Y);
        public override string ToString() => $"Node(q{Quad} d{Depth} {X},{Y})";
        public static bool operator ==(NodeId a, NodeId b) =>  a.Equals(b);
        public static bool operator !=(NodeId a, NodeId b) => !a.Equals(b);
    }

    public struct NeighbourResult
    {
        public NodeId Node;
        public Edge   ArrivalEdge;  // edge of Node's quad through which we entered
        public bool   Exists;
    }
}
```

`ArrivalEdge` is what makes the round-trip test possible and is needed by M3's skirt logic, so return it even though same-quad callers can infer it.

Create `Assets/Scripts/Core/Surface/SurfaceDef.cs`:

```csharp
using Unity.Mathematics;

namespace Sonoma.Core.Surface
{
    public enum SurfaceType : byte { PlaneGrid, CubeSphere, Cylinder }

    /// One root quad of the base mesh.
    public struct RootQuad
    {
        public int     Face;      // cube face 0..5; else unused
        public double3 Origin;    // plane grid: tile's min corner in world space
        public double  Angle0, Angle1;   // cylinder: angular range
        public double  Z0, Z1;           // cylinder: axial range
    }

    /// Immutable description of the world topology. Blittable, Burst-friendly.
    public struct SurfaceDef
    {
        public SurfaceType Type;
        public double Radius;         // CubeSphere, Cylinder
        public double TileSize;       // PlaneGrid: edge length of one root tile
        public int    Cols, Rows;     // PlaneGrid and Cylinder tiling
        public bool   TangentAdjust;  // CubeSphere spacing correction (default true)

        public int QuadCount => Type == SurfaceType.CubeSphere ? 6 : Cols * Rows;
    }
}
```

---

## Task 2 — Surface evaluation

Create `Assets/Scripts/Core/Surface/Surface.cs`. Static class, `switch` dispatch, no allocation.

### Required API

```csharp
public static RootQuad[] BuildRoots(in SurfaceDef s);
public static double3 SurfacePoint (in SurfaceDef s, in RootQuad q, double u, double v);
public static float3  SurfaceNormal(in SurfaceDef s, in RootQuad q, double u, double v);
public static void    SurfaceFrame (in SurfaceDef s, in RootQuad q, double u, double v,
                                    out double3 point, out float3 normal);   // both at once
public static double  NodeWorldSize(in SurfaceDef s, in RootQuad q, NodeId n);
public static NeighbourResult Neighbour(in SurfaceDef s, NodeId n, Edge e);
```

`SurfaceFrame` is the one M2's job will actually call; the other two delegate to it. Height is added along `normal`, matching the existing `CoordinateTransform.GetBaseSurface` contract.

### Cube face basis table

```csharp
// centre, right, up per face. right × up == centre (right-handed).
// Faces 2 and 3 have deliberately non-obvious `up` vectors — do not "tidy" them.
static readonly double3[] FaceCentre = {
    new double3( 1, 0, 0), new double3(-1, 0, 0), new double3(0,  1, 0),
    new double3( 0,-1, 0), new double3( 0, 0, 1), new double3(0,  0,-1),
};
static readonly double3[] FaceRight = {
    new double3( 0, 0,-1), new double3( 0, 0, 1), new double3(1, 0, 0),
    new double3( 1, 0, 0), new double3( 1, 0, 0), new double3(-1,0, 0),
};
static readonly double3[] FaceUp = {
    new double3( 0, 1, 0), new double3( 0, 1, 0), new double3(0, 0,-1),
    new double3( 0, 0, 1), new double3( 0, 1, 0), new double3(0, 1, 0),
};
```

### Cube-sphere point

```csharp
static void CubeSphereFrame(in SurfaceDef s, int face, double u, double v,
                            out double3 point, out float3 normal)
{
    double a = 2.0 * u - 1.0;
    double b = 2.0 * v - 1.0;
    if (s.TangentAdjust)
    {
        a = math.tan(a * (math.PI_DBL * 0.25));
        b = math.tan(b * (math.PI_DBL * 0.25));
    }
    double3 d = FaceCentre[face] + a * FaceRight[face] + b * FaceUp[face];
    d = d / math.length(d);
    point  = s.Radius * d;
    normal = (float3)d;
}
```

### Plane grid and cylinder

- **PlaneGrid.** `Cols × Rows` tiles of `TileSize`, centred on the origin, laid out in the XZ plane with `u → +X`, `v → +Z`. `RootQuad.Origin` is the tile's min corner. Point is `Origin + (u·TileSize, 0, v·TileSize)`; normal is always `(0, 1, 0)`. Lazy infinite expansion is **out of scope for M1** (see Out of scope).
- **Cylinder.** `Cols` around, `Rows` along the axis (Z). `u` maps to `[Angle0, Angle1]`, `v` to `[Z0, Z1]`. Point is `Radius·(cos θ, sin θ, 0) + (0, 0, z)`; normal is `−normalize(radial)` (inward, for interior viewing). This matches the existing `CoordinateTransform` cylinder convention, which the M0 prototype already renders correctly. Choose `Rows` so root quads are roughly square: `Rows ≈ Length / (2π·Radius / Cols)`.

### NodeWorldSize

Do not derive analytically per topology. Compute both diagonals of the node in world space and return the larger:

```csharp
public static double NodeWorldSize(in SurfaceDef s, in RootQuad q, NodeId n)
{
    double3 p00 = SurfacePoint(s, q, n.UMin, n.VMin);
    double3 p11 = SurfacePoint(s, q, n.UMax, n.VMax);
    double3 p10 = SurfacePoint(s, q, n.UMax, n.VMin);
    double3 p01 = SurfacePoint(s, q, n.UMin, n.VMax);
    return math.max(math.distance(p00, p11), math.distance(p10, p01));
}
```

This is topology-agnostic, correct under the tangent adjustment, and is exactly what M3's LOD metric needs.

---

## Task 3 — Adjacency and neighbour lookup

### The cube edge table (verified — port verbatim)

Each entry is `{ neighbourFace, neighbourEdge, reversed }`, indexed by `face * 4 + edge` with `Edge` numbering `South=0, East=1, North=2, West=3`.

```csharp
// Derived geometrically and verified: all 24 edges round-trip, and the
// `reversed` flag is symmetric. See M1 plan §Task 3 and CubeAdjacencyTests.
static readonly int[] EdgeLinks = {
    3,1,1,  5,3,0,  2,1,0,  4,1,0,   // face 0 (+X)
    3,3,0,  4,3,0,  2,3,1,  5,1,0,   // face 1 (-X)
    4,2,0,  0,2,0,  5,2,1,  1,2,1,   // face 2 (+Y)
    5,0,1,  0,0,1,  4,0,0,  1,0,0,   // face 3 (-Y)
    3,2,0,  0,3,0,  2,0,0,  1,1,0,   // face 4 (+Z)
    3,0,1,  1,3,0,  2,2,1,  0,1,0,   // face 5 (-Z)
};
```

Human-readable form, for review:

| From | South → | East → | North → | West → |
|---|---|---|---|---|
| 0 (+X) | 3 (−Y) East **rev** | 5 (−Z) West | 2 (+Y) East | 4 (+Z) East |
| 1 (−X) | 3 (−Y) West | 4 (+Z) West | 2 (+Y) West **rev** | 5 (−Z) East |
| 2 (+Y) | 4 (+Z) North | 0 (+X) North | 5 (−Z) North **rev** | 1 (−X) North **rev** |
| 3 (−Y) | 5 (−Z) South **rev** | 0 (+X) South **rev** | 4 (+Z) South | 1 (−X) South |
| 4 (+Z) | 3 (−Y) North | 0 (+X) West | 2 (+Y) South | 1 (−X) East |
| 5 (−Z) | 3 (−Y) South **rev** | 1 (−X) West | 2 (+Y) North **rev** | 0 (+X) East |

Eight of the 24 links are reversed. That is correct, not a transcription error.

### Neighbour arithmetic

```csharp
public static NeighbourResult Neighbour(in SurfaceDef s, NodeId n, Edge e)
{
    int N = n.Span;

    // Same-quad: pure integer arithmetic.
    switch (e)
    {
        case Edge.South: if (n.Y > 0)     return Hit(new NodeId(n.Quad, n.Depth, n.X, n.Y - 1), Edge.North); break;
        case Edge.North: if (n.Y < N - 1) return Hit(new NodeId(n.Quad, n.Depth, n.X, n.Y + 1), Edge.South); break;
        case Edge.West:  if (n.X > 0)     return Hit(new NodeId(n.Quad, n.Depth, n.X - 1, n.Y), Edge.East);  break;
        case Edge.East:  if (n.X < N - 1) return Hit(new NodeId(n.Quad, n.Depth, n.X + 1, n.Y), Edge.West);  break;
    }

    // Crossed the root quad boundary.
    return CrossQuad(s, n, e, N);
}
```

`CrossQuad` per topology:

- **CubeSphere.** Look up `EdgeLinks[face*4 + (int)e]`. Take the along-edge index `t = (e is South or North) ? n.X : n.Y`, apply `t' = reversed ? (N - 1 - t) : t`, then place it on the neighbour's edge:

  ```csharp
  // ne = neighbour edge
  (int nx, int ny) = ne switch {
      Edge.South => (t2, 0),
      Edge.North => (t2, N - 1),
      Edge.West  => (0,  t2),
      Edge.East  => (N - 1, t2),
  };
  ```
  `ArrivalEdge` is `ne`.

- **Cylinder.** The quad index is `col * Rows + row`. Crossing East/West wraps the column modulo `Cols`; the along-edge index and edge orientation are unchanged, so `reversed` is always false. Crossing North/South moves `row ± 1` and returns `Exists = false` at the ends (open cylinder, no caps).

- **PlaneGrid.** Quad index is `col * Rows + row`. No wrapping in either axis; off-grid returns `Exists = false`. Orientation is uniform, so `reversed` is always false and `ArrivalEdge` is the opposite edge.

Add a helper `public static Edge Opposite(Edge e) => (Edge)(((int)e + 2) & 3);` — this works because the enum is ordered South, East, North, West.

---

## Task 4 — Debug visualisation

Create `Assets/Scripts/Tools/SurfaceDebugDrawer.cs` (global namespace, consistent with the other tools). A `MonoBehaviour` that draws the new surface layer **without** touching `QuadtreeManager`, so it can be dropped into an empty scene and inspected in the Editor without pressing Play.

Inspector fields: `SurfaceType`, `Radius`, `TileSize`, `Cols`, `Rows`, `TangentAdjust`, `DrawDepth` (0–5), `DrawNormals`, `HighlightQuad` (−1 = none), `NeighbourProbe` (a `NodeId` to highlight along with its four neighbours).

In `OnDrawGizmos`:

- Build roots via `Surface.BuildRoots` and draw every node at `DrawDepth`, sampling each edge at 8 points through `SurfacePoint` so curvature is visible.
- Colour by root quad index so face boundaries are obvious.
- With `DrawNormals`, draw a short line along `SurfaceNormal` at each node centre — this catches an inward/outward sign error immediately.
- With `NeighbourProbe` set, draw that node in white and its four neighbours in red/green/blue/yellow. Dragging the probe across a cube seam is the fastest way for a human to sanity-check the adjacency table.

Keep it allocation-light; gizmo code runs on every Editor repaint. Guard with `if (Cols < 1 || Rows < 1) return;` so partially-typed Inspector values do not throw.

---

## Task 5 — Tests (the actual deliverable)

Replace `Assets/Tests/EditMode/SmokeTests.cs` with real coverage. The M0 placeholder has served its purpose; delete it and its `.meta`.

Create these files under `Assets/Tests/EditMode/`. Every threshold below is a **measured** value with headroom, not a guess.

### `CubeAdjacencyTests.cs`

1. **`FaceBasisIsRightHanded`** — for all 6 faces, `cross(right, up) == centre` exactly (integer components, so assert equality, not approximate).
2. **`AdjacencyTableMatchesGeometricDerivation`** — the strongest test in M1. Independently re-derive the table in the test: for each `(face, edge)`, compute the two corner points on the unit cube, find the unique other `(face, edge)` sharing both corners, and determine `reversed` by whether the start corners differ. Assert the derived triple equals the hard-coded `EdgeLinks` entry. This makes a mis-typed table impossible to miss.
3. **`EdgeLinksRoundTrip`** — for all 24 `(face, edge)`, following the link and then following the arrival edge's link returns the original `(face, edge)`, and the `reversed` flag is the same in both directions.
4. **`SharedEdgePointsCoincide`** — for all 24 edges, sample `t ∈ [0,1]` at 33 points; the point from the owning face and the point from the neighbouring face (with `t` reversed where the flag says so) must coincide. **Measured worst case is 2.07e-16 relative at `Radius = 6.371e6`**, so assert `distance < 1e-9 * Radius` — six orders of magnitude of headroom, still tight enough to catch any real error. Run this test with `TangentAdjust` both on and off.

### `NeighbourTests.cs`

5. **`CubeNeighbourRoundTripAllDepths`** — for depths 0–4, all 6 faces, every `(x, y)`, all 4 edges: `Neighbour(Neighbour(n, e).Node, arrivalEdge).Node == n`. That is **8,184 cases and all must pass** (this exact count was verified before writing this plan; if your count differs, the loop bounds are wrong).
6. **`CylinderWrapsInColumnsNotRows`** — East/West from any column returns a node with `Exists == true` and wraps at the seam; North/South at `row 0` / `row Rows-1` returns `Exists == false`.
7. **`PlaneGridDoesNotWrap`** — boundary tiles return `Exists == false`; interior neighbours round-trip.
8. **`NeighboursAreGeometricallyAdjacent`** — for each node and edge, the returned neighbour's arrival edge must be the *same world-space segment* as the edge we crossed (endpoints coincide, in either orientation). **This test is load-bearing:** `CubeNeighbourRoundTripAllDepths` cannot catch a reversal that is dropped consistently, because the return hop repeats the same mistake and lands back at the start. Verified by mutation: removing the `Reversed` handling in `CrossQuad` passes every other test and fails only this one.
9. **`NeighbourIsAlwaysSameDepth`** — across all topologies, `Neighbour(...).Node.Depth == n.Depth` whenever `Exists`.

### `NodeIdTests.cs`

10. **`ChildParentRoundTrip`** — for depths 0–8 and a spread of `(x, y)`, `n.Child(i).Parent == n` for all four `i`.
11. **`ChildUvRangesTileParent`** — the four children's `[UMin,UMax] × [VMin,VMax]` exactly tile the parent's range with no gap or overlap.
12. **`EqualityAndHashing`** — equal `NodeId`s hash equally; a `HashSet<NodeId>` de-duplicates as expected. (M2's scheduler will key dictionaries on this.)

### `SurfaceGeometryTests.cs`

13. **`CubeSpherePointsLieOnSphere`** — random `(face, u, v)`, assert `|SurfacePoint| == Radius` within `1e-9 * Radius`, with `TangentAdjust` on and off.
14. **`TangentAdjustmentImprovesSpacing`** — 16×16 cells on one face, ratio of largest to smallest cell diagonal. Assert **`< 1.7` with adjustment** (measured 1.621) and **`> 2.5` without** (measured 2.733). Asserting both directions documents the intent and fails loudly if the adjustment is silently disabled.
15. **`NodeWorldSizeHalvesWithDepth`** — a child's `NodeWorldSize` is between 0.45× and 0.65× its parent's (measured range over depths 0-5 is 0.467..0.633; the tighter 0.4-0.6 window first written here would fail), across topologies and several depths.
16. **`CylinderNormalsPointInward`** — `dot(normal, radialDirection) < 0` for sampled points, matching the interior-viewing convention the prototype already uses.

**Determinism note:** where a test uses randomness, seed it with a fixed constant so failures reproduce.

---

## Task 6 — Documentation and commit

1. **Correct the parent plan.** In `SonomaRevisedPlan.md` §4.1, the sentence claiming the tangent adjustment reduces the spacing ratio to "under 1.5:1" is wrong. Change it to the measured **1.62:1 (from 2.73:1 unadjusted)**.
2. **Update `CLAUDE.md`:** add `Core/Surface/` to the Implemented systems list, note that `Sonoma.Core.Surface` is the M1 topology layer and that `Core/CoordinateSpace/` remains in use by the prototype until M2 retires it, and replace the "single placeholder smoke test" line under Testing with a note that the EditMode suite now covers surface adjacency and node addressing. Explicitly record the `Edge` vs `EdgeDirection` ordering difference in Non-Obvious Implementation Details — that is a genuine footgun.
3. **Commit** in two steps:

```bash
git add Assets/Scripts/Core/Surface Assets/Scripts/Tools/SurfaceDebugDrawer.cs
git commit -m "M1: cube-sphere surface layer with integer node addressing"
```

```bash
git add -A
git commit -m "M1: surface adjacency and node addressing tests, docs"
```

End every commit message with:

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

---

## Verification

### Agent-side, without the Editor

```bash
# New files present, old placeholder gone
find Assets/Scripts/Core/Surface Assets/Tests -type f -name "*.cs" | sort
test ! -f Assets/Tests/EditMode/SmokeTests.cs && echo "placeholder removed"

# Every .cs under Assets/Scripts has a .meta (Unity will generate new ones on import)
for f in $(find Assets/Scripts Assets/Tests -name "*.cs"); do test -f "$f.meta" || echo "META PENDING: $f"; done

# The prototype was not touched
git diff --stat main..m1-surfaces -- Assets/Scripts/Core/Quadtree Assets/Scripts/Core/CoordinateSpace \
    Assets/Scripts/Core/Generation Assets/Scripts/Core/Rendering Assets/Shaders
```

That last command **must print nothing**. If it does not, rule 1 has been broken.

### Human-side, in the Editor

1. Console is free of compile errors.
2. Test Runner → EditMode: all 16 tests pass. Confirm `CubeNeighbourRoundTripAllDepths` reports 8,184 checked cases.
3. Drop `SurfaceDebugDrawer` on an empty GameObject, set `CubeSphere`, `DrawDepth = 2`: the sphere is fully covered with no gaps or overlaps at face boundaries, normals point outward, and moving `NeighbourProbe` across a seam highlights the geometrically adjacent node on the next face.
4. `SampleScene` still plays exactly as before M1.

---

## Definition of done

- [ ] `Sonoma.Core.Surface` provides `NodeId`, `Edge`, `RootQuad`, `SurfaceDef`, and a static `Surface` with `BuildRoots`, `SurfaceFrame`, `SurfacePoint`, `SurfaceNormal`, `NodeWorldSize`, `Neighbour`, `Opposite`.
- [ ] Cube-sphere, plane grid and cylinder all evaluate and support neighbour queries; cube-sphere handles all 24 cross-face links including the eight reversed ones.
- [ ] No interfaces or virtual dispatch in the surface layer; `double3` positions, `float3` normals.
- [ ] All 16 EditMode tests pass, including the independent geometric re-derivation of the adjacency table.
- [ ] `SurfaceDebugDrawer` renders roots and nodes in the Editor without entering Play mode.
- [ ] The prototype rendering path is byte-for-byte unmodified and `SampleScene` still plays.
- [ ] `SonomaRevisedPlan.md` spacing-ratio figure corrected; `CLAUDE.md` updated.

## Out of scope for M1

Do not start these:

- **Any Burst job, `NativeArray`, or generation code.** That is M2.
- **Retiring `BaseMeshQuad` / `CoordinateTransform` or rewiring `QuadtreeManager`.** M2 does the switchover in one deliberate step.
- **Lazy infinite plane-grid expansion.** M1 ships a fixed `Cols × Rows` grid, which is sufficient to test adjacency semantics. Streaming-driven root creation belongs with M3's LOD work.
- **The height function, noise, or precision work.** M2.
- **2:1 quadtree balancing.** Optional, M6 at the earliest.
- **Cylinder end caps.** The cylinder is deliberately open; `Exists == false` at the ends is correct behaviour, not a gap to fill.
