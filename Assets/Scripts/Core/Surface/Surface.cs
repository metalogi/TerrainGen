using Unity.Mathematics;

namespace Sonoma.Core.Surface
{
    // Topology layer: maps (root quad, u, v) to world-space surface points, and answers
    // neighbour queries on integer node addresses.
    //
    // Dispatch is a switch on SurfaceType, never an interface or virtual call, because
    // Burst cannot devirtualise those. For the same reason the face basis and the cube
    // adjacency table are switch expressions rather than static arrays: Burst forbids
    // reading managed static arrays from a job, and M2 calls these from jobs.
    //
    // Positions are double3 (planetary scale); normals are float3 (direction only).
    // Height is added along the normal, matching CoordinateTransform.GetBaseSurface.
    public static class Surface
    {
        const double QuarterPi = 0.7853981633974483096;  // pi/4
        const double TwoPi     = 6.2831853071795864769;

        // ── Root construction ────────────────────────────────────────────────

        public static RootQuad[] BuildRoots(in SurfaceDef s)
        {
            switch (s.Type)
            {
                case SurfaceType.CubeSphere:
                {
                    var roots = new RootQuad[6];
                    for (int f = 0; f < 6; f++) roots[f] = new RootQuad { Face = f };
                    return roots;
                }

                case SurfaceType.Cylinder:
                {
                    var roots = new RootQuad[s.Cols * s.Rows];
                    double dAngle = TwoPi / s.Cols;
                    double dZ     = s.Length / s.Rows;
                    for (int col = 0; col < s.Cols; col++)
                    for (int row = 0; row < s.Rows; row++)
                    {
                        roots[col * s.Rows + row] = new RootQuad
                        {
                            Angle0 = col * dAngle, Angle1 = (col + 1) * dAngle,
                            Z0     = row * dZ,     Z1     = (row + 1) * dZ,
                        };
                    }
                    return roots;
                }

                default: // PlaneGrid — fixed grid centred on the origin in XZ.
                {
                    var roots = new RootQuad[s.Cols * s.Rows];
                    double halfX = s.Cols * s.TileSize * 0.5;
                    double halfZ = s.Rows * s.TileSize * 0.5;
                    for (int col = 0; col < s.Cols; col++)
                    for (int row = 0; row < s.Rows; row++)
                    {
                        roots[col * s.Rows + row] = new RootQuad
                        {
                            Origin = new double3(col * s.TileSize - halfX, 0.0,
                                                 row * s.TileSize - halfZ),
                        };
                    }
                    return roots;
                }
            }
        }

        // ── Surface evaluation ───────────────────────────────────────────────

        public static void SurfaceFrame(in SurfaceDef s, in RootQuad q, double u, double v,
                                        out double3 point, out float3 normal)
        {
            switch (s.Type)
            {
                case SurfaceType.CubeSphere: CubeSphereFrame(s, q.Face, u, v, out point, out normal); return;
                case SurfaceType.Cylinder:   CylinderFrame  (s, q,      u, v, out point, out normal); return;
                default:                     PlaneFrame     (s, q,      u, v, out point, out normal); return;
            }
        }

        public static double3 SurfacePoint(in SurfaceDef s, in RootQuad q, double u, double v)
        {
            SurfaceFrame(s, q, u, v, out double3 p, out _);
            return p;
        }

        public static float3 SurfaceNormal(in SurfaceDef s, in RootQuad q, double u, double v)
        {
            SurfaceFrame(s, q, u, v, out _, out float3 n);
            return n;
        }

        static void CubeSphereFrame(in SurfaceDef s, int face, double u, double v,
                                    out double3 point, out float3 normal)
        {
            double a = 2.0 * u - 1.0;
            double b = 2.0 * v - 1.0;

            // tan(+-pi/4) == +-1, so corners stay exactly on cube corners and face
            // boundaries are untouched; only the interior distribution changes.
            if (s.TangentAdjust)
            {
                a = math.tan(a * QuarterPi);
                b = math.tan(b * QuarterPi);
            }

            double3 d = FaceCentre(face) + a * FaceRight(face) + b * FaceUp(face);
            d = d / math.length(d);
            point  = s.Radius * d;
            normal = (float3)d;
        }

