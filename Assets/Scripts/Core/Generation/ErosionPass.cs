using System;
using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // The erosion graph's working arrays, one set per run: what ErosionPass.Run reads and writes.
    // Every array but the graph's three is NodeCount long.
    public struct ErosionArrays<TI, TF, TD>
        where TI : struct, IErosionArray<int>
        where TF : struct, IErosionArray<float>
        where TD : struct, IErosionArray<double>
    {
        public TI Neighbours;   // the graph's, MaxNeighbours a node
        public TF Lengths;      // the graph's
        public TD Areas;        // the graph's

        public TD Heights;      // in: T. Out: T', eroded, within the range of T
        public TD Filled;       // out: the last routing's depression-filled surface
        public TD Drainage;     // out: the last routing's drainage area A, square metres
        public TI Receivers;    // out: each node's receiver; an outlet is its own
        public TI Stack;        // out: nodes in routing order, every receiver before its donors
        public TI Heap;         // scratch: the flood's queue
        public TD HeapKeys;     // scratch: each queue entry's key, beside it
        public TI State;        // scratch: the flood's visited marks
        public TD Scratch;      // scratch: the diffusion sweep's new heights
    }

    // Stream-power erosion over the erosion graph (Docs/M5_MacroLayer_Plan.md, M5c, E3): the
    // implicit scheme of Braun and Willett (2013) with n = 1 and no uplift. Each iteration
    //
    //   1. Floods: a priority flood from the outlets (Barnes et al. 2014) gives the depression-filled
    //      surface, each node at least its own height and an epsilon slope above the node it was
    //      reached from. Its pop order is ascending filled height, ties by node index.
    //   2. Routes: each node's receiver is its steepest descent on the filled surface by
    //      (f - f_r) / length, ties to the lower index. The filled height falls strictly along every
    //      receiver, so the receivers are a forest whose roots are the outlets, and the flood's pop
    //      order is already the stack: every receiver before its donors.
    //   3. Drains: walking the stack backwards, each node adds its area to its receiver's.
    //   4. Incises, downstream first: h_i <- (h_i + F h_r) / (1 + F), F = KDt A_i^m / length_ir. It
    //      only ever lowers a node: inside a lake the receiver can stand above the unfilled ground,
    //      and the lake is left as a hollow, for a water system to fill later.
    //   5. Diffuses: one Jacobi sweep of h_i += KappaDt x laplacian, the laplacian on the graph as
    //      half the sum over neighbours of (h_j - h_i) / length^2, which on a square D8 grid is the
    //      continuous one to second order.
    //
    // Outlets are every node at or below BaseLevel, and are neither incised nor diffused. On a
    // surface with no node that low, the lowest node is the outlet, so all the water still leaves.
    // The cylinders' rims are not outlets (D1). After the iterations the pass floods, routes and
    // drains once more, so the drainage it returns is the eroded surface's, and clamps the heights
    // to the range they started in (E1).
    //
    // Every step is a sequential pass in double with each tie broken by index, so the result is the
    // same in any build: Run is the one function the managed reference and ErosionJob both call,
    // and its only non-IEEE-exact operation, A^m, is ErosionParams.AreaPower's square roots.
    public static class ErosionPass
    {
        // The filled surface's slope across a flat or a lake, in metres per metre: a lake 100 km
        // across rises by a centimetre. Enough for every filled node to stand strictly above the
        // node it was reached from, in double, at any height the maps hold.
        public const double EpsilonSlope = 1e-7;

        const int MaxNeighbours = ErosionGraph.MaxNeighbours;

        // KappaDt x the graph laplacian's largest diagonal, max over nodes of
        // KappaDt / 2 x sum of 1 / length^2. Above 1 the sweep overshoots: a node moves past the
        // mean of its neighbours, and repeated sweeps grow a checkerboard. Run refuses it.
        public static double DiffusionNumber<TF>(double kappaDt, int nodeCount, TF lengths)
            where TF : struct, IErosionArray<float>
        {
            double worst = 0.0;
            for (int node = 0; node < nodeCount; node++)
            {
                double sum = 0.0;
                for (int k = 0; k < MaxNeighbours; k++)
                {
                    double len = lengths.Get(node * MaxNeighbours + k);
                    if (len > 0.0) sum += 1.0 / (len * len);
                }
                worst = math.max(worst, 0.5 * kappaDt * sum);
            }
            return worst;
        }

        // Checks what the pass needs of its parameters and graph before it changes anything. The
        // managed reference and ErosionJob.Run both call it, so neither can run what the other
        // refuses.
        public static void Validate<TF>(in ErosionParams p, int nodeCount, TF lengths)
            where TF : struct, IErosionArray<float>
        {
            if (!p.Enabled)
                throw new ArgumentException("ErosionPass: the parameters have no erosion.");
            if (nodeCount < 1)
                throw new ArgumentException("ErosionPass: the graph has no nodes.");
            double d = DiffusionNumber(p.DiffusionKDt, nodeCount, lengths);
            if (d > 1.0)
                throw new ArgumentOutOfRangeException(nameof(p),
                    $"ErosionPass: the diffusion is unstable on this graph (diffusion number {d:F3}, at most 1). " +
                    "Lower the erosion settings' Diffusion.");
        }

        public static void Run<TI, TF, TD>(in ErosionParams p, int nodeCount, ref ErosionArrays<TI, TF, TD> a)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < nodeCount; i++)
            {
                double h = a.Heights.Get(i);
                lo = math.min(lo, h);
                hi = math.max(hi, h);
            }

            for (int it = 0; it < p.Iterations; it++)
            {
                Route(p, nodeCount, ref a);
                Drain(nodeCount, ref a);
                Incise(p, nodeCount, ref a);
                if (p.DiffusionKDt > 0.0) Diffuse(p, nodeCount, ref a);
            }

            for (int i = 0; i < nodeCount; i++)
                a.Heights.Set(i, math.clamp(a.Heights.Get(i), lo, hi));

            Route(p, nodeCount, ref a);
            Drain(nodeCount, ref a);
        }

        static bool IsOutlet<TI, TF, TD>(in ErosionArrays<TI, TF, TD> a, int node)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
            => a.Receivers.Get(node) == node;

        // Steps 1 and 2: the flood, the receivers and the stack.
        static void Route<TI, TF, TD>(in ErosionParams p, int nodeCount, ref ErosionArrays<TI, TF, TD> a)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            int size = 0, popped = 0;
            for (int i = 0; i < nodeCount; i++)
                a.State.Set(i, 0);

            // Outlets. Only those beside ground above the base level go in the queue. An outlet whose
            // neighbours are all outlets would come out of it to find every neighbour visited and push
            // nothing, so it goes straight onto the stack instead: its own receiver, before any node
            // that could drain to it. Nothing else reads its place in the stack, so the routing, the
            // drainage and the heights are the same bits either way, and the queue is smaller by the
            // sea -- 69% of the terrarium's nodes, about as much of the planets'.
            for (int i = 0; i < nodeCount; i++)
            {
                if (!(a.Heights.Get(i) <= p.BaseLevel)) continue;
                if (IsCoastal(p, ref a, i))
                    Seed(ref a, i, ref size);
                else
                {
                    a.Filled.Set(i, a.Heights.Get(i));
                    a.Receivers.Set(i, i);
                    a.State.Set(i, 2);
                    a.Stack.Set(popped++, i);
                }
            }

            while (popped < nodeCount)
            {
                // No outlet yet, or ground the outlets do not reach: the lowest node left unvisited
                // drains it. Every surface the terrain has is connected, so past the first flood
                // this is only for a surface with nothing at or below BaseLevel.
                if (size == 0)
                {
                    int lowest = -1;
                    for (int i = 0; i < nodeCount; i++)
                        if (a.State.Get(i) == 0 && (lowest < 0 || a.Heights.Get(i) < a.Heights.Get(lowest)))
                            lowest = i;
                    Seed(ref a, lowest, ref size);
                }

                int c = Pop(ref a, ref size);
                a.Stack.Set(popped++, c);
                double fc = a.Filled.Get(c);

                for (int k = 0; k < MaxNeighbours; k++)
                {
                    int m = a.Neighbours.Get(c * MaxNeighbours + k);
                    if (m < 0 || a.State.Get(m) != 0) continue;
                    double f = math.max(a.Heights.Get(m), fc + EpsilonSlope * a.Lengths.Get(c * MaxNeighbours + k));
                    a.Filled.Set(m, f);
                    a.State.Set(m, 1);
                    Push(ref a, m, ref size);
                }

                // c's receiver: the steepest descent on the filled surface. Every neighbour of c is
                // visited by now, and those lower than c were popped before it, so the filled
                // heights read here are this flood's. An outlet keeps itself, set by Seed.
                if (a.State.Get(c) == 2) continue;
                int    best  = -1;
                double slope = 0.0;
                for (int k = 0; k < MaxNeighbours; k++)
                {
                    int m = a.Neighbours.Get(c * MaxNeighbours + k);
                    if (m < 0) continue;
                    double drop = fc - a.Filled.Get(m);
                    if (!(drop > 0.0)) continue;
                    double s = drop / a.Lengths.Get(c * MaxNeighbours + k);
                    if (best < 0 || s > slope || (s == slope && m < best))
                    {
                        best  = m;
                        slope = s;
                    }
                }
                // Reached from a node strictly lower, so there is one; were rounding ever to swallow
                // the epsilon, c would drain itself rather than nowhere.
                a.Receivers.Set(c, best >= 0 ? best : c);
            }
        }

        // An outlet with a neighbour above the base level, which the flood has to start from.
        static bool IsCoastal<TI, TF, TD>(in ErosionParams p, ref ErosionArrays<TI, TF, TD> a, int node)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            for (int k = 0; k < MaxNeighbours; k++)
            {
                int m = a.Neighbours.Get(node * MaxNeighbours + k);
                if (m >= 0 && !(a.Heights.Get(m) <= p.BaseLevel)) return true;
            }
            return false;
        }

        // An outlet: filled at its own height, its own receiver, and marked 2 so Route leaves it so.
        static void Seed<TI, TF, TD>(ref ErosionArrays<TI, TF, TD> a, int node, ref int size)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            a.Filled.Set(node, a.Heights.Get(node));
            a.Receivers.Set(node, node);
            a.State.Set(node, 2);
            Push(ref a, node, ref size);
        }

        // Step 3: drainage area, upstream first.
        static void Drain<TI, TF, TD>(int nodeCount, ref ErosionArrays<TI, TF, TD> a)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            for (int i = 0; i < nodeCount; i++)
                a.Drainage.Set(i, a.Areas.Get(i));
            for (int s = nodeCount - 1; s >= 0; s--)
            {
                int i = a.Stack.Get(s), r = a.Receivers.Get(i);
                if (r != i) a.Drainage.Set(r, a.Drainage.Get(r) + a.Drainage.Get(i));
            }
        }

        // Step 4: implicit incision, downstream first, so each receiver is already this step's.
        static void Incise<TI, TF, TD>(in ErosionParams p, int nodeCount, ref ErosionArrays<TI, TF, TD> a)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            if (!(p.KDt > 0.0)) return;
            for (int s = 0; s < nodeCount; s++)
            {
                int i = a.Stack.Get(s), r = a.Receivers.Get(i);
                if (r == i) continue;
                double hi = a.Heights.Get(i), hr = a.Heights.Get(r);
                if (!(hi > hr)) continue;
                double f = p.KDt * p.AreaPower(a.Drainage.Get(i)) / LinkLength(ref a, i, r);
                a.Heights.Set(i, (hi + f * hr) / (1.0 + f));
            }
        }

        static double LinkLength<TI, TF, TD>(ref ErosionArrays<TI, TF, TD> a, int i, int r)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            for (int k = 0; k < MaxNeighbours; k++)
                if (a.Neighbours.Get(i * MaxNeighbours + k) == r)
                    return a.Lengths.Get(i * MaxNeighbours + k);
            return 1.0;   // unreachable: a receiver is a neighbour
        }

        // Step 5: one Jacobi sweep, every node reading the last sweep's heights.
        static void Diffuse<TI, TF, TD>(in ErosionParams p, int nodeCount, ref ErosionArrays<TI, TF, TD> a)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            for (int i = 0; i < nodeCount; i++)
            {
                double h = a.Heights.Get(i);
                if (IsOutlet(a, i))
                {
                    a.Scratch.Set(i, h);
                    continue;
                }
                double sum = 0.0;
                for (int k = 0; k < MaxNeighbours; k++)
                {
                    int m = a.Neighbours.Get(i * MaxNeighbours + k);
                    if (m < 0) continue;
                    double len = a.Lengths.Get(i * MaxNeighbours + k);
                    sum += (a.Heights.Get(m) - h) / (len * len);
                }
                a.Scratch.Set(i, h + 0.5 * p.DiffusionKDt * sum);
            }
            for (int i = 0; i < nodeCount; i++)
                a.Heights.Set(i, a.Scratch.Get(i));
        }

        // ── The flood's queue: a binary heap of nodes, keyed (filled height, index) ──
        //
        // A total order, so the pop sequence is the same whatever the heap's internal layout. Each
        // entry carries its key, a node's filled height as it was pushed, in HeapKeys beside Heap: a
        // comparison then reads the two slots it compares rather than two nodes' filled heights
        // scattered across the graph. A node's filled height is set once, before its push, so the key
        // is always the one a comparison through Filled would read, and the pass's results are the
        // same bits. On the managed reference it saves about 5% of the terrarium's pass (2,205 ms to
        // 2,100), where routing and drainage are 1,493 ms, incision 375 and diffusion 174.

        static bool Before(double kx, int x, double ky, int y) => kx < ky || (kx == ky && x < y);

        static void Push<TI, TF, TD>(ref ErosionArrays<TI, TF, TD> a, int node, ref int size)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            double key = a.Filled.Get(node);
            int    at  = size++;
            while (at > 0)
            {
                int    parent = (at - 1) >> 1;
                int    up     = a.Heap.Get(parent);
                double upKey  = a.HeapKeys.Get(parent);
                if (!Before(key, node, upKey, up)) break;
                a.Heap.Set(at, up);
                a.HeapKeys.Set(at, upKey);
                at = parent;
            }
            a.Heap.Set(at, node);
            a.HeapKeys.Set(at, key);
        }

        static int Pop<TI, TF, TD>(ref ErosionArrays<TI, TF, TD> a, ref int size)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            int    top     = a.Heap.Get(0);
            int    last    = a.Heap.Get(--size);
            double lastKey = a.HeapKeys.Get(size);
            int    at      = 0;
            while (true)
            {
                int child = 2 * at + 1;
                if (child >= size) break;
                int    c  = a.Heap.Get(child);
                double kc = a.HeapKeys.Get(child);
                if (child + 1 < size)
                {
                    int    c2 = a.Heap.Get(child + 1);
                    double k2 = a.HeapKeys.Get(child + 1);
                    if (Before(k2, c2, kc, c)) { child++; c = c2; kc = k2; }
                }
                if (!Before(kc, c, lastKey, last)) break;
                a.Heap.Set(at, c);
                a.HeapKeys.Set(at, kc);
                at = child;
            }
            if (size > 0)
            {
                a.Heap.Set(at, last);
                a.HeapKeys.Set(at, lastKey);
            }
            return top;
        }

        // ── The managed reference ──

        public sealed class Result
        {
            public double[] Heights, Filled, Drainage;
            public int[]    Receivers, Stack;
        }

        // Erodes heights, one per node of the graph, and returns the eroded heights and the last
        // routing. The input array is left as it was.
        public static Result Run(in ErosionParams p, ErosionGraph g, double[] heights)
        {
            if (heights == null || heights.Length != g.NodeCount)
                throw new ArgumentException("ErosionPass: one height per node of the graph.", nameof(heights));
            Validate(p, g.NodeCount, new ErosionGraph.Floats(g.Lengths));

            int n = g.NodeCount;
            var r = new Result
            {
                Heights = (double[])heights.Clone(), Filled = new double[n], Drainage = new double[n],
                Receivers = new int[n], Stack = new int[n],
            };
            var a = new ErosionArrays<ErosionGraph.Ints, ErosionGraph.Floats, ErosionGraph.Doubles>
            {
                Neighbours = new ErosionGraph.Ints(g.Neighbours),
                Lengths    = new ErosionGraph.Floats(g.Lengths),
                Areas      = new ErosionGraph.Doubles(g.Areas),
                Heights    = new ErosionGraph.Doubles(r.Heights),
                Filled     = new ErosionGraph.Doubles(r.Filled),
                Drainage   = new ErosionGraph.Doubles(r.Drainage),
                Receivers  = new ErosionGraph.Ints(r.Receivers),
                Stack      = new ErosionGraph.Ints(r.Stack),
                Heap       = new ErosionGraph.Ints(new int[n]),
                HeapKeys   = new ErosionGraph.Doubles(new double[n]),
                State      = new ErosionGraph.Ints(new int[n]),
                Scratch    = new ErosionGraph.Doubles(new double[n]),
            };
            Run(p, n, ref a);
            return r;
        }
    }
}
