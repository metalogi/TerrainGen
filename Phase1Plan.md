# Phase 1 Implementation Plan: Foundation

## Goal

A single terrain quad visible in the Unity scene: heightmap generated on a worker thread, mesh uploaded to the GPU, camera movement driving quadtree subdivision, origin rebase keeping float precision valid. No biomes, no seam stitching, no streaming — just the structural skeleton that everything else builds on.

---

## Step 1: Base Mesh and Coordinate Transform

**Files:** `Assets/Scripts/Core/CoordinateSpace/BaseMeshQuad.cs`, `CoordinateTransform.cs`, `BaseMeshFactory.cs`

### `BaseMeshQuad`

Plain data struct (no MonoBehaviour). Stores a surface type and the parametric range needed to compute positions analytically. Corner positions are stored for the plane case only.

```csharp
public enum SurfaceType { Plane, Sphere, Cylinder }

public struct BaseMeshQuad
{
    public SurfaceType Type;

    // Plane: corner positions in world absolute space (bilinear interpolation is exact for flat surfaces)
    public double3 P00, P10, P01, P11;
    public float3  N00, N10, N01, N11;

    // Sphere: parametric range — interpolate lat/lon, compute point analytically
    public double SphereRadius;
    public double Lat0, Lat1;   // latitude  at v=0 and v=1
    public double Lon0, Lon1;   // longitude at u=0 and u=1

    // Cylinder: parametric range — interpolate angle and z, compute point analytically
    public double CylRadius;
    public double Angle0, Angle1;  // angle at u=0 and u=1
    public double CylZ0,  CylZ1;  // z     at v=0 and v=1
}
```

**Why parametric storage instead of corners:** bilinear interpolation of Cartesian corners is only exact for flat planes. For a sphere, interpolating between corner positions in 3D space produces points inside the sphere — the error grows toward the interior of each quad and compounds through subdivision. Storing lat/lon (or angle/z) and computing positions analytically ensures child chunks at any depth conform to the true curved surface.

### `CoordinateTransform`

Static utility. Dispatches on `SurfaceType` to compute `(u, v, height)` → World Absolute position:

```csharp
public static double3 ToWorldPosition(double u, double v, double height, BaseMeshQuad quad)
{
    switch (quad.Type)
    {
        case SurfaceType.Sphere:
            double lat    = math.lerp(quad.Lat0, quad.Lat1, v);
            double lon    = math.lerp(quad.Lon0, quad.Lon1, u);
            double3 sPos  = SpherePoint(quad.SphereRadius, lat, lon);
            float3  sNorm = math.normalize((float3)(sPos / quad.SphereRadius));
            return sPos + (double3)(sNorm * (float)height);

        case SurfaceType.Cylinder:
            double  angle = math.lerp(quad.Angle0, quad.Angle1, u);
            double  z     = math.lerp(quad.CylZ0,  quad.CylZ1,  v);
            double3 radial = new double3(Math.Cos(angle), Math.Sin(angle), 0);
            float3  cNorm  = -(float3)radial;   // inward — negate for outward
            return quad.CylRadius * radial + new double3(0, 0, z) + (double3)(cNorm * (float)height);

        default: // Plane
            // Bilinear interpolation is exact for flat surfaces
            double3 pos = bilerp(quad.P00, quad.P10, quad.P01, quad.P11, u, v);
            float3  n   = math.normalize(bilerp(quad.N00, quad.N10, quad.N01, quad.N11, u, v));
            return pos + (double3)(n * (float)height);
    }
}
```

Use `double3` throughout for position arithmetic to preserve precision.

### `BaseMeshFactory`

Static factory with three creation methods returning `BaseMeshQuad[]`. Each quad stores its parametric range so `CoordinateTransform` can compute analytically correct positions at any subdivision depth.

- **`CreatePlane(float size)`** — one quad, `Type = Plane`, corners on XZ plane, normals up
- **`CreateUVSphere(float radius, int cols, int rows)`** — `cols × rows` quads, `Type = Sphere`, each quad stores its lat/lon range; adjacent quads share exact parametric boundary values so there is no seam
- **`CreateCylinder(float radius, float length, int cols)`** — `cols` quads tiling the wall, `Type = Cylinder`, horizontal axis (Z), inward-facing normals for interior viewing