        static void CylinderFrame(in SurfaceDef s, in RootQuad q, double u, double v,
                                  out double3 point, out float3 normal)
        {
            double angle  = q.Angle0 + (q.Angle1 - q.Angle0) * u;
            double z      = q.Z0     + (q.Z1     - q.Z0)     * v;
            double3 radial = new double3(math.cos(angle), math.sin(angle), 0.0);
            point  = s.Radius * radial + new double3(0.0, 0.0, z);
            normal = -(float3)radial;   // inward: terrain lines the interior wall
        }

        static void PlaneFrame(in SurfaceDef s, in RootQuad q, double u, double v,
                               out double3 point, out float3 normal)
        {
            point  = q.Origin + new double3(u * s.TileSize, 0.0, v * s.TileSize);
            normal = new float3(0f, 1f, 0f);
        }

        // Longer of the two diagonals, in world units. Topology-agnostic and correct
        // under the tangent adjustment, which an analytic per-topology formula is not.
        public static double NodeWorldSize(in SurfaceDef s, in RootQuad q, NodeId n)
        {
            double3 p00 = SurfacePoint(s, q, n.UMin, n.VMin);
            double3 p11 = SurfacePoint(s, q, n.UMax, n.VMax);
            double3 p10 = SurfacePoint(s, q, n.UMax, n.VMin);
            double3 p01 = SurfacePoint(s, q, n.UMin, n.VMax);
            return math.max(math.distance(p00, p11), math.distance(p10, p01));
        }

        // ── Cube face basis ──────────────────────────────────────────────────
        //
        // right x up == centre for every face (right-handed). Faces 2 (+Y) and 3 (-Y)
        // have deliberately non-obvious `up` vectors; that is what makes them
        // right-handed. Changing them invalidates the adjacency table below.

        public static double3 FaceCentre(int face) => face switch
        {
            0 => new double3( 1,  0,  0),
            1 => new double3(-1,  0,  0),
            2 => new double3( 0,  1,  0),
            3 => new double3( 0, -1,  0),
            4 => new double3( 0,  0,  1),
            _ => new double3( 0,  0, -1),
        };

        public static double3 FaceRight(int face) => face switch
        {
            0 => new double3( 0,  0, -1),
            1 => new double3( 0,  0,  1),
            2 => new double3( 1,  0,  0),
            3 => new double3( 1,  0,  0),
            4 => new double3( 1,  0,  0),
            _ => new double3(-1,  0,  0),
        };

        public static double3 FaceUp(int face) => face switch
        {
            0 => new double3( 0,  1,  0),
            1 => new double3( 0,  1,  0),
            2 => new double3( 0,  0, -1),
            3 => new double3( 0,  0,  1),
            4 => new double3( 0,  1,  0),
            _ => new double3( 0,  1,  0),
        };

        // ── Cube adjacency ───────────────────────────────────────────────────
        //
        // Derived geometrically and verified: all 24 links round-trip and the Reversed
        // flag is symmetric. CubeAdjacencyTests re-derives this from the face basis and
        // compares, so a mis-typed entry cannot survive. Eight of the 24 links are reversed.
        public static EdgeLink CubeEdgeLink(int face, Edge edge) => (face * 4 + (int)edge) switch
        {
            //                                     face 0 (+X)
            0  => new EdgeLink(3, Edge.East,  true),
            1  => new EdgeLink(5, Edge.West,  false),
            2  => new EdgeLink(2, Edge.East,  false),
            3  => new EdgeLink(4, Edge.East,  false),
            //                                     face 1 (-X)
            4  => new EdgeLink(3, Edge.West,  false),
            5  => new EdgeLink(4, Edge.West,  false),
            6  => new EdgeLink(2, Edge.West,  true),
            7  => new EdgeLink(5, Edge.East,  false),
            //                                     face 2 (+Y)
            8  => new EdgeLink(4, Edge.North, false),
            9  => new EdgeLink(0, Edge.North, false),
            10 => new EdgeLink(5, Edge.North, true),
            11 => new EdgeLink(1, Edge.North, true),
            //                                     face 3 (-Y)
            12 => new EdgeLink(5, Edge.South, true),
            13 => new EdgeLink(0, Edge.South, true),
            14 => new EdgeLink(4, Edge.South, false),
            15 => new EdgeLink(1, Edge.South, false),
            //                                     face 4 (+Z)
            16 => new EdgeLink(3, Edge.North, false),
            17 => new EdgeLink(0, Edge.West,  false),
            18 => new EdgeLink(2, Edge.South, false),
            19 => new EdgeLink(1, Edge.East,  false),
            //                                     face 5 (-Z)
            20 => new EdgeLink(3, Edge.South, true),
            21 => new EdgeLink(1, Edge.West,  false),
            22 => new EdgeLink(2, Edge.North, true),
            _  => new EdgeLink(0, Edge.East,  false),
        };

