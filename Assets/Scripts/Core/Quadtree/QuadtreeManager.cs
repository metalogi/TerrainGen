using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using Sonoma.Core.CoordinateSpace;
using Sonoma.Core.Generation;
using Sonoma.Core.Rendering;
using Sonoma.Systems.Configuration;

namespace Sonoma.Core.Quadtree
{
    public enum WorldTopology { Plane, UVSphere, Cylinder }
    public enum EdgeDirection  { North, South, East, West }

    public class QuadtreeManager : MonoBehaviour
    {
        [Header("Settings")]
        public TerrainSettings Settings;
        public Material ChunkMaterial;

        [Header("World Topology")]
        public WorldTopology Topology    = WorldTopology.Plane;
        public float PlaneSize           = 1000f;
        public float SphereRadius        = 500f;
        public int   SphereCols          = 8;
        public int   SphereRows          = 4;
        public float CylRadius           = 400f;
        public float CylHeight           = 1000f;
        public int   CylCols             = 8;

        [Header("Fallbacks (used when Settings is null)")]
        public int   DefaultMaxDepth          = 6;
        public float DefaultBaseSplitDistance = 200f;
        public float DefaultHysteresis        = 1.2f;

        BaseMeshQuad[]      quads;
        List<QuadtreeNode>  roots            = new List<QuadtreeNode>();
        Queue<QuadtreeNode> pendingGeneration = new Queue<QuadtreeNode>();
        Camera              mainCamera;

        // Spatial index: maps QuadtreeBounds → the node currently responsible for that region.
        // Only nodes that are the active leaf (visible or generation-pending) are registered.
        // Coarser nodes are deregistered when their children take over (TryHideParent),
        // and re-registered when children are collapsed (CollapseNode / EvictLeaf).
        Dictionary<QuadtreeBounds, QuadtreeNode> _spatialIndex = new Dictionary<QuadtreeBounds, QuadtreeNode>();

        void Start()
        {
            mainCamera = Camera.main;

            quads = Topology switch
            {
                WorldTopology.UVSphere => BaseMeshFactory.CreateUVSphere(SphereRadius, SphereCols, SphereRows),
                WorldTopology.Cylinder => BaseMeshFactory.CreateCylinder(CylRadius, CylHeight, CylCols),
                _                      => BaseMeshFactory.CreatePlane(PlaneSize),
            };

            for (int qi = 0; qi < quads.Length; qi++)
            {
                var full = new QuadtreeBounds { Min = Vector2.zero, Max = Vector2.one, QuadIndex = qi };
                var root = new QuadtreeNode   { Bounds = full, Depth = 0, Parent = null };
                roots.Add(root);
                Enqueue(root);
            }
        }

        void Update()
        {
            if (mainCamera == null) mainCamera = Camera.main;
            if (mainCamera == null) return;

            ProcessGenerationQueue();

            foreach (var root in roots)
                UpdateNode(root, mainCamera.transform.position);

            EnforceMemoryBudget();
        }

        // ── Generation queue ─────────────────────────────────────────────────

        void ProcessGenerationQueue()
        {
            int budget = Settings != null ? Settings.MaxUploadsPerFrame : 3;
            while (pendingGeneration.Count > 0 && budget-- > 0)
            {
                var node = pendingGeneration.Dequeue();
                SpawnChunk(node);
                if (node.Parent != null)
                    TryHideParent(node.Parent);
            }
        }

        void Enqueue(QuadtreeNode node)
        {
            node.State = NodeState.Generating;
            pendingGeneration.Enqueue(node);
        }

