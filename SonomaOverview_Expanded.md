# Sonoma Terrain Generation System
## Technical Design Document

This document describes a system built in the Unity game engine to generate realistic procedurally generated natural landscapes using hierarchical heightmap-based terrain generation.

---

## Core Capabilities

* **Multi-scale landscape generation** from continent-sized down to third-person game scale. Each level has appropriate detail for its viewing distance, with smooth LOD transitions (minor popping acceptable).

* **Modular, hierarchical procedural generation** using different algorithms at different scale levels, with each level's output feeding the next as input.

* **Data-driven parameters** with Editor GUIs to edit serializable configuration data.

* **Performant realtime generation** using multithreading, Unity DOTS, Jobs, and Burst compiler.

* **Procedurally generated terrain geometry** with asset-based vegetation and rocks controlled by procedural placement algorithms. Terrain shader parameters are also procedurally generated.

---

## World Geometry Architecture

### Base Mesh Structure

The terrain system uses an arbitrary **Base Mesh** composed of quads to define the world's fundamental shape. This enables support for various world topologies:

- **Plane** - traditional flat world
- **Sphere** - planetary surfaces
- **Inside of cylinder** - ringworld/O'Neill cylinder environments
- **Other custom shapes** - any quad-based geometry

Each quad in the Base Mesh represents the root of a quadtree hierarchy and defines a region of the world to be generated.

### Coordinate Space Transformation

All procedural generation occurs in **Quadtree Space** - a regular orthogonal Cartesian coordinate system where:
- Each quadtree node is a square in the XY plane
- Height (Z-axis) represents elevation
- Coordinates are normalized [0,1] within each node's bounds

**Transformation to World Space:**

For each Base Mesh quad, the system calculates a transformation matrix that:
1. Maps the Quadtree Space XY coordinates to the quad's surface
2. Extrudes along the quad's corner normals for height displacement
3. Applies the transformation to generated chunk geometry before rendering

This approach provides several advantages:
- All generation algorithms work in simple Cartesian space
- Easy to reason about and debug generation logic
- Supports arbitrary world curvature without modifying generation code
- Allows seamless transitions between different world topologies

The transformation matrix is computed per-quad and cached, incorporating:
- Quad corner positions in world space
- Interpolated normal directions for height extrusion
- Scaling factors to maintain consistent feature sizes across the surface

---

## Level of Detail (LOD) System

### Quadtree Spatial Partitioning

The terrain uses a **quadtree hierarchy** for spatial organization:

- **Root nodes** - one per Base Mesh quad
- **Child nodes** - each node subdivides into 4 children when higher detail is needed
- **Maximum depth** - configurable per world (e.g., 8-12 levels typical)
- **Node size** - each level is half the size of its parent

Example hierarchy for a 1024x1024 unit world:
- Level 0: 1024x1024 (root)
- Level 1: 512x512 (4 nodes)
- Level 2: 256x256 (16 nodes)
- Level 3: 128x128 (64 nodes)
- etc.

### LOD Transition Strategy

**Distance-based LOD selection:**
- Each quadtree level has a configured **visibility distance**
- When camera distance to node center < visibility distance → subdivide to children
- When camera distance > visibility distance * hysteresis factor → collapse to parent
- Hysteresis prevents thrashing (e.g., hysteresis = 1.2)

**Transition smoothing:**
- Morph vertices toward parent LOD positions when near transition distance
- Alpha blend parameter: `blend = saturate((distance - minDistance) / transitionRange)`
- Provides smooth visual fade instead of sudden pop

### Seam Elimination Strategy

**T-junction stitching approach:**

T-junctions occur where different LOD levels meet. The system uses vertex welding and shared edge constraints:

1. **Edge classification** - each chunk edge is classified as:
   - Interior edge (same LOD neighbor)
   - Transition edge (lower LOD neighbor)
   - Boundary edge (no neighbor/world edge)

2. **Shared vertex policy:**
   - When generating a chunk next to a lower-LOD neighbor, the higher-detail chunk samples the coarser chunk's edge vertices
   - Constrain edge vertices to lie exactly on the neighbor's edge geometry
   - Forces continuity even with different tessellation densities

3. **Implementation:**
   - Query neighbor chunks during generation
   - If neighbor is coarser LOD, sample its edge heightmap at matching positions
   - Lock edge vertices to sampled heights
   - Interior vertices remain unconstrained

4. **Skirt fallback:**
   - Thin vertical "skirts" dropped from chunk edges as backup
   - Hidden beneath terrain, only visible if stitching fails
   - Minimal performance cost as insurance against gaps

