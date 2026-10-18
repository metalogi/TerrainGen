using System;
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
    // Height is added along the normal; HeightSampleJob and ChunkMeshJob are the callers.
    //
    // Named SurfaceMath, not Surface, on purpose. A type may not share the name of its
    // own namespace: from a sibling namespace such as Sonoma.Core.Generation, with the
    // house style of putting usings above the namespace, the simple name `Surface` binds
    // to the namespace Sonoma.Core.Surface found in the enclosing Sonoma.Core scope, and
    // `Surface.SurfacePoint(...)` fails to compile with CS0234. Do not rename it back.
    public static class SurfaceMath
    {
        const double QuarterPi = 0.7853981633974483096;  // pi/4
        const double TwoPi     = 6.2831853071795864769;

        // Guard messages are compile-time constants so the throw sites stay Burst-compilable.
        const string FaceRangeMessage = "SurfaceMath: cube face index must be in 0..5.";

        // ── Root construction ────────────────────────────────────────────────

        public static RootQuad[] BuildRoots(in SurfaceDef s)
        {
            switch (s.Type)
            {
                case SurfaceType.CubeSphere:
                {
                    var roots = new RootQuad[6];
                    for (int f = 0; f < 6; f++) roots[f] = new RootQuad { Index = f };
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
                        int i = col * s.Rows + row;
                        roots[i] = new RootQuad
                        {
                            Index  = i,
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
                        int i = col * s.Rows + row;
                        roots[i] = new RootQuad
                        {
                            Index  = i,
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
                case SurfaceType.CubeSphere: CubeSphereFrame(s, q.Index, u, v, out point, out normal); return;
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
            // NodeId already carries its quad, so the RootQuad argument is redundant and can
            // disagree with it. A mismatch evaluates the node's UV range against another
            // quad's basis and returns a wrong-but-plausible size, which would bias LOD
            // decisions with nothing asserting. Fail instead.
            if (q.Index != n.Quad)
                throw new ArgumentException(
                    "SurfaceMath.NodeWorldSize: RootQuad.Index does not match NodeId.Quad; " +
                    "the root and the node describe different quads.", nameof(q));

            double3 p00 = SurfacePoint(s, q, n.UMin, n.VMin);
            double3 p11 = SurfacePoint(s, q, n.UMax, n.VMax);
            double3 p10 = SurfacePoint(s, q, n.UMax, n.VMin);
            double3 p01 = SurfacePoint(s, q, n.UMin, n.VMax);
            return math.max(math.distance(p00, p11), math.distance(p10, p01));
        }

        // The largest ratio of measured NodeWorldSize to nominal size (S0 / 2^depth) any
        // node of this surface can reach.
        //
        // A cube face is not uniformly parameterised, so nodes at one depth are not all the
        // same size. LodMath needs this number: the morph range of a coarse node has to
        // start beyond every point of its own patch, and how far a patch reaches past its
        // own bounding-sphere distance is exactly this spread. Feed it too small a value and
        // the coarse side of a LOD boundary begins morphing while the fine side has already
        // finished, which is a crack.
        //
        // Measured, converging with depth: pi/2 for a tangent-adjusted cube sphere (1.56999
        // at depth 10), sqrt(3) without the adjustment (1.73205), exactly 1 for a plane grid
        // whose nodes are all similar, and 1.01259 for a cylinder -- rounded up to 1.02 so
        // the constant is an upper bound rather than a sample.
        // SurfaceGeometryTests.NodeSizeSpreadMatchesTheTable re-derives all four numerically.
        public static double MaxNodeSizeSpread(in SurfaceDef s) => s.Type switch
        {
            SurfaceType.CubeSphere => s.TangentAdjust ? math.PI_DBL / 2.0 : math.sqrt(3.0),
            SurfaceType.PlaneGrid  => 1.0,
            SurfaceType.Cylinder   => 1.02,
            _ => throw new ArgumentOutOfRangeException(nameof(s),
                     "SurfaceMath.MaxNodeSizeSpread: unknown surface type."),
        };

        // Whether (du, dv, normal) is a right-handed frame, i.e. dot(cross(du, dv), n) > 0.
        //
        // The topologies do not agree, and nothing before M2 cared. A cube-sphere is
        // right-handed: u runs along `right`, v along `up`, and right x up == centre, which
        // is the outward normal. A plane grid is left-handed: u runs +X, v runs +Z, and
        // cross(+X, +Z) == -Y while the normal is +Y. A cylinder is left-handed too, because
        // its normal points inward at the axis for interior viewing while cross(du, dv)
        // points outward.
        //
        // The consequence is entirely about triangle winding: one fixed vertex order cannot
        // face outward on both groups, so a mesh built for a plane renders a cube-sphere
        // inside out. ChunkMeshLayout.WriteIndices takes this as its flipWinding argument.
        // Measured values are +2.467e-2 (every cube face), -1.0e-4 (plane), -7.854e-4
        // (cylinder); SurfaceGeometryTests.UvFrameHandednessMatchesTheTable re-derives them
        // numerically rather than trusting this switch.
        public static bool UvFrameIsRightHanded(in SurfaceDef s) => s.Type switch
        {
            SurfaceType.CubeSphere => true,
            SurfaceType.PlaneGrid  => false,
            SurfaceType.Cylinder   => false,
            _ => throw new ArgumentOutOfRangeException(nameof(s),
                     "SurfaceMath.UvFrameIsRightHanded: unknown surface type."),
        };

        // ── Cube face basis ──────────────────────────────────────────────────
        //
        // right x up == centre for every face (right-handed). Faces 2 (+Y) and 3 (-Y)
        // have deliberately non-obvious `up` vectors; that is what makes them
        // right-handed. Changing them invalidates the adjacency table below.
        //
        // Face 5 is spelled out rather than left to the catch-all, so an out-of-range index
        // throws instead of quietly evaluating face 5's geometry. Same reasoning as the
        // NodeId.Parent guard: a bad quad index should fail here, not surface later as
        // terrain in the wrong place.

        public static double3 FaceCentre(int face) => face switch
        {
            0 => new double3( 1,  0,  0),
            1 => new double3(-1,  0,  0),
            2 => new double3( 0,  1,  0),
            3 => new double3( 0, -1,  0),
            4 => new double3( 0,  0,  1),
            5 => new double3( 0,  0, -1),
            _ => throw new ArgumentOutOfRangeException(nameof(face), FaceRangeMessage),
        };

        public static double3 FaceRight(int face) => face switch
        {
            0 => new double3( 0,  0, -1),
            1 => new double3( 0,  0,  1),
            2 => new double3( 1,  0,  0),
            3 => new double3( 1,  0,  0),
            4 => new double3( 1,  0,  0),
            5 => new double3(-1,  0,  0),
            _ => throw new ArgumentOutOfRangeException(nameof(face), FaceRangeMessage),
        };

        public static double3 FaceUp(int face) => face switch
        {
            0 => new double3( 0,  1,  0),
            1 => new double3( 0,  1,  0),
            2 => new double3( 0,  0, -1),
            3 => new double3( 0,  0,  1),
            4 => new double3( 0,  1,  0),
            5 => new double3( 0,  1,  0),
            _ => throw new ArgumentOutOfRangeException(nameof(face), FaceRangeMessage),
        };

        // ── Cube adjacency ───────────────────────────────────────────────────
        //
        // Derived geometrically and verified: all 24 links round-trip and the Reversed
        // flag is symmetric. CubeAdjacencyTests re-derives this from the face basis and
        // compares, so a mis-typed entry cannot survive. Eight of the 24 links are reversed.
        public static EdgeLink CubeEdgeLink(int face, Edge edge)
        {
            // The index is flattened, so an out-of-range face or edge does not overflow into
            // nothing -- it lands on another face's row and returns a valid-looking link.
            // Validate both before flattening.
            if ((uint)face > 5u)
                throw new ArgumentOutOfRangeException(nameof(face), FaceRangeMessage);
            if ((uint)edge > 3u)
                throw new ArgumentOutOfRangeException(nameof(edge),
                    "SurfaceMath.CubeEdgeLink: edge must be South, East, North or West.");

            return (face * 4 + (int)edge) switch
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
                23 => new EdgeLink(0, Edge.East,  false),
                _  => throw new InvalidOperationException(
                          "SurfaceMath.CubeEdgeLink: unreachable; face and edge are validated above."),
            };
        }

        public static Edge Opposite(Edge e) => (Edge)(((int)e + 2) & 3);

        // Where a root quad's edge leads, on any topology: false where the surface has no
        // neighbour there (a plane grid's outer edges, a cylinder's open ends). The link's Face
        // is the neighbouring quad's index, which on a cube sphere is also its face.
        //
        // The one place root-quad adjacency lives. Neighbour crosses quads through it, and the
        // macro layer's edge rule (MacroLayout.EdgeSource) uses it to decide which quad owns a
        // shared edge -- the two must agree on what is adjacent to what, so they share the data
        // rather than each holding a copy.
        //
        // PlaneGrid and Cylinder share a col-major root layout, quad = col * Rows + row. The
        // cylinder wraps around its columns; the plane grid wraps in neither axis. Neither ever
        // reverses the along-edge parameter.
        public static bool QuadEdgeLink(in SurfaceDef s, int quad, Edge e, out EdgeLink link)
        {
            if ((uint)quad >= (uint)s.QuadCount)
                throw new ArgumentOutOfRangeException(nameof(quad),
                    "SurfaceMath.QuadEdgeLink: quad is outside this surface's root quad count.");
            if ((uint)e > 3u)
                throw new ArgumentOutOfRangeException(nameof(e),
                    "SurfaceMath.QuadEdgeLink: edge must be South, East, North or West.");

            if (s.Type == SurfaceType.CubeSphere)
            {
                link = CubeEdgeLink(quad, e);
                return true;
            }

            link = default;
            int rows = s.Rows;
            if (rows <= 0 || s.Cols <= 0) return false;

            int c = quad / rows, r = quad % rows;
            bool wrapCols = s.Type == SurfaceType.Cylinder;

            switch (e)
            {
                case Edge.East:
                    c++;
                    if (c >= s.Cols) { if (!wrapCols) return false; c = 0; }
                    link = new EdgeLink(c * rows + r, Edge.West, false);
                    return true;

                case Edge.West:
                    c--;
                    if (c < 0) { if (!wrapCols) return false; c = s.Cols - 1; }
                    link = new EdgeLink(c * rows + r, Edge.East, false);
                    return true;

                case Edge.North:
                    r++;
                    if (r >= rows) return false;   // open ends: no caps
                    link = new EdgeLink(c * rows + r, Edge.South, false);
                    return true;

                default: // South
                    r--;
                    if (r < 0) return false;
                    link = new EdgeLink(c * rows + r, Edge.North, false);
                    return true;
            }
        }

        // ── Inverse ──────────────────────────────────────────────────────────

        // The root quad and (u, v) whose surface point lies on the ray from the surface's own
        // centre through p -- the inverse of SurfacePoint, for a point on the base surface or
        // anywhere along its normal. TerrainRoot.HeightAt needs it to read the macro maps, which
        // are addressed by (quad, u, v), from a point.
        //
        // A tool path, never a generation path: chunks know their quad and (u, v) already, and
        // nothing here has to agree with anything to the bit. atan(tan(x)) and the divisions
        // leave about 1e-15 of rounding in u and v. A point on a shared edge may come back on
        // either quad, which the macro layer makes irrelevant: its reads agree there to the bit
        // from both sides (MacroLayout.EdgeSource). u and v are clamped to [0, 1], and a point
        // beyond a plane grid's tiles or a cylinder's ends comes back on the nearest quad.
        //
        // Plane grid: the tile by floor. Cylinder: the column by angle, the row by length.
        // Cube sphere: the face by the largest-magnitude axis, then the projection onto that
        // face's plane, then the tangent adjustment undone with atan.
        public static void Locate(in SurfaceDef s, double3 p, out int quad, out double u, out double v)
        {
            switch (s.Type)
            {
                case SurfaceType.CubeSphere:
                {
                    double3 m = math.abs(p);
                    int face = m.x >= m.y && m.x >= m.z ? (p.x >= 0.0 ? 0 : 1)
                             : m.y >= m.z               ? (p.y >= 0.0 ? 2 : 3)
                             :                            (p.z >= 0.0 ? 4 : 5);

                    double depth = math.dot(p, FaceCentre(face));
                    double a = math.dot(p, FaceRight(face)) / depth;
                    double b = math.dot(p, FaceUp(face))    / depth;
                    if (s.TangentAdjust)
                    {
                        a = math.atan(a) / QuarterPi;
                        b = math.atan(b) / QuarterPi;
                    }

                    quad = face;
                    u = math.saturate(0.5 * (a + 1.0));
                    v = math.saturate(0.5 * (b + 1.0));
                    return;
                }

                case SurfaceType.Cylinder:
                {
                    // The same arithmetic BuildRoots uses for each column's and row's range, so
                    // a point on a quad's own range maps back into it.
                    double dAngle = TwoPi / s.Cols;
                    double dZ     = s.Length / s.Rows;

                    double angle = math.atan2(p.y, p.x);
                    if (angle < 0.0) angle += TwoPi;
                    int col = math.clamp((int)math.floor(angle / dAngle), 0, s.Cols - 1);
                    int row = math.clamp((int)math.floor(p.z / dZ),       0, s.Rows - 1);

                    double angle0 = col * dAngle, angle1 = (col + 1) * dAngle;
                    double z0     = row * dZ,     z1     = (row + 1) * dZ;

                    quad = col * s.Rows + row;
                    u = math.saturate((angle - angle0) / (angle1 - angle0));
                    v = math.saturate((p.z   - z0)     / (z1     - z0));
                    return;
                }

                default: // PlaneGrid
                {
                    double halfX = s.Cols * s.TileSize * 0.5;
                    double halfZ = s.Rows * s.TileSize * 0.5;
                    int col = math.clamp((int)math.floor((p.x + halfX) / s.TileSize), 0, s.Cols - 1);
                    int row = math.clamp((int)math.floor((p.z + halfZ) / s.TileSize), 0, s.Rows - 1);

                    quad = col * s.Rows + row;
                    u = math.saturate((p.x - (col * s.TileSize - halfX)) / s.TileSize);
                    v = math.saturate((p.z - (row * s.TileSize - halfZ)) / s.TileSize);
                    return;
                }
            }
        }

        // ── Neighbour lookup ─────────────────────────────────────────────────

        public static NeighbourResult Neighbour(in SurfaceDef s, NodeId n, Edge e)
        {
            // A malformed address used to answer with a plausible-looking neighbour rather
            // than fail: an out-of-range Quad fell through the face switches' catch-all and
            // got face 5's basis with face 0's adjacency. Depth is bounded because C# masks
            // shift counts, so Span would wrap negative past 30.
            if ((uint)n.Depth >= 31u)
                throw new ArgumentOutOfRangeException(nameof(n),
                    "SurfaceMath.Neighbour: NodeId.Depth must be in 0..30.");
            if ((uint)n.Quad >= (uint)s.QuadCount)
                throw new ArgumentOutOfRangeException(nameof(n),
                    "SurfaceMath.Neighbour: NodeId.Quad is outside this surface's root quad count.");

            int N = n.Span;
            if ((uint)n.X >= (uint)N || (uint)n.Y >= (uint)N)
                throw new ArgumentOutOfRangeException(nameof(n),
                    "SurfaceMath.Neighbour: NodeId.X/Y are outside the node grid at this depth.");

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
            if (!QuadEdgeLink(s, n.Quad, e, out EdgeLink link)) return Miss();

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

        static NeighbourResult Hit(NodeId node, Edge arrival)
            => new NeighbourResult { Node = node, ArrivalEdge = arrival, Exists = true };

        static NeighbourResult Miss() => default;
    }
}