        void SpawnChunk(QuadtreeNode node)
        {
            int   res         = Settings != null ? Settings.ChunkResolution : 33;
            float heightScale = Settings != null ? Settings.HeightScale     : 200f;
            float baseFreq    = Settings != null ? Settings.BaseFrequency   : 0.005f;
            int   octaves     = Settings != null ? Settings.Octaves         : 4;

            var quad = quads[node.Bounds.QuadIndex];
            var hm   = HeightmapGenerator.Generate(res, node.Bounds.Min, node.Bounds.Max, quad, baseFreq, octaves);

            // Stitch first, then cache — children stitching to us must read the stitched values,
            // not the raw noise, so they produce heights consistent with this mesh's actual edges.
            ApplySeamStitching(hm, res, node); // modifies edge rows of hm in-place
            CacheEdgeHeights(node, hm, res);   // stores the stitched edges for fine neighbors to read

            var mesh = BuildMesh(hm, res, node, quad, heightScale);

            var go    = new GameObject($"Chunk_Q{node.Bounds.QuadIndex}_D{node.Depth}_{node.Bounds.Centre.x:F2}_{node.Bounds.Centre.y:F2}");
            var chunk = go.AddComponent<TerrainChunk>();
            if (ChunkMaterial != null) chunk.Initialize(ChunkMaterial);
            chunk.ApplyMesh(mesh);
            go.transform.position = Vector3.zero;

            // Children start hidden — TryHideParent activates all four atomically once ready,
            // preventing the partial-overlap window where parent + some children are both visible.
            if (node.Parent != null)
                go.SetActive(false);

            node.Chunk = chunk;
            RegisterNode(node);        // enter spatial index after edge data is set
            node.State = NodeState.Active;
        }

        // Once all four children are ready, swap parent off and children on in one frame.
        void TryHideParent(QuadtreeNode parent)
        {
            if (parent.Chunk == null || parent.Children == null) return;
            foreach (var c in parent.Children)
                if (c.Chunk == null) return;

            // Parent's region is now covered by its children.
            DeregisterNode(parent);
            parent.Chunk.gameObject.SetActive(false);
            foreach (var c in parent.Children)
                c.Chunk.gameObject.SetActive(true);
            // Children are already registered in SpawnChunk.
        }

        // ── LOD update ───────────────────────────────────────────────────────

        void UpdateNode(QuadtreeNode node, Vector3 camPos)
        {
            if (node.State == NodeState.Generating) return;

            float dist      = WorldDistanceToNode(node, camPos);
            float splitDist = GetLodDistance(node.Depth);
            int   maxDepth  = Settings != null ? Settings.MaxDepth         : DefaultMaxDepth;
            float hyster    = Settings != null ? Settings.HysteresisFactor : DefaultHysteresis;

            if (node.IsLeaf && node.State == NodeState.Active && node.Depth < maxDepth && dist < splitDist)
            {
                SubdivideNode(node);
            }
            else if (!node.IsLeaf && node.State == NodeState.Subdivided && dist > splitDist * hyster)
            {
                CollapseNode(node);
            }
            else if (!node.IsLeaf)
            {
                foreach (var c in node.Children)
                    UpdateNode(c, camPos);
            }
        }

        void SubdivideNode(QuadtreeNode node)
        {
            node.Subdivide();
            foreach (var c in node.Children)
                Enqueue(c);
        }

        void CollapseNode(QuadtreeNode node)
        {
            foreach (var c in node.Children)
                DisposeSubtree(c);

            if (node.Chunk != null)
            {
                RegisterNode(node);   // parent reclaims its region in the spatial index
                node.Chunk.gameObject.SetActive(true);
            }

            node.Collapse();
        }

        void DisposeSubtree(QuadtreeNode node)
        {
            if (node.Children != null)
                foreach (var c in node.Children)
                    DisposeSubtree(c);

            DeregisterNode(node);
            node.EdgeN = node.EdgeS = node.EdgeE = node.EdgeW = null;
            node.EdgeNormalN = node.EdgeNormalS = node.EdgeNormalE = node.EdgeNormalW = null;

            if (node.Chunk != null)
            {
                node.Chunk.Dispose();
                node.Chunk = null;
            }
        }

        // ── Spatial index ────────────────────────────────────────────────────

        void RegisterNode(QuadtreeNode node)   => _spatialIndex[node.Bounds] = node;
        void DeregisterNode(QuadtreeNode node) => _spatialIndex.Remove(node.Bounds);