        public static Edge Opposite(Edge e) => (Edge)(((int)e + 2) & 3);

        // ── Neighbour lookup ─────────────────────────────────────────────────

        public static NeighbourResult Neighbour(in SurfaceDef s, NodeId n, Edge e)
        {
            int N = n.Span;

            // Same root quad: pure integer arithmetic.
            switch (e)
            {
                case Edge.South: if (n.Y > 0)     return Hit(new NodeId(n.Quad, n.Depth, n.X,     n.Y - 1), Edge.North); break;
                case Edge.North: if (n.Y < N - 1) return Hit(new NodeId(n.Quad, n.Depth, n.X,     n.Y + 1), Edge.South); break;
                case Edge.West:  if (n.X > 0)     return Hit(new NodeId(n.Quad, n.Depth, n.X - 1, n.Y),     Edge.East);  break;
                case Edge.East:  if (n.X < N - 1) return Hit(new NodeId(n.Quad, n.Depth, n.X + 1, n.Y),     Edge.West);  break;
            }

            return CrossQuad(s, n, e, N);
        }

        static NeighbourResult CrossQuad(in SurfaceDef s, NodeId n, Edge e, int N)
        {
            if (s.Type == SurfaceType.CubeSphere)
            {
                EdgeLink link = CubeEdgeLink(n.Quad, e);

                // Index along our edge, mapped onto the neighbour's edge.
                int t  = (e == Edge.South || e == Edge.North) ? n.X : n.Y;
                int t2 = link.Reversed ? (N - 1 - t) : t;

                int nx, ny;
                switch (link.Edge)
                {
                    case Edge.South: nx = t2;    ny = 0;     break;
                    case Edge.North: nx = t2;    ny = N - 1; break;
                    case Edge.West:  nx = 0;     ny = t2;    break;
                    default:         nx = N - 1; ny = t2;    break; // East
                }
                return Hit(new NodeId(link.Face, n.Depth, nx, ny), link.Edge);
            }

            // PlaneGrid and Cylinder share a col-major root layout: quad = col * Rows + row.
            // The cylinder wraps around its columns; the plane grid wraps in neither axis.
            // Neither ever reverses the along-edge parameter.
            int rows = s.Rows;
            if (rows <= 0 || s.Cols <= 0) return Miss();

            int c = n.Quad / rows, r = n.Quad % rows;
            bool wrapCols = s.Type == SurfaceType.Cylinder;

            switch (e)
            {
                case Edge.East:
                    c++;
                    if (c >= s.Cols) { if (!wrapCols) return Miss(); c = 0; }
                    return Hit(new NodeId(c * rows + r, n.Depth, 0, n.Y), Edge.West);

                case Edge.West:
                    c--;
                    if (c < 0) { if (!wrapCols) return Miss(); c = s.Cols - 1; }
                    return Hit(new NodeId(c * rows + r, n.Depth, N - 1, n.Y), Edge.East);

                case Edge.North:
                    r++;
                    if (r >= rows) return Miss();   // open ends: no caps
                    return Hit(new NodeId(c * rows + r, n.Depth, n.X, 0), Edge.South);

                default: // South
                    r--;
                    if (r < 0) return Miss();
                    return Hit(new NodeId(c * rows + r, n.Depth, n.X, N - 1), Edge.North);
            }
        }

        static NeighbourResult Hit(NodeId node, Edge arrival)
            => new NeighbourResult { Node = node, ArrivalEdge = arrival, Exists = true };

        static NeighbourResult Miss() => default;
    }
}