This approach:
- Eliminates visible seams without geometric skirts
- Works with arbitrary LOD level differences
- Maintains crack-free terrain during all transitions
- Adds minimal overhead to generation

---

## Hierarchical Generation Pipeline

### Level 0: Continental Features (Base Resolution)

**Purpose:** Generate large-scale features visible at maximum zoom-out

**Algorithm:** Large-scale noise functions, tectonic simulation, or graph-based generation

**Outputs:**
- Low-resolution heightmap (e.g., 128x128 per Base Mesh quad)
- Biome classification map
- Climate data (temperature, rainfall)
- Major feature metadata (mountain ranges, river networks, coastlines)

**Rendering:**
- Geometry: Base Mesh quad tessellated into grid
- Textures: Biome colors, basic shading
- Used when camera is at continental view distance

### Level 1-N: Progressive Refinement (Heightmap Levels)

**Purpose:** Progressively add detail as camera approaches

**Algorithm:** Detail noise added to parent level heightmap, respecting biome constraints

**Process per level:**
1. Sample parent level heightmap (upscale 2x)
2. Add detail noise at current frequency
3. Apply biome-specific modulation (e.g., flatten plains, sharpen mountains)
4. Generate derivative maps (slope, curvature) for shading and placement

**Outputs per chunk:**
- Heightmap at current resolution (e.g., 65x65, 129x129, 257x257 vertices)
- Normal map (optional, can be calculated in shader)
- Splat map for texture blending
- Placement density maps for vegetation/rocks

**Level Count:** Typically 4-8 levels depending on world scale and detail requirements

**Rendering:**
- Geometry: Tessellated grid mesh displaced by heightmap
- Textures: Multi-layer terrain shader with splat blending
- Materials: Parameters driven by biome data and slope/height
- Vegetation/Props: Procedurally placed based on density maps

---

## Coordinate Precision Management

### Origin Rebasing System

To handle continent-scale worlds without floating-point precision issues:

**Strategy:**
- Maintain a **world origin offset** that moves with the camera
- When camera moves beyond threshold distance from current origin (e.g., 1000 units):
  1. Shift world origin to camera position
  2. Update all active chunk positions relative to new origin
  3. Update physics world origin
  4. Maintain double-precision coordinates for actual world position

**Implementation:**
- Camera stays near (0,0,0) in Unity world space
- All terrain chunks positioned relative to current origin
- Conversion functions between "world absolute" (double) and "local render" (float) coordinates
- Seamless to generation system - chunks generated in local coordinates

**Benefits:**
- Maintains full float precision for rendering at all locations
- Supports truly infinite worlds
- Transparent to rendering and physics systems
- Small performance cost (coordinate updates only during rebase)

---

## Streaming and Memory Management

### Chunk Loading Strategy

**Distance-based generation trigger:**
- **Generation distance** - configurable radius around camera (e.g., 2000 units)
- When camera moves, check if any quadtree nodes within generation distance are not loaded
- Queue generation jobs for required chunks in priority order (closest first)

**Background generation:**
- Generation jobs run on worker threads using Unity Jobs/Burst
- Chunks generate LOD levels as needed based on current camera distance
- Mesh creation marshalled back to main thread
- Target: chunks ready before player reaches them (assume max camera velocity)

### Chunk Unloading Strategy

**Memory-based unloading:**
- Track **loaded chunk count** against configurable maximum (e.g., 500 chunks)
- When limit exceeded, unload furthest chunks first
- Unload order: distance from camera (furthest first)
- Preserve chunks that are parents of loaded children to maintain hierarchy

**Unload process:**
1. Identify chunks beyond retention distance
2. If over memory budget, sort by distance and mark for unload
3. Release mesh data, textures, and GPU resources
4. Keep generation seed/parameters for fast regeneration if needed
5. Remove from active render list

**Optimization strategies:**
- Cache generated heightmap data for quick reload (configurable cache size)
- Serialize frequently-visited areas to disk
- Prioritize unloading of highest-LOD chunks (most memory, least visible impact)

---

## Multithreading Architecture

### Generation Pipeline Stages

**Stage 1: Height Generation (Worker Thread)**
- Input: Parent heightmap, biome data, noise parameters
- Process: Multi-octave noise, biome modulation, feature placement
- Output: Float array heightmap, metadata
- Technology: Unity Jobs with Burst compilation

**Stage 2: Mesh Generation (Worker Thread)**
- Input: Heightmap, LOD level, neighbor information for seam stitching
- Process: Vertex position calculation, normal computation, UV mapping
- Output: Vertex/index buffers (native arrays)
- Technology: Unity Jobs with Burst compilation