        // Returns the registered node sharing the given edge of `node`, or null if the boundary
        // is cross-quad (or the quad edge with no neighbor). Walks coarser depths if no same-depth
        // neighbor is registered, so the returned node may be at a lower depth than `node`.
        QuadtreeNode FindNeighbor(QuadtreeNode node, EdgeDirection dir)
        {
            int   qi       = node.Bounds.QuadIndex;
            float nodeSize = node.Bounds.Size;
            float eps      = nodeSize * 0.001f; // small offset to push probe past the edge

            for (float trySize = nodeSize; trySize <= 1f + 1e-5f; trySize *= 2f)
            {
                // Probe point just outside our edge
                float probeU, probeV;
                switch (dir)
                {
                    case EdgeDirection.North: probeU = node.Bounds.Centre.x; probeV = node.Bounds.Max.y + eps; break;
                    case EdgeDirection.South: probeU = node.Bounds.Centre.x; probeV = node.Bounds.Min.y - eps; break;
                    case EdgeDirection.East:  probeU = node.Bounds.Max.x + eps; probeV = node.Bounds.Centre.y; break;
                    default:                  probeU = node.Bounds.Min.x - eps; probeV = node.Bounds.Centre.y; break;
                }

                if (probeU < -1e-4f || probeU > 1f + 1e-4f || probeV < -1e-4f || probeV > 1f + 1e-4f)
                    return null; // cross-quad boundary

                float snapU = Mathf.Floor(probeU / trySize) * trySize;
                float snapV = Mathf.Floor(probeV / trySize) * trySize;

                var candidate = new QuadtreeBounds
                {
                    Min      = new Vector2(snapU, snapV),
                    Max      = new Vector2(snapU + trySize, snapV + trySize),
                    QuadIndex = qi
                };

                // Guard: the candidate must be strictly on the other side of our edge,
                // not an ancestor whose bounds contain our own node.
                bool valid = dir switch
                {
                    EdgeDirection.North => candidate.Min.y >= node.Bounds.Max.y - 1e-4f,
                    EdgeDirection.South => candidate.Max.y <= node.Bounds.Min.y + 1e-4f,
                    EdgeDirection.East  => candidate.Min.x >= node.Bounds.Max.x - 1e-4f,
                    EdgeDirection.West  => candidate.Max.x <= node.Bounds.Min.x + 1e-4f,
                    _                   => false
                };
                if (!valid) continue;

                if (_spatialIndex.TryGetValue(candidate, out var neighbor))
                    return neighbor;
            }

            return null;
        }

        // ── Seam stitching ───────────────────────────────────────────────────

        // Extracts the four border rows/columns from hm and stores them on the node
        // so that finer neighbors can read them when stitching their own edges.
        void CacheEdgeHeights(QuadtreeNode node, float[,] hm, int res)
        {
            node.EdgeN = new float[res];
            node.EdgeS = new float[res];
            node.EdgeE = new float[res];
            node.EdgeW = new float[res];
            for (int i = 0; i < res; i++)
            {
                node.EdgeN[i] = hm[i, res - 1];
                node.EdgeS[i] = hm[i, 0];
                node.EdgeE[i] = hm[res - 1, i];
                node.EdgeW[i] = hm[0, i];
            }
        }

        void ApplySeamStitching(float[,] hm, int res, QuadtreeNode node)
        {
            TryStitchEdge(hm, res, node, EdgeDirection.North);
            TryStitchEdge(hm, res, node, EdgeDirection.South);
            TryStitchEdge(hm, res, node, EdgeDirection.East);
            TryStitchEdge(hm, res, node, EdgeDirection.West);
        }

        void TryStitchEdge(float[,] hm, int res, QuadtreeNode node, EdgeDirection dir)
        {
            var neighbor = FindNeighbor(node, dir);
            // Only stitch when the neighbor is COARSER (lower depth = larger region).
            // Same-depth neighbors have matching vertex counts; finer neighbors stitch to us.
            if (neighbor == null || neighbor.Depth >= node.Depth) return;

            float[] coarseEdge = GetOpposingEdge(neighbor, dir);
            if (coarseEdge == null || coarseEdge.Length == 0) return;

            QuadtreeBounds myBounds    = node.Bounds;
            QuadtreeBounds coarseBounds = neighbor.Bounds;

            for (int i = 0; i < res; i++)
            {
                float frac = i / (float)(res - 1);

                // Global UV position of this vertex along the edge (in the axis parallel to the edge)
                float uvPos, coarseMin, coarseSize;
                if (dir == EdgeDirection.North || dir == EdgeDirection.South)
                {
                    uvPos      = Mathf.Lerp(myBounds.Min.x, myBounds.Max.x, frac);
                    coarseMin  = coarseBounds.Min.x;
                    coarseSize = coarseBounds.Size;
                }
                else
                {
                    uvPos      = Mathf.Lerp(myBounds.Min.y, myBounds.Max.y, frac);
                    coarseMin  = coarseBounds.Min.y;
                    coarseSize = coarseBounds.Size;
                }

                // Bilinear interpolation into the coarse edge sample array
                float t           = (uvPos - coarseMin) / coarseSize;
                float coarseFloat = t * (res - 1);
                int   lo          = Mathf.FloorToInt(coarseFloat);
                int   hi          = Mathf.Min(lo + 1, res - 1);
                float height      = Mathf.Lerp(coarseEdge[lo], coarseEdge[hi], coarseFloat - lo);

                switch (dir)
                {
                    case EdgeDirection.North: hm[i, res - 1] = height; break;
                    case EdgeDirection.South: hm[i, 0]       = height; break;
                    case EdgeDirection.East:  hm[res - 1, i] = height; break;
                    case EdgeDirection.West:  hm[0, i]       = height; break;
                }
            }
        }