**Testing:** verify in the gizmo view that quad boundaries on sphere and cylinder remain coincident at all shared edges, and that subdivided mesh chunks continue to conform to the curved surface (no faceting visible at deep LOD levels).

---

## Step 2: Quadtree Data Structure

**Files:** `Assets/Scripts/Core/Quadtree/QuadtreeNode.cs`, `QuadtreeBounds.cs`

### `QuadtreeBounds`

Stores a node's extent in Quadtree Space as a normalized [0,1] square, plus a reference to its `BaseMeshQuad`. The bounds know how to compute their world-space centre by calling `CoordinateTransform`:

```csharp
public struct QuadtreeBounds
{
    public float2 Min, Max;           // [0,1] within the root quad
    public int    QuadIndex;          // index into BaseMesh quad array
    
    public float2 Centre => (Min + Max) * 0.5f;
    public float  Size   => Max.x - Min.x;       // always square
    
    // Returns the four child bounds (NW, NE, SW, SE)
    public QuadtreeBounds[] Subdivide() { ... }
}
```

### `QuadtreeNode`

Plain class (not MonoBehaviour). Owns generation state, not rendering:

```csharp
public class QuadtreeNode
{
    public QuadtreeBounds  Bounds;
    public int             Depth;
    public QuadtreeNode    Parent;
    public QuadtreeNode[]  Children;   // null when leaf
    public NodeState       State;
    public TerrainChunk    Chunk;      // null until mesh is ready
}

public enum NodeState { Inactive, Generating, Active, Subdivided }
```

No Unity objects here — keeps the tree testable outside Play mode.

---

## Step 3: TerrainChunk (Render Object)

**Files:** `Assets/Scripts/Core/Rendering/TerrainChunk.cs`

MonoBehaviour attached to a generated GameObject. Owns:

- `MeshFilter` + `MeshRenderer`
- `NativeArray<float> Heightmap` (disposed when chunk unloads)
- Reference back to its `QuadtreeNode`

Lifecycle methods called by the manager, not self-driven:

```csharp
public void Initialize(QuadtreeNode node, Material material) { ... }
public void ApplyMesh(Mesh mesh) { ... }   // called on main thread after job completes
public void Dispose() { ... }              // releases NativeArray, destroys GameObject
```

Position is set in Local Render Space (camera-relative float) at `ApplyMesh` time. The `WorldOriginSystem` updates `transform.position` on rebase.

---

## Step 4: Heightmap Generation Job

**Files:** `Assets/Scripts/Core/Generation/HeightmapGeneratorJob.cs`

A `[BurstCompile] IJob` that fills a `NativeArray<float>` with height values.

### Level 0 input contract

Because Level 0 has no parent heightmap, the job receives the world-space transform so it can sample noise in surface space (see CLAUDE.md — this is required for sphere/cylinder seam-free wrapping):

```csharp
[BurstCompile]
public struct HeightmapGeneratorJob : IJob
{
    public int      Resolution;          // vertices per edge, e.g. 33
    public float2   BoundsMin, BoundsMax; // quadtree [0,1] extent
    // Quad corners for surface-space noise sampling (Level 0)
    public double3  P00, P10, P01, P11;
    public float3   N00, N10, N01, N11;
    // Noise parameters
    public float    BaseFrequency;
    public int      Octaves;
    public float    Persistence;
    public uint     Seed;

    public NativeArray<float> Output;

    public void Execute()
    {
        for (int y = 0; y < Resolution; y++)
        for (int x = 0; x < Resolution; x++)
        {
            float u = math.lerp(BoundsMin.x, BoundsMax.x, x / (float)(Resolution - 1));
            float v = math.lerp(BoundsMin.y, BoundsMax.y, y / (float)(Resolution - 1));

            // Surface-space position for noise — handles sphere/cylinder wrapping
            float3 surfacePos = (float3)CoordinateTransform.ToWorldPosition(
                u, v, 0f, P00, P10, P01, P11, N00, N10, N01, N11);

            float h = FractalNoise(surfacePos * BaseFrequency, Octaves, Persistence, Seed);
            Output[y * Resolution + x] = h;
        }
    }
}
```