**Stage 3: Mesh Upload (Main Thread)**
- Input: Vertex/index buffers from worker thread
- Process: Create Unity Mesh, upload to GPU
- Output: Renderable mesh component
- Technology: Main thread only (Unity API requirement)

**Stage 4: Material/Texture Assignment (Main Thread)**
- Input: Biome data, placement maps
- Process: Assign materials, generate/assign textures, place vegetation
- Output: Fully configured terrain chunk
- Technology: Main thread

### Synchronization Strategy

- Job dependencies ensure parent levels complete before children start
- Producer-consumer queue for main thread uploads
- Budget per-frame mesh uploads to maintain framerate (e.g., max 3 chunks/frame)
- Priority system ensures closest chunks process first

---

## Data-Driven Configuration

### Serializable Parameters

**Global Settings:**
- World scale and Base Mesh definition
- Quadtree maximum depth
- Generation distance, unload distance
- Max loaded chunk count
- Camera velocity assumptions for pregeneration

**Per-Level Settings:**
- Chunk resolution (vertices per edge)
- Visibility distance for LOD transitions
- Noise function parameters (frequency, amplitude, octaves)
- Biome blending rules

**Biome Definitions:**
- Height range applicability
- Slope constraints
- Noise modulation parameters
- Texture layers and blend weights
- Vegetation/rock types and density curves

### Editor Integration

**Inspector UI:**
- Custom editors for generation profiles
- Real-time preview of noise functions
- Biome painting tools for artistic override
- Debug visualization (quadtree bounds, LOD levels, seams)

**Testing Tools:**
- Jump to coordinate function
- Force regeneration of specific chunks
- LOD freeze for debugging transitions
- Performance profiling overlay (generation time, memory usage)

---

## Technical Specifications

### Performance Targets

- **Frame Budget:** < 2ms total per frame for all terrain updates
- **Generation Time:** Chunks ready before player arrival at max velocity
- **Memory Budget:** Configurable, typically 500-2000 active chunks
- **Polygon Count:** Variable by LOD, e.g., 50K-500K triangles visible

### Mesh Specifications

- **Vertex Format:** Position, Normal, UV, (optional: Tangent, Color)
- **Index Format:** 16-bit or 32-bit based on chunk resolution
- **Topology:** Triangle list
- **Typical Resolutions:** 33x33, 65x65, 129x129, 257x257 vertices per chunk

### Coordinate Ranges

- **Quadtree Space:** [0,1] normalized per node
- **Local Render Space:** Float precision, camera-relative
- **World Absolute Space:** Double precision for large worlds

---

## Implementation Phases

### Phase 1: Foundation
- Base Mesh system and coordinate transformation
- Quadtree structure and camera distance queries
- Basic heightmap generation (single level)
- Origin rebasing system

### Phase 2: LOD and Streaming
- Multi-level quadtree LOD
- Distance-based subdivision/collapse
- Seam stitching implementation
- Chunk loading/unloading system

### Phase 3: Generation Pipeline
- Hierarchical generation with parent dependency
- Multi-threaded generation using Jobs/Burst
- Biome system and feature placement
- Material and texture generation

### Phase 4: Polish and Optimization
- LOD transition morphing
- Generation caching system
- Editor tools and visualization
- Performance profiling and optimization

### Phase 5: Content and Assets
- Vegetation placement system
- Rock and detail object scattering
- Shader parameter generation
- Biome definition library

---

## Open Questions and Future Considerations

1. **Collision mesh generation:** Separate simplified mesh or use visual mesh?
2. **Water bodies:** Integrated into heightmap or separate system?
3. **Caves and overhangs:** Future voxel layer, or fake with decals/meshes?
4. **Determinism:** Require same terrain from same seed for networking/saves?
5. **Artist override:** Ability to hand-author specific regions?
6. **Physics:** Terrain deformation, destruction, or fully static?

---

## Summary

This system provides a robust foundation for procedural terrain generation at arbitrary scales. The heightmap-based approach with quadtree LOD management offers:

- **Flexibility:** Base Mesh supports diverse world shapes
- **Performance:** Distance-based streaming with multithreaded generation
- **Quality:** Seam-free LOD transitions with proper stitching
- **Scalability:** Handles continent-scale worlds with origin rebasing
- **Maintainability:** Data-driven configuration with editor tools

The design balances technical sophistication with practical implementation concerns, providing clear paths for both initial development and future enhancements.
