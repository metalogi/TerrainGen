# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Sonoma is a Unity 6 (6000.3.5f2) project implementing a procedural terrain generation system using hierarchical heightmap-based terrain with quadtree LOD management. The system generates realistic natural landscapes from continent-scale down to third-person game scale with seamless LOD transitions.

**Current Status:** Foundation phase - `Assets/Scripts/` directory tree exists but all folders are empty. The only existing C# code is `Assets/TutorialInfo/Scripts/` (Unity boilerplate that can be deleted). All core terrain systems need to be built from scratch.

## Unity Development Commands

### Opening the Project
- Open Unity Hub and add this project directory
- Unity Editor Version: 6000.3.5f2
- The project uses Universal Render Pipeline (URP)

### Building
- Build through Unity Editor: File → Build Settings → Build
- Target Platform: Windows 64-bit (can be changed in Build Settings)
- No automated build scripts currently configured

### Testing
- Unity Test Runner: Window → General → Test Runner
- Run tests via Test Framework package (com.unity.test-framework@1.6.0)
- No tests currently implemented - tests should be created alongside core systems

### Editor Scripts
- Editor scripts go in `Assets/Editor/` or `Assets/*/Editor/` folders
- Editor code compiles to Assembly-CSharp-Editor.csproj
- Runtime code compiles to Assembly-CSharp.csproj

### Important Unity Packages
- **URP** (17.3.0): Universal Render Pipeline - all rendering uses URP shaders
- **Input System** (1.17.0): Use new Input System, not legacy Input Manager
- **Burst Compiler**: Used for high-performance terrain generation jobs
- **Jobs System**: Multithreading for terrain generation pipeline
- **AI Navigation** (2.0.9): For NavMesh generation on procedural terrain

## Core Architecture

### Coordinate Systems (Critical Concept)

The system uses **four coordinate spaces** with transformations between them:

1. **Quadtree Space**: Local [0,1] normalized coordinates per quadtree node
   - All generation algorithms work in this simple Cartesian space
   - Z-axis represents height/elevation

2. **World Space**: Curved surfaces mapped from Quadtree Space
   - Supports planes, spheres, cylinders, or custom Base Mesh topologies
   - `BaseMeshQuad` stores a `SurfaceType` enum and parametric range, not just Cartesian corners
   - `CoordinateTransform` dispatches on `SurfaceType` and computes positions analytically

3. **Local Render Space**: Camera-relative float coordinates
   - Used for actual Unity rendering to maintain precision
   - Origin rebases when camera moves beyond threshold (~1000 units)

4. **World Absolute Space**: Double-precision global coordinates
   - Tracks true position in large worlds
   - Prevents floating-point precision issues at scale

**Implementation Note:** Always generate in Quadtree Space, transform to World Space, then offset to Local Render Space for rendering.

### Surface Parameterization (Critical for Curved Topologies)

Bilinear interpolation of Cartesian corner positions is only exact for flat planes. For spheres and cylinders, interpolating corners in 3D space produces points that drift off the true surface — the error grows toward the interior of a quad and compounds through subdivision levels, producing a faceted approximation rather than the true curve.

**Rule:** `CoordinateTransform.ToWorldPosition` must use topology-aware interpolation:

- **Plane** — bilinear interpolation of the four corner positions is exact
- **Sphere** — interpolate lat/lon parametrically, then compute `SpherePoint(lat, lon, radius)` analytically. Do **not** bilinearly interpolate Cartesian corners.
- **Cylinder** — interpolate angle and z parametrically, then compute `CylPoint(angle, z, radius)` analytically.

**Consequence for `BaseMeshQuad`**: the struct must store the parametric range for its surface type (lat0/lat1/lon0/lon1 for sphere; angle0/angle1/z0/z1 for cylinder), not just the four corner positions. A `SurfaceType` enum field drives dispatch in `CoordinateTransform`. Corner positions may still be stored for plane quads where bilinear interpolation is used.

This ensures child chunks at any subdivision depth conform to the true curved surface, not a flat facet of the root quad.

### Quadtree Spatial Hierarchy

