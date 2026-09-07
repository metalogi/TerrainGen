using UnityEngine;
using Unity.Mathematics;
using Sonoma.Core.Surface;

/// Draws the surface layer as gizmos, in Edit mode, without entering Play.
///
/// Deliberately independent of TerrainRoot and the generation pipeline: drop it on
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
    [Tooltip("Upper bound. Reduced automatically on large root grids to stay within MaxGizmoNodes.")]
    [Range(0, 4)] public int DrawDepth = 2;
    [Tooltip("Upper bound. Reduced automatically at high DrawDepth to stay within MaxGizmoLines.")]
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

    // OnDrawGizmos runs on every SceneView repaint, so the cost has to be bounded by
    // construction rather than by the user happening to pick small numbers. Unbounded, a
    // DrawDepth of 5 with EdgeSegments at 16 issued 6 x 32x32 nodes x 4 arcs x 16 segments
    // = ~393k Gizmos.DrawLine calls per repaint, which locks the Editor hard enough that
    // the inspector needed to lower the value stops redrawing.
    //
    // Two knobs reach that blowup -- DrawDepth and a large Cols x Rows root grid -- so the
    // budget is enforced on the result rather than on either slider: the effective depth is
    // clamped to MaxGizmoNodes, then the segment count is scaled to MaxGizmoLines. Turning
    // either slider up past the budget costs detail, not responsiveness.
    const int MaxGizmoNodes = 4096;
    const int MaxGizmoLines = 24000;

    static readonly Edge[]  ProbeEdges   = { Edge.South, Edge.East, Edge.North, Edge.West };
    static readonly Color[] ProbeColours = { Color.red,  Color.green, Color.cyan, Color.yellow };

    // BuildRoots allocates, and this runs per repaint, so rebuild only when an input moved.
    [System.NonSerialized] RootQuad[]  _roots;
    [System.NonSerialized] SurfaceType _cachedType;
    [System.NonSerialized] float       _cachedRadius, _cachedTileSize, _cachedLength;
    [System.NonSerialized] int         _cachedCols, _cachedRows;
    [System.NonSerialized] bool        _cachedTangent;

    RootQuad[] Roots(in SurfaceDef s)
    {
        if (_roots != null
            && _cachedType    == Type     && _cachedRadius  == Radius
            && _cachedTileSize == TileSize && _cachedLength  == CylinderLength
            && _cachedCols    == Cols     && _cachedRows    == Rows
            && _cachedTangent == TangentAdjust)
            return _roots;

        _roots          = SurfaceMath.BuildRoots(s);
        _cachedType     = Type;
        _cachedRadius   = Radius;
        _cachedTileSize = TileSize;
        _cachedLength   = CylinderLength;
        _cachedCols     = Cols;
        _cachedRows     = Rows;
        _cachedTangent  = TangentAdjust;
        return _roots;
    }

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

        var roots = Roots(s);

        // Node count first, then spend whatever is left of the line budget on curvature.
        int quads = HighlightQuad >= 0 ? 1 : roots.Length;
        int depth = DrawDepth;
        while (depth > 0 && (long)quads << (2 * depth) > MaxGizmoNodes) depth--;

        int  span     = 1 << depth;
        long nodes    = (long)quads * span * span;
        int  segments = (int)Mathf.Clamp(MaxGizmoLines / Mathf.Max(1f, nodes * 4f), 2f, EdgeSegments);

        for (int quad = 0; quad < roots.Length; quad++)
        {
            if (HighlightQuad >= 0 && quad != HighlightQuad) continue;

            Gizmos.color = QuadColour(quad, roots.Length);

            for (int x = 0; x < span; x++)
            for (int y = 0; y < span; y++)
            {
                var n = new NodeId(quad, depth, x, y);
                DrawNode(s, roots[quad], n, segments);

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

        if (ShowNeighbours) DrawProbe(s, roots, depth, span, segments);
    }

    void DrawProbe(in SurfaceDef s, RootQuad[] roots, int depth, int span, int segments)
    {
        if (ProbeQuad < 0 || ProbeQuad >= roots.Length) return;
        if (ProbeX < 0 || ProbeX >= span || ProbeY < 0 || ProbeY >= span) return;

        var probe = new NodeId(ProbeQuad, depth, ProbeX, ProbeY);

        Gizmos.color = Color.white;
        DrawNode(s, roots[ProbeQuad], probe, segments, filled: true);

        // One colour per edge so it is obvious which way each link went.
        for (int i = 0; i < ProbeEdges.Length; i++)
        {
            var hop = SurfaceMath.Neighbour(s, probe, ProbeEdges[i]);
            if (!hop.Exists) continue;

            Gizmos.color = ProbeColours[i];
            DrawNode(s, roots[hop.Node.Quad], hop.Node, segments, filled: true);
        }
    }

    void DrawNode(in SurfaceDef s, in RootQuad q, NodeId n, int segments, bool filled = false)
    {
        double u0 = n.UMin, u1 = n.UMax, v0 = n.VMin, v1 = n.VMax;

        DrawArc(s, q, u0, v0, u1, v0, segments);   // south
        DrawArc(s, q, u1, v0, u1, v1, segments);   // east
        DrawArc(s, q, u1, v1, u0, v1, segments);   // north
        DrawArc(s, q, u0, v1, u0, v0, segments);   // west

        if (filled)   // cross through the middle, so the highlighted node reads clearly
        {
            DrawArc(s, q, u0, v0, u1, v1, segments);
            DrawArc(s, q, u1, v0, u0, v1, segments);
        }
    }

    // Samples along the parameter so curvature is visible rather than chorded.
    void DrawArc(in SurfaceDef s, in RootQuad q, double u0, double v0, double u1, double v1,
                 int segments)
    {
        int steps = Mathf.Max(2, segments);
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