Implement `FractalNoise` as static Burst-compatible 3D value or Perlin noise. No managed allocations inside `Execute`.

---

## Step 5: Mesh Generation Job

**Files:** `Assets/Scripts/Core/Generation/MeshGeneratorJob.cs`

A `[BurstCompile] IJob` that converts a heightmap into vertex and index buffers.

```csharp
[BurstCompile]
public struct MeshGeneratorJob : IJob
{
    public int      Resolution;
    public float    HeightScale;
    public float2   BoundsMin, BoundsMax;
    // Quad corners for world→local-render transform
    public double3  P00, P10, P01, P11;
    public float3   N00, N10, N01, N11;
    public double3  WorldOrigin;         // subtracted to produce Local Render Space

    [ReadOnly]  public NativeArray<float>   Heightmap;
    [WriteOnly] public NativeArray<float3>  Vertices;
    [WriteOnly] public NativeArray<float3>  Normals;
    [WriteOnly] public NativeArray<float2>  UVs;
    [WriteOnly] public NativeArray<int>     Indices;

    public void Execute() { ... }
}
```

Normals: compute analytically from heightmap finite differences. Indices: standard grid triangle list. Use `int` indices throughout; `Mesh.indexFormat` can be set to 16-bit if `Resolution ≤ 181` (181² = 32761 < 65536).

---

## Step 6: Mesh Upload and Main Thread Handoff

**Files:** `Assets/Scripts/Core/Rendering/MeshUploader.cs`

Static utility called on the main thread after both jobs complete:

```csharp
public static Mesh Upload(MeshGeneratorJob job)
{
    var mesh = new Mesh();
    mesh.indexFormat = job.Resolution <= 181 ? IndexFormat.UInt16 : IndexFormat.UInt32;
    mesh.SetVertexBufferParams(job.Vertices.Length, vertexLayout);
    mesh.SetVertexBufferData(job.Vertices, 0, 0, job.Vertices.Length);
    // ... normals, UVs
    mesh.SetIndexBufferParams(job.Indices.Length, mesh.indexFormat);
    mesh.SetIndexBufferData(job.Indices, 0, 0, job.Indices.Length);
    mesh.subMeshCount = 1;
    mesh.SetSubMesh(0, new SubMeshDescriptor(0, job.Indices.Length));
    mesh.RecalculateBounds();
    return mesh;
}
```

---

## Step 7: QuadtreeManager

**Files:** `Assets/Scripts/Core/Quadtree/QuadtreeManager.cs`

MonoBehaviour that owns the tree and drives it each frame.

### Initialization

```csharp
void Start()
{
    foreach (var quad in baseMesh.Quads)
        rootNodes.Add(new QuadtreeNode { Bounds = FullBounds(quad), Depth = 0 });

    foreach (var root in rootNodes)
        ScheduleGeneration(root);
}
```

### Per-frame LOD update (budget-capped)

```csharp
void Update()
{
    int uploadsThisFrame = 0;

    // 1. Collect completed jobs, upload meshes (max 3/frame)
    foreach (var pending in pendingUploads)
    {
        if (uploadsThisFrame >= maxUploadsPerFrame) break;
        pending.JobHandle.Complete();
        var mesh = MeshUploader.Upload(pending.MeshJob);
        pending.Node.Chunk.ApplyMesh(mesh);
        pending.Node.State = NodeState.Active;
        uploadsThisFrame++;
    }

    // 2. Traverse tree, decide split/merge
    foreach (var root in rootNodes)
        UpdateNode(root, Camera.main.transform.position);
}
```

### Split / merge logic