        // Opposing edge: our North shares the neighbor's South, etc.
        float[] GetOpposingEdge(QuadtreeNode neighbor, EdgeDirection myDir) => myDir switch
        {
            EdgeDirection.North => neighbor.EdgeS,
            EdgeDirection.South => neighbor.EdgeN,
            EdgeDirection.East  => neighbor.EdgeW,
            EdgeDirection.West  => neighbor.EdgeE,
            _                   => null
        };

        Vector3[] GetOpposingEdgeNormals(QuadtreeNode neighbor, EdgeDirection myDir) => myDir switch
        {
            EdgeDirection.North => neighbor.EdgeNormalS,
            EdgeDirection.South => neighbor.EdgeNormalN,
            EdgeDirection.East  => neighbor.EdgeNormalW,
            EdgeDirection.West  => neighbor.EdgeNormalE,
            _                   => null
        };

        void CacheEdgeNormals(QuadtreeNode node, Vector3[] normals, int res)
        {
            node.EdgeNormalN = new Vector3[res];
            node.EdgeNormalS = new Vector3[res];
            node.EdgeNormalE = new Vector3[res];
            node.EdgeNormalW = new Vector3[res];
            for (int i = 0; i < res; i++)
            {
                node.EdgeNormalN[i] = normals[(res - 1) * res + i];
                node.EdgeNormalS[i] = normals[i];
                node.EdgeNormalE[i] = normals[i * res + (res - 1)];
                node.EdgeNormalW[i] = normals[i * res];
            }
        }

        void ApplyEdgeNormalStitching(Vector3[] normals, int res, QuadtreeNode node)
        {
            TryStitchEdgeNormals(normals, res, node, EdgeDirection.North);
            TryStitchEdgeNormals(normals, res, node, EdgeDirection.South);
            TryStitchEdgeNormals(normals, res, node, EdgeDirection.East);
            TryStitchEdgeNormals(normals, res, node, EdgeDirection.West);
        }

        void TryStitchEdgeNormals(Vector3[] normals, int res, QuadtreeNode node, EdgeDirection dir)
        {
            var neighbor = FindNeighbor(node, dir);
            if (neighbor == null || neighbor.Depth >= node.Depth) return;

            Vector3[] coarseEdge = GetOpposingEdgeNormals(neighbor, dir);
            if (coarseEdge == null || coarseEdge.Length == 0) return;

            QuadtreeBounds myBounds     = node.Bounds;
            QuadtreeBounds coarseBounds = neighbor.Bounds;

            for (int i = 0; i < res; i++)
            {
                float frac = i / (float)(res - 1);

                float uvPos, coarseMin, coarseSize;
                if (dir == EdgeDirection.North || dir == EdgeDirection.South)
                {
                    uvPos      = Mathf.Lerp(myBounds.Min.x, myBounds.Max.x, frac);
                    coarseMin  = coarseBounds.Min.x;
                    coarseSize = coarseBounds.Size;
                }
                else
                {
                    uvPos      = Mathf.Lerp(myBounds.Min.y, myBounds.Max.y, frac);
                    coarseMin  = coarseBounds.Min.y;
                    coarseSize = coarseBounds.Size;
                }

                float t           = (uvPos - coarseMin) / coarseSize;
                float coarseFloat = t * (res - 1);
                int   lo          = Mathf.FloorToInt(coarseFloat);
                int   hi          = Mathf.Min(lo + 1, res - 1);
                float blend       = coarseFloat - lo;
                Vector3 n         = Vector3.Slerp(coarseEdge[lo], coarseEdge[hi], blend);
                if (n.sqrMagnitude > 1e-6f) n.Normalize();

                int vi = dir switch
                {
                    EdgeDirection.North => (res - 1) * res + i,
                    EdgeDirection.South => i,
                    EdgeDirection.East  => i * res + (res - 1),
                    EdgeDirection.West  => i * res,
                    _                   => 0
                };
                normals[vi] = n;
            }
        }

