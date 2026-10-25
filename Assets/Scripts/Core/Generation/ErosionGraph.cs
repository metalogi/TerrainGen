using System;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // One array the graph is built over, read and written by index: a managed array here, a
    // NativeArray in ErosionGraphJob. A generic constraint, as IMacroScalars is, so Burst
    // specialises the builder for the struct it is given.
    public interface IErosionArray<T> where T : struct
    {
        T    Get(int index);
        void Set(int index, T value);
    }

    // The graph macro erosion runs on (Docs/M5_MacroLayer_Plan.md, M5c, E2): one graph over the
    // whole surface, not one per quad, because water crosses root-quad edges.
    //
    //   * Nodes. Every top-level texel from 0 to n on each axis of every quad, with shared edge and
    //     corner texels merged to their owner by MacroLayout.EdgeSource, C2's ownership rule. A
    //     node's value can then go back to every quad that stores it, and EdgeCopies keeps the edge
    //     rows identical. A cube sphere has 6n^2 + 2 nodes, a cylinder of C x R quads Cn(Rn + 1), a
    //     plane grid (Cn + 1)(Rn + 1).
    //   * Neighbours. Up to eight: the D8 offsets, carried across an edge by the halo's crossing
    //     (MacroLayout.Cross and Walk), reversal included, then merged to the owner. A diagonal step
    //     across a cube corner, where three faces meet, has no node and is dropped. Past a rim there
    //     is nothing. Two offsets that land on one node count once, which they do at a cube corner.
    //     Neighbours are packed first in each node's eight slots, in offset order (rows, then
    //     columns), and the rest are -1.
    //   * Lengths and areas from the surface, never the grid: a cube sphere's texels vary 1.6:1
    //     across a face. A link's length is the chord between its nodes' surface points. A node's
    //     area is a quarter of each texel cell it is a corner of, every cell measured by the vector
    //     area of its corners, so the areas sum to the surface's to the chord's error.
    //
    // Nodes are numbered, and their points computed, in managed code in both builds: numbering is
    // sequential, and SurfacePoint's trigonometry is not Burst's to the bit (the continent jobs'
    // rule). The neighbours, lengths and areas are Fill, the same static function in both, so the
    // Burst graph equals this one by construction.
    public sealed class ErosionGraph
    {
        public const int MaxNeighbours = 8;

        public int       Cells;        // n, the top level's texel cells across a quad
        public int       NodeCount;
        public int[]     NodeOf;       // graph texel -> node
        public int[]     NodeTexel;    // node -> its owner's graph texel
        public double3[] Points;       // graph texel -> its surface point
        public int[]     Neighbours;   // MaxNeighbours per node, -1 for none
        public float[]   Lengths;      // metres, per neighbour slot; 0 for none
        public double[]  Areas;        // square metres, per node

        // A graph texel is (quad, i, j) with i and j in [0, n]: the top level without its border.
        public static int TexelsPerQuad(int n) => (n + 1) * (n + 1);
        public static int Texel(int n, int q, int i, int j) => q * TexelsPerQuad(n) + j * (n + 1) + i;

        public static void Coords(int n, int texel, out int q, out int i, out int j)
        {
            int per = TexelsPerQuad(n);
            q = texel / per;
            int r = texel - q * per;
            j = r / (n + 1);
            i = r - j * (n + 1);
        }

        // The managed reference: the whole graph.
        public static ErosionGraph Build(in SurfaceDef s, RootQuad[] roots, in MacroLayout layout)
        {
            var g = Prepare(s, roots, layout);
            g.Neighbours = new int[g.NodeCount * MaxNeighbours];
            g.Lengths    = new float[g.NodeCount * MaxNeighbours];
            g.Areas      = new double[g.NodeCount];
            Fill(s, g.Cells, g.NodeCount, new Ints(g.NodeOf), new Ints(g.NodeTexel), new Points3(g.Points),
                 new Ints(g.Neighbours), new Floats(g.Lengths), new Doubles(g.Areas));
            return g;
        }

        // The nodes and points, which both builds compute here: a texel its own quad owns becomes a
        // node, in texel order, and every other takes its owner's node.
        public static ErosionGraph Prepare(in SurfaceDef s, RootQuad[] roots, in MacroLayout layout)
        {
            if (roots == null || roots.Length != layout.QuadCount)
                throw new ArgumentException("ErosionGraph: one root per quad of the layout.", nameof(roots));

            int    n     = layout.Cells(layout.LevelCount - 1);
            int    total = layout.QuadCount * TexelsPerQuad(n);
            var    nodeOf = new int[total];
            var    owners = new System.Collections.Generic.List<int>(total);
            var    points = new double3[total];
            double cells  = n;

            for (int t = 0; t < total; t++)
            {
                Coords(n, t, out int q, out int i, out int j);
                points[t] = SurfaceMath.SurfacePoint(s, roots[q], i / cells, j / cells);
                if (layout.EdgeSource(s, q, layout.LevelCount - 1, i, j, out _, out _, out _))
                    nodeOf[t] = -1;
                else
                {
                    nodeOf[t] = owners.Count;
                    owners.Add(t);
                }
            }

            // A source is a texel nothing copies into, so every one was numbered above.
            for (int t = 0; t < total; t++)
            {
                if (nodeOf[t] >= 0) continue;
                Coords(n, t, out int q, out int i, out int j);
                layout.EdgeSource(s, q, layout.LevelCount - 1, i, j, out int sq, out int si, out int sj);
                nodeOf[t] = nodeOf[Texel(n, sq, si, sj)];
            }

            return new ErosionGraph
            {
                Cells = n, NodeCount = owners.Count, NodeOf = nodeOf, NodeTexel = owners.ToArray(), Points = points,
            };
        }

        // The neighbours, lengths and areas, from the numbering and points.
        public static void Fill<TI, TP, TF, TD>(in SurfaceDef s, int n, int nodeCount, TI nodeOf, TI nodeTexel,
                                                TP points, TI neighbours, TF lengths, TD areas)
            where TI : struct, IErosionArray<int>
            where TP : struct, IErosionArray<double3>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            for (int node = 0; node < nodeCount; node++)
            {
                Coords(n, nodeTexel.Get(node), out int q, out int i, out int j);
                double3 p     = points.Get(nodeTexel.Get(node));
                int     first = node * MaxNeighbours, count = 0;

                for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0) continue;
                    if (!Resolve(s, n, q, i + di, j + dj, out int rq, out int ri, out int rj)) continue;
                    int m = nodeOf.Get(Texel(n, rq, ri, rj));
                    if (m == node) continue;

                    bool seen = false;
                    for (int k = 0; k < count; k++)
                        seen |= neighbours.Get(first + k) == m;
                    if (seen) continue;

                    neighbours.Set(first + count, m);
                    lengths.Set(first + count, (float)math.length(p - points.Get(nodeTexel.Get(m))));
                    count++;
                }

                for (int k = count; k < MaxNeighbours; k++)
                {
                    neighbours.Set(first + k, -1);
                    lengths.Set(first + k, 0f);
                }
            }

            for (int node = 0; node < nodeCount; node++)
                areas.Set(node, 0.0);

            // A quarter of each cell to each of its corners, cells in texel order, corners in a fixed
            // order, so the sums are the same in both builds.
            for (int q = 0; q < s.QuadCount; q++)
            for (int cj = 0; cj < n; cj++)
            for (int ci = 0; ci < n; ci++)
            {
                int t00 = Texel(n, q, ci, cj),     t10 = Texel(n, q, ci + 1, cj);
                int t01 = Texel(n, q, ci, cj + 1), t11 = Texel(n, q, ci + 1, cj + 1);
                double3 p00 = points.Get(t00), p10 = points.Get(t10), p01 = points.Get(t01), p11 = points.Get(t11);
                double quarter = 0.125 * math.length(math.cross(p11 - p00, p01 - p10));
                Add(areas, nodeOf.Get(t00), quarter);
                Add(areas, nodeOf.Get(t10), quarter);
                Add(areas, nodeOf.Get(t01), quarter);
                Add(areas, nodeOf.Get(t11), quarter);
            }
        }

        static void Add<TD>(TD areas, int node, double a) where TD : struct, IErosionArray<double>
            => areas.Set(node, areas.Get(node) + a);

        // The graph texel a D8 step lands on, or false where there is none: past a rim, or
        // diagonally across a cube corner, where three faces meet and no quad is diagonal. Inside a
        // cylinder or plane grid four quads meet at an interior corner, and the walk lands on the
        // diagonal quad's texel. The cube's case is dropped explicitly, as E2 states it, though the
        // walk alone gives the same graph there: removing the check changes no test's result.
        static bool Resolve(in SurfaceDef s, int n, int q, int i, int j, out int rq, out int ri, out int rj)
        {
            rq = q; ri = i; rj = j;
            bool outI = i < 0 || i > n;
            bool outJ = j < 0 || j > n;
            if (!outI && !outJ) return true;

            if (outI && outJ)
            {
                if (s.Type == SurfaceType.CubeSphere) return false;
                return MacroLayout.Walk(s, n, q, i, j, crossJFirst: true,  out rq, out ri, out rj)
                    || MacroLayout.Walk(s, n, q, i, j, crossJFirst: false, out rq, out ri, out rj);
            }

            if (!MacroLayout.Cross(s, n, ref rq, ref ri, ref rj, crossJ: outJ)) return false;
            return ri >= 0 && ri <= n && rj >= 0 && rj <= n;
        }

        public readonly struct Ints : IErosionArray<int>
        {
            readonly int[] _a;
            public Ints(int[] a) { _a = a; }
            public int  Get(int index)            => _a[index];
            public void Set(int index, int value) => _a[index] = value;
        }

        public readonly struct Floats : IErosionArray<float>
        {
            readonly float[] _a;
            public Floats(float[] a) { _a = a; }
            public float Get(int index)              => _a[index];
            public void  Set(int index, float value) => _a[index] = value;
        }

        public readonly struct Doubles : IErosionArray<double>
        {
            readonly double[] _a;
            public Doubles(double[] a) { _a = a; }
            public double Get(int index)               => _a[index];
            public void   Set(int index, double value) => _a[index] = value;
        }

        public readonly struct Points3 : IErosionArray<double3>
        {
            readonly double3[] _a;
            public Points3(double3[] a) { _a = a; }
            public double3 Get(int index)                => _a[index];
            public void    Set(int index, double3 value) => _a[index] = value;
        }
    }
}