- **Root nodes**: One per Base Mesh quad (defines world topology)
- **Child nodes**: Each node subdivides into 4 children based on camera distance
- **Typical depth**: 8-12 levels (configurable)
- **Node size**: Each level is 50% of parent size
- **LOD transitions**: Distance-based with hysteresis to prevent thrashing

### Generation Pipeline (Multithreaded)

**Stage 1 - Height Generation (Worker Thread)**
- Input: Parent heightmap, biome data, noise parameters
- Process: Multi-octave noise, biome modulation
- Technology: Unity Jobs + Burst compilation
- Output: Float array heightmap

**Stage 2 - Mesh Generation (Worker Thread)**
- Input: Heightmap, neighbor info for seam stitching
- Process: Vertex positions, normals, UVs
- Technology: Unity Jobs + Burst compilation
- Output: Vertex/index buffers (NativeArray)

**Stage 3 - Mesh Upload (Main Thread)**
- Create Unity Mesh, upload to GPU
- Must happen on main thread (Unity API requirement)

**Stage 4 - Material Assignment (Main Thread)**
- Assign materials, textures, place vegetation
- Biome-driven material parameters

**Synchronization:** Job dependencies ensure parent levels complete before children. Budget max 3 chunks/frame for mesh uploads to maintain framerate.

### Hierarchical Generation Levels

**Level 0 - Continental (Base Resolution):** Large-scale noise/tectonic simulation generating low-res heightmap (~128×128 per Base Mesh quad), biome classification, climate data (temperature/rainfall), and major feature metadata (mountain ranges, coastlines).

**Exception to "generate in Quadtree Space":** Level 0 has no parent to inherit edge values from, so adjacent root quads would produce mismatching edges if noise is sampled in local [0,1] space. Level 0 generation must receive the world-space transform and sample noise in surface-space coordinates:
- **Sphere**: sample 3D noise at the actual point on the sphere surface — topologically adjacent points on different root quads are spatially adjacent in 3D noise space, so wrapping is implicit
- **Cylinder**: use cylindrical parameterization for the wrap axis (map to `sin/cos`) so the seam closes naturally
- **Plane**: local quadtree coordinates work fine; no wrapping required

Levels 1–N don't share this constraint — they inherit correct edge values from their parent heightmap and seam-stitch to neighbors, so continuity is guaranteed structurally.

**Levels 1–N - Progressive Refinement:** Each level:
1. Upsample parent heightmap 2×
2. Add detail noise at current frequency
3. Apply biome-specific modulation (flatten plains, sharpen mountains)
4. Derive slope/curvature maps for material blending and vegetation density

Typical 4–8 refinement levels depending on scale range.

### Seam Elimination Strategy

Uses **T-junction stitching** to prevent cracks between LOD levels:

1. **Edge Classification**: Interior (same LOD) / Transition (different LOD) / Boundary
2. **Shared Vertex Policy**: Higher-detail chunks sample coarser neighbor edge vertices
3. **Edge Locking**: Constrain boundary vertices to match neighbor heightmap exactly
4. **Skirt Fallback**: Thin vertical skirts under terrain edges as backup

**Critical:** When generating a chunk, always query neighbor chunks and lock edge vertices if neighbor is coarser LOD.

### LOD Transition Morphing

Vertex blend parameter for smooth transitions: `blend = saturate((distance - minDistance) / transitionRange)`. Morph vertices toward parent LOD positions as camera approaches transition distance. Hysteresis factor (e.g. 1.2×) prevents subdivision/collapse thrashing.

### Chunk Unloading

When loaded chunk count exceeds budget: sort by camera distance, unload furthest first. Always preserve chunks that are parents of loaded children. Retain generation seed/parameters for fast regeneration; optionally cache heightmap data to skip Stage 1 on reload.

## Implementation Phases (5-Phase Roadmap)

### Phase 1: Foundation (Current Target)
- [ ] Base Mesh system and coordinate transformation matrices
- [ ] Quadtree data structure (QuadtreeNode, bounds, children)
- [ ] Camera distance queries for LOD decisions
- [ ] Basic single-level heightmap generation
- [ ] Origin rebasing system for large worlds