        // ── Mesh building (CPU, synchronous — replaced by Burst job in Phase 3) ──

        Mesh BuildMesh(float[,] hm, int res, QuadtreeNode node, BaseMeshQuad quad, float heightScale)
        {
            var bounds = node.Bounds;
            float skirtDepth = Settings != null ? Settings.SkirtDepth : 10f;

            int vertCount  = res * res;
            int skirtVerts = 4 * res;
            int totalVerts = vertCount + skirtVerts;

            int mainTriIdx  = (res - 1) * (res - 1) * 6;
            int skirtTriIdx = 4 * (res - 1) * 6;
            int totalIdx    = mainTriIdx + skirtTriIdx;

            Vector3[] verts   = new Vector3[totalVerts];
            Vector3[] normals = new Vector3[totalVerts];
            Vector2[] uvs     = new Vector2[totalVerts];
            int[]     tris    = new int[totalIdx];

            // Main mesh vertices
            for (int y = 0; y < res; y++)
            {
                float v = Mathf.Lerp(bounds.Min.y, bounds.Max.y, y / (float)(res - 1));
                for (int x = 0; x < res; x++)
                {
                    float   u     = Mathf.Lerp(bounds.Min.x, bounds.Max.x, x / (float)(res - 1));
                    int     i     = y * res + x;
                    double3 world = CoordinateTransform.ToWorldPosition(u, v, hm[x, y] * heightScale, quad);
                    double3 local = world - WorldOriginSystem.WorldOrigin;
                    verts[i] = new Vector3((float)local.x, (float)local.y, (float)local.z);
                    uvs[i]   = new Vector2(u, v);
                }
            }

            // Normals via central differences
            for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
            {
                int     i = y * res + x;
                Vector3 L = verts[y * res + Mathf.Max(0, x - 1)];
                Vector3 R = verts[y * res + Mathf.Min(res - 1, x + 1)];
                Vector3 D = verts[Mathf.Max(0, y - 1) * res + x];
                Vector3 U = verts[Mathf.Min(res - 1, y + 1) * res + x];
                Vector3 n = Vector3.Cross(U - D, R - L);
                normals[i] = n.sqrMagnitude > 1e-6f ? n.normalized : Vector3.up;
            }

            // Stitch edge normals from coarser neighbors before caching and before skirts copy them.
            ApplyEdgeNormalStitching(normals, res, node);
            CacheEdgeNormals(node, normals, res);

            // Main mesh triangles
            int ti = 0;
            for (int y = 0; y < res - 1; y++)
            for (int x = 0; x < res - 1; x++)
            {
                int i00 = y * res + x, i10 = i00 + 1, i01 = i00 + res, i11 = i01 + 1;
                tris[ti++] = i00; tris[ti++] = i11; tris[ti++] = i10;
                tris[ti++] = i00; tris[ti++] = i01; tris[ti++] = i11;
            }

            // Skirt vertices — one row per edge, offset along the smooth surface normal direction
            // (not the terrain-slope normal, which has horizontal components on sloped terrain).
            // Layout: [vertCount..+res-1] = North, [+res..+2res-1] = South,
            //         [+2res..+3res-1] = East,  [+3res..+4res-1] = West.
            int sN = vertCount,         sS = vertCount + res,
                sE = vertCount + 2*res, sW = vertCount + 3*res;

            for (int x = 0; x < res; x++) // North edge: y = res-1
            {
                int vi = (res - 1) * res + x;
                verts  [sN + x] = verts[vi] + SkirtDir(verts[vi], quad) * skirtDepth;
                normals[sN + x] = normals[vi];
                uvs    [sN + x] = uvs[vi];
            }
            for (int x = 0; x < res; x++) // South edge: y = 0
            {
                int vi = x;
                verts  [sS + x] = verts[vi] + SkirtDir(verts[vi], quad) * skirtDepth;
                normals[sS + x] = normals[vi];
                uvs    [sS + x] = uvs[vi];
            }
            for (int y = 0; y < res; y++) // East edge: x = res-1
            {
                int vi = y * res + (res - 1);
                verts  [sE + y] = verts[vi] + SkirtDir(verts[vi], quad) * skirtDepth;
                normals[sE + y] = normals[vi];
                uvs    [sE + y] = uvs[vi];
            }
            for (int y = 0; y < res; y++) // West edge: x = 0
            {
                int vi = y * res;
                verts  [sW + y] = verts[vi] + SkirtDir(verts[vi], quad) * skirtDepth;
                normals[sW + y] = normals[vi];
                uvs    [sW + y] = uvs[vi];
            }

            // Skirt triangles — winding: top[i], top[i+1], bot[i+1]; top[i], bot[i+1], bot[i]
            // (CW when viewed from outside on N/S/E edges; W may need material Cull Off for full coverage)
            for (int i = 0; i < res - 1; i++) // North
            {
                int t0 = (res - 1) * res + i, t1 = t0 + 1, b0 = sN + i, b1 = sN + i + 1;
                tris[ti++] = t0; tris[ti++] = t1; tris[ti++] = b1;
                tris[ti++] = t0; tris[ti++] = b1; tris[ti++] = b0;
            }
            for (int i = 0; i < res - 1; i++) // South
            {
                int t0 = i, t1 = i + 1, b0 = sS + i, b1 = sS + i + 1;
                tris[ti++] = t0; tris[ti++] = t1; tris[ti++] = b1;
                tris[ti++] = t0; tris[ti++] = b1; tris[ti++] = b0;
            }
            for (int i = 0; i < res - 1; i++) // East
            {
                int t0 = i * res + (res - 1), t1 = (i + 1) * res + (res - 1), b0 = sE + i, b1 = sE + i + 1;
                tris[ti++] = t0; tris[ti++] = t1; tris[ti++] = b1;
                tris[ti++] = t0; tris[ti++] = b1; tris[ti++] = b0;
            }
            for (int i = 0; i < res - 1; i++) // West (flipped winding — faces outward toward -U)
            {
                int t0 = i * res, t1 = (i + 1) * res, b0 = sW + i, b1 = sW + i + 1;
                tris[ti++] = t0; tris[ti++] = b0; tris[ti++] = t1;
                tris[ti++] = t1; tris[ti++] = b0; tris[ti++] = b1;
            }

            var mesh = new Mesh();
            mesh.indexFormat = totalVerts > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices  = verts;
            mesh.normals   = normals;
            mesh.uv        = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        // ── Memory budget ────────────────────────────────────────────────────

        // Collapses the most-distant chunk groups when active count exceeds MaxActiveChunks.
        // A "collapsible group" is a set of 4 siblings that are all active leaves — collapsing
        // them frees 4 chunks and re-activates their (hidden) parent.
        void EnforceMemoryBudget()
        {
            int maxChunks = Settings != null ? Settings.MaxActiveChunks : 0;
            if (maxChunks <= 0 || TerrainChunk.AllActive.Count <= maxChunks) return;

            var collapsible = new List<QuadtreeNode>();
            foreach (var root in roots)
                CollectCollapsibleParents(root, collapsible);

            Vector3 camPos = mainCamera != null ? mainCamera.transform.position : Vector3.zero;
            collapsible.Sort((a, b) =>
                WorldDistanceToNode(b, camPos).CompareTo(WorldDistanceToNode(a, camPos)));

            foreach (var parent in collapsible)
            {
                if (TerrainChunk.AllActive.Count <= maxChunks) break;
                CollapseNode(parent);
            }
        }

        // Recursively collects parent nodes whose four children are ALL active leaves.
        // These are safe to collapse without leaving any uncovered region.
        void CollectCollapsibleParents(QuadtreeNode node, List<QuadtreeNode> result)
        {
            if (node.Children == null) return;

            bool allActiveLeaves = true;
            foreach (var c in node.Children)
            {
                if (c.Children != null || c.State != NodeState.Active || c.Chunk == null)
                { allActiveLeaves = false; break; }
            }

            if (allActiveLeaves && node.Chunk != null)
                result.Add(node);
            else
                foreach (var c in node.Children)
                    CollectCollapsibleParents(c, result);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        // Returns the direction skirt vertices should be displaced from an edge vertex,
        // based on the underlying surface topology. Using the smooth surface normal (not the
        // terrain-slope normal) avoids horizontal displacement artifacts on sloped terrain.
        static Vector3 SkirtDir(Vector3 vertRenderPos, BaseMeshQuad quad)
        {
            switch (quad.Type)
            {
                case SurfaceType.Sphere:
                {
                    // Sphere is centered at world origin; in render space the center is -WorldOrigin.
                    // Skirt goes toward sphere center (inward).
                    var orig   = WorldOriginSystem.WorldOrigin;
                    var center = new Vector3(-(float)orig.x, -(float)orig.y, -(float)orig.z);
                    return (center - vertRenderPos).normalized;
                }
                case SurfaceType.Cylinder:
                {
                    // Cylinder axis is world Z. Nearest axis point in render space = (-WorldOrigin.xy, vert.z).
                    // Skirt goes toward the axis — project into XY plane and normalize.
                    var orig  = WorldOriginSystem.WorldOrigin;
                    var toAxis = new Vector3(-(float)orig.x - vertRenderPos.x,
                                             -(float)orig.y - vertRenderPos.y,
                                             0f);
                    return toAxis.normalized;
                }
                default: // Plane
                    return Vector3.down;
            }
        }

        float GetLodDistance(int depth)
        {
            if (Settings != null && Settings.LodDistances != null && depth < Settings.LodDistances.Length)
                return Settings.LodDistances[depth];
            return DefaultBaseSplitDistance / (1 << depth);
        }

        float WorldDistanceToNode(QuadtreeNode node, Vector3 camPos)
        {
            var     centre = node.Bounds.Centre;
            double3 world  = CoordinateTransform.ToWorldPosition(centre.x, centre.y, 0.0, quads[node.Bounds.QuadIndex]);
            double3 local  = world - WorldOriginSystem.WorldOrigin;
            Vector3 w      = new Vector3((float)local.x, (float)local.y, (float)local.z);
            return Vector3.Distance(w, camPos);
        }

        // ── Debug visualisation ───────────────────────────────────────────────
        //
        // Draws node boundaries as line strips sampled along the curved quad surface.
        // Green = Active leaf, Yellow = Generating, Blue = Subdivided.

        void OnDrawGizmos()
        {
            if (!Application.isPlaying || quads == null) return;
            foreach (var root in roots)
                DrawNodeGizmo(root);
        }

        void DrawNodeGizmo(QuadtreeNode node)
        {
            switch (node.State)
            {
                case NodeState.Active:     Gizmos.color = new Color(0.2f, 1f,   0.2f, 0.8f); break;
                case NodeState.Generating: Gizmos.color = new Color(1f,   1f,   0f,   0.8f); break;
                case NodeState.Subdivided: Gizmos.color = new Color(0.4f, 0.4f, 1f,   0.3f); break;
                default:                   Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.3f); break;
            }

            var  b    = node.Bounds;
            var  quad = quads[b.QuadIndex];
            DrawQuadEdge(quad, b.Min.x, b.Min.y, b.Max.x, b.Min.y); // bottom
            DrawQuadEdge(quad, b.Min.x, b.Max.y, b.Max.x, b.Max.y); // top
            DrawQuadEdge(quad, b.Min.x, b.Min.y, b.Min.x, b.Max.y); // left
            DrawQuadEdge(quad, b.Max.x, b.Min.y, b.Max.x, b.Max.y); // right

            if (node.Children != null)
                foreach (var c in node.Children)
                    DrawNodeGizmo(c);
        }

        void DrawQuadEdge(BaseMeshQuad quad, float u0, float v0, float u1, float v1, int steps = 8)
        {
            var     orig = WorldOriginSystem.WorldOrigin;
            Vector3 prev = ToRenderPos(CoordinateTransform.ToWorldPosition(u0, v0, 0.0, quad), orig);
            for (int s = 1; s <= steps; s++)
            {
                float   t    = s / (float)steps;
                Vector3 next = ToRenderPos(CoordinateTransform.ToWorldPosition(
                    Mathf.Lerp(u0, u1, t), Mathf.Lerp(v0, v1, t), 0.0, quad), orig);
                Gizmos.DrawLine(prev, next);
                prev = next;
            }
        }

        static Vector3 ToRenderPos(double3 world, double3 origin)
        {
            double3 local = world - origin;
            return new Vector3((float)local.x, (float)local.y, (float)local.z);
        }
    }
}