```csharp
void UpdateNode(QuadtreeNode node, Vector3 cameraPos)
{
    float dist = WorldDistanceToNode(node, cameraPos);
    float splitDist = lodDistances[node.Depth];

    if (node.Children == null && dist < splitDist && node.Depth < maxDepth)
        Subdivide(node);
    else if (node.Children != null && dist > splitDist * hysteresis)
        Collapse(node);
    else if (node.Children != null)
        foreach (var child in node.Children)
            UpdateNode(child, cameraPos);
}
```

`lodDistances` is a `float[]` ScriptableObject field, indexed by depth. `hysteresis` defaults to 1.2.

`WorldDistanceToNode`: transform node centre from Quadtree Space to Local Render Space via `CoordinateTransform`, then `Vector3.Distance`.

---

## Step 8: Origin Rebasing System

**Files:** `Assets/Scripts/Core/CoordinateSpace/WorldOriginSystem.cs`

MonoBehaviour on the camera (or a manager object).

```csharp
public class WorldOriginSystem : MonoBehaviour
{
    public static double3 WorldOrigin { get; private set; }

    const float RebaseThreshold = 1000f;

    void LateUpdate()
    {
        Vector3 camPos = Camera.main.transform.position;
        if (math.length((float3)camPos) < RebaseThreshold) return;

        double3 shift = (double3)(float3)camPos;
        WorldOrigin += shift;

        // Shift all active chunk GameObjects
        foreach (var chunk in TerrainChunk.AllActive)
            chunk.transform.position -= (Vector3)(float3)shift;

        // Keep camera at origin
        Camera.main.transform.position = Vector3.zero;
    }
}
```

`TerrainChunk.AllActive` is a static `HashSet<TerrainChunk>` populated in `Initialize` / `Dispose`.

---

## Step 9: Global Settings ScriptableObject

**Files:** `Assets/Scripts/Systems/Configuration/TerrainSettings.cs`

```csharp
[CreateAssetMenu(fileName = "TerrainSettings", menuName = "Sonoma/Terrain Settings")]
public class TerrainSettings : ScriptableObject
{
    public int   ChunkResolution    = 33;
    public int   MaxDepth           = 8;
    public int   MaxUploadsPerFrame = 3;
    public float HysteresisFactor   = 1.2f;
    public float HeightScale        = 200f;
    public float[] LodDistances;        // one entry per depth level

    // Level 0 noise
    public float BaseFrequency = 0.5f;
    public int   Octaves       = 6;
    public float Persistence   = 0.5f;
    public uint  Seed          = 42;
}
```

`QuadtreeManager` takes a `TerrainSettings` reference. No magic numbers in code.

---

## Implementation Order

Work in this sequence — each step is testable before the next begins:

1. **`BaseMeshQuad` + `CoordinateTransform` + `BaseMeshFactory`** — verify with Gizmos in Edit Mode
2. **`QuadtreeBounds` + `QuadtreeNode`** — unit-test subdivision in Play Mode, draw bounds as Gizmos
3. **`TerrainChunk`** — manually create one, display a flat Unity `Mesh` at a known position
4. **`HeightmapGeneratorJob`** — schedule manually, visualize output as a Texture2D in a debug window
5. **`MeshGeneratorJob` + `MeshUploader`** — apply to one `TerrainChunk`, see displaced mesh in scene
6. **`QuadtreeManager`** — drive subdivision from camera; confirm split/merge triggers correctly
7. **`WorldOriginSystem`** — move camera 2000+ units, confirm chunks reposition seamlessly
8. **`TerrainSettings` ScriptableObject** — replace all hard-coded values

---

## Definition of Done for Phase 1

- [ ] Plane, sphere, and cylinder Base Meshes construct without seams (verified via Gizmos)
- [ ] Quadtree subdivides and collapses correctly as camera moves, no thrashing
- [ ] Level 0 heightmap generates on a worker thread; 3D noise sampling prevents seams at root-quad boundaries on sphere/cylinder
- [ ] Mesh uploads are budget-capped at `MaxUploadsPerFrame`; no frame spikes visible in Profiler
- [ ] Camera can travel > 10,000 units without visible float precision artifacts; origin rebase is seamless
- [ ] All magic numbers live in `TerrainSettings` asset
- [ ] No managed allocations in Jobs (Burst compilation succeeds with no warnings)