### Phase 2: LOD and Streaming
- [ ] Multi-level quadtree with distance-based subdivision/collapse
- [ ] T-junction seam stitching implementation
- [ ] Chunk loading/unloading based on camera distance
- [ ] Memory budget tracking (target: 500-2000 active chunks)

### Phase 3: Generation Pipeline
- [ ] Hierarchical generation with parent→child dependencies
- [ ] Multi-threaded generation using Jobs/Burst
- [ ] Biome system with feature placement
- [ ] Material and texture generation per chunk

### Phase 4: Polish and Optimization
- [ ] LOD transition morphing (vertex blending near transitions)
- [ ] Generation caching system for frequently visited areas
- [ ] Editor debug visualization (quadtree bounds, LOD levels)
- [ ] Performance profiling overlay

### Phase 5: Content and Assets
- [ ] Vegetation placement system
- [ ] Rock and detail object scattering
- [ ] Shader parameter generation (splat maps, normal maps)
- [ ] Biome definition library (ScriptableObjects)

## Planned Code Organization

Expected folder structure as systems are implemented:

```
Assets/Scripts/
├── Core/
│   ├── Quadtree/           # QuadtreeNode, LODSystem, QuadtreeManager
│   ├── Generation/         # HeightmapGenerator, MeshGenerator, BiomeSystem
│   ├── Rendering/          # TerrainChunk, ChunkMeshRenderer, MaterialGenerator
│   └── CoordinateSpace/    # BaseGeometry, CoordinateTransform, OriginRebasing
├── Systems/
│   ├── Streaming/          # ChunkLoader, MemoryManager
│   ├── Performance/        # ProfilingSystem, PerformanceMonitor
│   └── Configuration/      # GenerationProfile, BiomeDefinition (ScriptableObjects)
└── Editor/                 # Custom inspectors, debug tools, visualization
```

## Technical Specifications

### Performance Targets
- **Frame Budget**: < 2ms per frame for all terrain updates
- **Generation Time**: Chunks ready before player arrival at max camera velocity
- **Memory Budget**: Configurable (typically 500-2000 active chunks)
- **Visible Triangles**: 50K-500K depending on LOD

### Mesh Specifications
- **Vertex Format**: Position, Normal, UV (optional: Tangent, Color)
- **Typical Resolutions**: 33x33, 65x65, 129x129, 257x257 vertices per chunk
- **Index Format**: 16-bit for small chunks, 32-bit for large chunks

### Data-Driven Configuration
All generation parameters should be ScriptableObjects:
- **Global Settings**: World scale, quadtree depth, generation distances
- **Per-Level Settings**: Chunk resolution, visibility distance, noise parameters
- **Biome Definitions**: Height ranges, slope constraints, vegetation density curves

## Important Design Decisions

1. **Heightmap-Based Only**: No voxels or overhangs (caves/overhangs are future work or handled with separate meshes)
2. **Deterministic Generation**: Same seed → same terrain (important for networking/saves)
3. **Collision Meshes**: Use simplified versions of visual mesh (separate LOD chain)
4. **Water Bodies**: Separate system, not integrated into heightmap
5. **Static Terrain**: No runtime deformation or destruction in initial implementation

## Unity-Specific Best Practices for This Project

- **Jobs System**: Use `IJobParallelFor` for heightmap generation, `IJob` for mesh building
- **Burst Compilation**: Mark all generation jobs with `[BurstCompile]` attribute
- **NativeCollections**: Use `NativeArray<T>` for passing data to/from jobs
- **Mesh.SetVertexBufferData**: Use for efficient mesh uploads from native arrays
- **ScriptableObjects**: All configuration data (biomes, generation profiles, global settings)
- **Custom Inspectors**: Create custom editors for complex configuration data
- **Gizmos**: Draw quadtree bounds, LOD levels, camera ranges for debugging

## Key References

- **Design Document**: See `SonomaOverview_Expanded.md` for complete technical specification
- **Unity Jobs Documentation**: https://docs.unity3d.com/Manual/JobSystem.html
- **Burst Compiler**: https://docs.unity3d.com/Packages/com.unity.burst@latest
- **URP Shader Graph**: For terrain material generation
