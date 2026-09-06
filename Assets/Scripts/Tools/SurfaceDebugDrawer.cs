using UnityEngine;
using Unity.Mathematics;
using Sonoma.Core.Surface;

/// Draws the M1 surface layer as gizmos, in Edit mode, without entering Play.
///
/// Deliberately independent of QuadtreeManager and the rest of the prototype: drop it on
/// an empty GameObject in an empty scene to inspect root quads, node grids, normals and
/// neighbour links. Dragging the neighbour probe across a cube-face seam is the fastest
/// way to sanity-check the adjacency table by eye.
public class SurfaceDebugDrawer : MonoBehaviour
{
    [Header("Topology")]
    public SurfaceType Type = SurfaceType.CubeSphere;
    public float  Radius        = 500f;
    public float  TileSize      = 1000f;
    public float  CylinderLength = 2000f;
    public int    Cols          = 8;
    public int    Rows          = 4;
    public bool   TangentAdjust = true;

    [Header("Drawing")]
    [Range(0, 5)] public int DrawDepth = 2;
    [Range(2, 16)] public int EdgeSegments = 8;
    public bool  DrawNormals   = false;
    public float NormalLength  = 40f;
    [Tooltip("-1 draws every root quad; otherwise only this one.")]
    public int   HighlightQuad = -1;

    [Header("Neighbour Probe")]
    public bool ShowNeighbours = false;
    public int  ProbeQuad = 0;
    public int  ProbeX    = 0;
    public int  ProbeY    = 0;

    SurfaceDef BuildDef()
    {
        switch (Type)
        {
            case SurfaceType.CubeSphere: return SurfaceDef.CubeSphere(Radius, TangentAdjust);
            case SurfaceType.Cylinder:   return SurfaceDef.Cylinder(Radius, CylinderLength, Cols, Rows);
            default:                     return SurfaceDef.PlaneGrid(TileSize, Cols, Rows);
        }
    }

    void OnDrawGizmos()
    {
        // Guard against half-typed Inspector values.
        if (Cols < 1 || Rows < 1 || Radius <= 0f || TileSize <= 0f) return;

        var s = BuildDef();
        if (s.QuadCount < 1) return;

        var roots = SurfaceMath.BuildRoots(s);
        int span  = 1 << DrawDepth;

        for (int quad = 0; quad < roots.Length; quad++)
        {
            if (HighlightQuad >= 0 && quad != HighlightQuad) continue;

            Gizmos.color = QuadColour(quad, roots.Length);

            for (int x = 0; x < span; x++)
            for (int y = 0; y < span; y++)
            {
                var n = new NodeId(quad, DrawDepth, x, y);
                DrawNode(s, roots[quad], n);

                if (DrawNormals)
                {
                    SurfaceMath.SurfaceFrame(s, roots[quad],
                        (n.UMin + n.UMax) * 0.5, (n.VMin + n.VMax) * 0.5,
                        out double3 p, out float3 nrm);
                    Vector3 a = ToVec(p);
                    Gizmos.DrawLine(a, a + new Vector3(nrm.x, nrm.y, nrm.z) * NormalLength);
                }
            }
        }

        if (ShowNeighbours) DrawProbe(s, roots, span);
    }

    void DrawProbe(in SurfaceDef s, RootQuad[] roots, int span)
    {
        if (ProbeQuad < 0 || ProbeQuad >= roots.Length) return;
        if (ProbeX < 0 || ProbeX >= span || ProbeY < 0 || ProbeY >= span) return;

        var probe = new NodeId(ProbeQuad, DrawDepth, ProbeX, ProbeY);

        Gizmos.color = Color.white;
        DrawNode(s, roots[ProbeQuad], probe, filled: true);

        // One colour per edge so it is obvious which way each link went.
        var edges   = new[] { Edge.South, Edge.East, Edge.North, Edge.West };
        var colours = new[] { Color.red,  Color.green, Color.cyan, Color.yellow };

        for (int i = 0; i < 4; i++)
        {
            var hop = SurfaceMath.Neighbour(s, probe, edges[i]);
            if (!hop.Exists) continue;

            Gizmos.color = colours[i];
            DrawNode(s, roots[hop.Node.Quad], hop.Node, filled: true);
        }
    }

    void DrawNode(in SurfaceDef s, in RootQuad q, NodeId n, bool filled = false)
    {
        double u0 = n.UMin, u1 = n.UMax, v0 = n.VMin, v1 = n.VMax;

        DrawArc(s, q, u0, v0, u1, v0);   // south
        DrawArc(s, q, u1, v0, u1, v1);   // east
        DrawArc(s, q, u1, v1, u0, v1);   // north
        DrawArc(s, q, u0, v1, u0, v0);   // west

        if (filled)   // cross through the middle, so the highlighted node reads clearly
        {
            DrawArc(s, q, u0, v0, u1, v1);
            DrawArc(s, q, u1, v0, u0, v1);
        }
    }

    // Samples along the parameter so curvature is visible rather than chorded.
    void DrawArc(in SurfaceDef s, in RootQuad q, double u0, double v0, double u1, double v1)
    {
        int steps = Mathf.Max(2, EdgeSegments);
        Vector3 prev = ToVec(SurfaceMath.SurfacePoint(s, q, u0, v0));

        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps;
            Vector3 next = ToVec(SurfaceMath.SurfacePoint(s, q,
                u0 + (u1 - u0) * t, v0 + (v1 - v0) * t));
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }

    static Vector3 ToVec(double3 p) => new Vector3((float)p.x, (float)p.y, (float)p.z);

    static Color QuadColour(int quad, int count)
    {
        float h = count <= 1 ? 0f : quad / (float)count;
        return Color.HSVToRGB(h, 0.65f, 1f);
    }
}
