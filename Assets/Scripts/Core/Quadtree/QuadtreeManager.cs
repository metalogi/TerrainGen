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
            var mesh = BuildMesh(hm, res, node.Bounds, quad, heightScale);

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
            node.State = NodeState.Active;
        }

        // Once all four children are ready, swap parent off and children on in one frame.
        void TryHideParent(QuadtreeNode parent)
        {
            if (parent.Chunk == null || parent.Children == null) return;
            foreach (var c in parent.Children)
                if (c.Chunk == null) return;

            parent.Chunk.gameObject.SetActive(false);
            foreach (var c in parent.Children)
                c.Chunk.gameObject.SetActive(true);
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
                node.Chunk.gameObject.SetActive(true);

            node.Collapse();
        }

        void DisposeSubtree(QuadtreeNode node)
        {
            if (node.Children != null)
                foreach (var c in node.Children)
                    DisposeSubtree(c);

            if (node.Chunk != null)
            {
                node.Chunk.Dispose();
                node.Chunk = null;
            }
        }

        // ── Mesh building (CPU, synchronous — replaced by Burst job in Phase 3) ──

        Mesh BuildMesh(float[,] hm, int res, QuadtreeBounds bounds, BaseMeshQuad quad, float heightScale)
        {
            int       vertCount = res * res;
            Vector3[] verts     = new Vector3[vertCount];
            Vector3[] normals   = new Vector3[vertCount];
            Vector2[] uvs       = new Vector2[vertCount];
            int[]     tris      = new int[(res - 1) * (res - 1) * 6];

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

            int ti = 0;
            for (int y = 0; y < res - 1; y++)
            for (int x = 0; x < res - 1; x++)
            {
                int i00 = y * res + x, i10 = i00 + 1, i01 = i00 + res, i11 = i01 + 1;
                tris[ti++] = i00; tris[ti++] = i11; tris[ti++] = i10;
                tris[ti++] = i00; tris[ti++] = i01; tris[ti++] = i11;
            }

            var mesh = new Mesh();
            mesh.indexFormat = vertCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices  = verts;
            mesh.normals   = normals;
            mesh.uv        = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

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

        // Samples the quad surface at (u0,v0)..(u1,v1) and draws line segments.
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
