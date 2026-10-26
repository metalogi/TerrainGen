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
    // Outlets are the sea: every water body -- nodes at or below BaseLevel, connected -- of at least
    // MinOutletArea, or the largest if none is (MarkSeas). They are neither incised nor diffused. A
    // smaller body is a hollow, which the flood fills and routes rivers through, so they reach the
    // sea rather than ending in it; on the ring, at a threshold of 0, 60% of the land drained into
    // hollows under 1% of the water. On a surface with no water at all the lowest node is the
    // outlet, so all the water still leaves. The cylinders' rims are not outlets (D1). After the iterations the pass floods, routes and
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

            if (p.BreachDepth > 0.0) Condition(p, nodeCount, ref a);

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

        // ── Breaching and filling, once, before the iterations ──
        //
        // A closed basin is a lake to the flood: rivers cross its filled surface, which incision never
        // cuts, so it stays flat and the river network stops at its rim. On the ring a fifth of the
        // land lay in basins walled by its mountain band, kilometres high, which 30 iterations of
        // incision barely notch. So the surface is first made to drain, by the hybrid of breaching and
        // filling (after Lindsay 2016):
        //
        //   A priority flood from the seas over the heights, remembering in Receivers the node each was
        //   reached from. When it steps from a node c down to a lower neighbour m, m is in a basin. A
        //   channel is carved from c back along the remembered chain towards the sea, each node lowered
        //   to just below m, strictly downhill, until the chain is already lower or reaches the sea. If
        //   that would lower any node more than BreachDepth below its own height, nothing is carved and
        //   m is raised to just above c instead: filled.
        //
        // So a shallow basin gets an outlet and keeps its floor; a deep one gets an outlet carved
        // up to BreachDepth deep and is filled up to it, a flat plain the rivers cross. The heights it
        // leaves are what the iterations erode, and D includes the carving and the filling. Scratch holds the
        // original heights the limit is measured from; it is free until the first diffusion sweep.
        static void Condition<TI, TF, TD>(in ErosionParams p, int nodeCount, ref ErosionArrays<TI, TF, TD> a)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            for (int i = 0; i < nodeCount; i++)
            {
                a.State.Set(i, 0);
                a.Scratch.Set(i, a.Heights.Get(i));
            }
            MarkSeas(p, nodeCount, ref a);

            int size = 0;
            int done = PlaceSeas(nodeCount, ref a, ref size, out int queued) + queued;

            while (done < nodeCount)
            {
                if (size == 0)
                {
                    SeedLowest(nodeCount, ref a, ref size);
                    done++;
                }

                int c = Pop(ref a, ref size);
                for (int k = 0; k < MaxNeighbours; k++)
                {
                    int m = a.Neighbours.Get(c * MaxNeighbours + k);
                    if (m < 0 || a.State.Get(m) != 0) continue;
                    a.State.Set(m, 1);
                    a.Receivers.Set(m, c);
                    done++;

                    // c's height as it stands: a carve through it may have lowered it since it was pushed.
                    double step = EpsilonSlope * a.Lengths.Get(c * MaxNeighbours + k);
                    double hc   = a.Heights.Get(c), hm = a.Heights.Get(m);
                    if (!(hm >= hc + step))
                    {
                        // No channel is carved below the base level, and a floor below it is raised to
                        // it: a hollow under sea level becomes a plain at sea level rather than an arm of
                        // the sea. Carved to their floors, the ring's hollows joined the ocean through
                        // their channels, and its rivers ran a median of 8 steps.
                        double floor = math.max(hm, p.BaseLevel + step);
                        if (floor < hc + step && Breach(p, ref a, c, floor - step))
                            hm = floor;
                        else
                            hm = hc + step;   // filled
                        a.Heights.Set(m, hm);
                    }
                    a.Filled.Set(m, hm);
                    Push(ref a, m, ref size);
                }
            }
        }

        // Lowers the chain from c towards the sea so that it runs strictly downhill from target, if no
        // node on it falls more than BreachDepth below its original height; true if the channel is
        // carved, false, changing nothing, if it cannot be. The chain ends at an outlet, which water
        // leaves whatever its height, or at a node already below its target.
        static bool Breach<TI, TF, TD>(in ErosionParams p, ref ErosionArrays<TI, TF, TD> a, int c, double target)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            // The chain descends by the flood's epsilon slope, never to the base level itself, where a
            // node would count as water and the channel would join the sea: it runs out level just
            // above it instead, a flat the flood gives its slope.
            double lowest = p.BaseLevel + EpsilonSlope;
            for (int pass = 0; pass < 2; pass++)
            {
                int    x = c;
                double t = math.max(target, lowest);
                while (true)
                {
                    int up = a.Receivers.Get(x);
                    if (up == x || a.Heights.Get(x) <= t) break;
                    if (pass == 0)
                    {
                        if (a.Scratch.Get(x) - t > p.BreachDepth) return false;
                    }
                    else
                    {
                        a.Heights.Set(x, t);
                        a.Filled.Set(x, t);
                    }
                    t  = math.max(t - EpsilonSlope * LinkLength(ref a, x, up), lowest);
                    x  = up;
                }
            }
            return true;
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

            MarkSeas(p, nodeCount, ref a);
            popped = PlaceSeas(nodeCount, ref a, ref size, out _);

            while (popped < nodeCount)
            {
                if (size == 0) SeedLowest(nodeCount, ref a, ref size);

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
                // Under a lake: the flood raised it above its ground. Incise reads the mark.
                if (a.Filled.Get(c) > a.Heights.Get(c)) a.State.Set(c, Lake);
            }
        }

        // Route's marks in State: unvisited 0, visited by the flood 1, an outlet placed 2, and before
        // the flood, Sea for a node of a water body that is an outlet and Measuring while its body is
        // walked. A body too small to be a sea goes back to 0 and the flood fills it like any hollow.
        const int Sea = 3, Measuring = 4;

        // After the flood, a node the flood raised above its ground: under a lake, or on a flat it
        // gave an epsilon slope. Incise cuts a river entering one down to its surface, not its floor.
        const int Lake = 5;

        // Every water body -- nodes at or below BaseLevel, connected -- walked breadth-first with the
        // heap's array as the queue (the heap is empty before the flood) and its area summed. A body
        // of at least MinOutletArea is sea. If none is, the largest is, so the water still leaves; a
        // surface with no water at all falls to Route's lowest-node rule. Bodies are found in node
        // order and ties go to the first, so the seas are the same in any build. At MinOutletArea 0
        // every body is sea, the rule before it existed, and the routing is the same bits.
        static void MarkSeas<TI, TF, TD>(in ErosionParams p, int nodeCount, ref ErosionArrays<TI, TF, TD> a)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            bool   anySea      = false;
            int    largestSeed = -1;
            double largestArea = -1.0;
            for (int i = 0; i < nodeCount; i++)
            {
                if (!(a.Heights.Get(i) <= p.BaseLevel) || a.State.Get(i) != 0) continue;
                double area = WalkBody(p, ref a, i, 0, Measuring, out int count);
                if (area >= p.MinOutletArea)
                {
                    for (int k = 0; k < count; k++) a.State.Set(a.Heap.Get(k), Sea);
                    anySea = true;
                }
                if (area > largestArea)
                {
                    largestArea = area;
                    largestSeed = i;
                }
            }
            if (!anySea && largestSeed >= 0)
            {
                WalkBody(p, ref a, largestSeed, Measuring, Sea, out int count);
                for (int k = 0; k < count; k++) a.State.Set(a.Heap.Get(k), Sea);
            }
            for (int i = 0; i < nodeCount; i++)
                if (a.State.Get(i) == Measuring) a.State.Set(i, 0);
        }

        // The water body containing seed: every connected node at or below BaseLevel whose State is
        // from, relabelled to, left in Heap[0 .. count) in walk order. Returns its area.
        static double WalkBody<TI, TF, TD>(in ErosionParams p, ref ErosionArrays<TI, TF, TD> a, int seed,
                                          int from, int to, out int count)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            double area = 0.0;
            int    head = 0;
            count = 0;
            a.Heap.Set(count++, seed);
            a.State.Set(seed, to);
            while (head < count)
            {
                int x = a.Heap.Get(head++);
                area += a.Areas.Get(x);
                for (int k = 0; k < MaxNeighbours; k++)
                {
                    int m = a.Neighbours.Get(x * MaxNeighbours + k);
                    if (m < 0 || a.State.Get(m) != from || !(a.Heights.Get(m) <= p.BaseLevel)) continue;
                    a.State.Set(m, to);
                    a.Heap.Set(count++, m);
                }
            }
            return area;
        }

        // An outlet beside a node that is not sea, which the flood has to start from: land, or a
        // hollow too small to be sea. Outlets already placed are marked 2, the rest Sea.
        static bool IsCoastal<TI, TF, TD>(ref ErosionArrays<TI, TF, TD> a, int node)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            for (int k = 0; k < MaxNeighbours; k++)
            {
                int m = a.Neighbours.Get(node * MaxNeighbours + k);
                if (m < 0) continue;
                int s = a.State.Get(m);
                if (s != Sea && s != 2) return true;
            }
            return false;
        }

        // The seas MarkSeas found, as outlets, for Route and Condition both. Only those beside ground
        // that is not sea go in the queue. One whose neighbours are all sea would come out of it to
        // find every neighbour visited and push nothing, so it goes straight onto the stack instead:
        // its own receiver, before any node that could drain to it. Nothing else reads its place in
        // the stack, so the routing, the drainage and the heights are the same bits either way, and
        // the queue is smaller by the sea -- 69% of the terrarium's nodes, about as much of the
        // planets'. Returns how many it placed on the stack, and how many it queued.
        static int PlaceSeas<TI, TF, TD>(int nodeCount, ref ErosionArrays<TI, TF, TD> a, ref int size, out int queued)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            int placed = 0;
            queued = 0;
            for (int i = 0; i < nodeCount; i++)
            {
                if (a.State.Get(i) != Sea) continue;
                if (IsCoastal(ref a, i))
                {
                    Seed(ref a, i, ref size);
                    queued++;
                }
                else
                {
                    a.Filled.Set(i, a.Heights.Get(i));
                    a.Receivers.Set(i, i);
                    a.State.Set(i, 2);
                    a.Stack.Set(placed++, i);
                }
            }
            return placed;
        }

        // No outlet yet, or ground the outlets do not reach: the lowest node left unvisited drains
        // it, ties to the lower index. Every surface the terrain has is connected, so past the first
        // flood this is only for a surface with no sea at all.
        static void SeedLowest<TI, TF, TD>(int nodeCount, ref ErosionArrays<TI, TF, TD> a, ref int size)
            where TI : struct, IErosionArray<int>
            where TF : struct, IErosionArray<float>
            where TD : struct, IErosionArray<double>
        {
            int lowest = -1;
            for (int i = 0; i < nodeCount; i++)
                if (a.State.Get(i) == 0 && (lowest < 0 || a.Heights.Get(i) < a.Heights.Get(lowest)))
                    lowest = i;
            Seed(ref a, lowest, ref size);
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

        // Step 4: implicit incision, downstream first, so each receiver is already this step's. A
        // river entering a lake is cut towards the lake's surface, the receiver's filled height, never
        // its floor: on the ring, cutting to the floor of hollows kilometres below sea level took the
        // deepest cut from 1,226 m to 3,868 m once rivers ran through them.
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
                double hi = a.Heights.Get(i);
                double hr = a.State.Get(r) == Lake ? a.Filled.Get(r) : a.Heights.Get(r);
                // Land is cut towards the sea's surface, never its floor: the base level is what a
                // river erodes to. Aimed at a sea node's own height, a river mouth beside deep water
                // was dragged under the base level and became sea, 1.4 to 3.3% of each scene's land.
                bool land = hi > p.BaseLevel;
                if (land) hr = math.max(hr, p.BaseLevel);
                if (!(hi > hr)) continue;
                double f = p.KDt * p.AreaPower(a.Drainage.Get(i)) / LinkLength(ref a, i, r);
                double h = (hi + f * hr) / (1.0 + f);
                a.Heights.Set(i, land ? math.max(h, p.BaseLevel + EpsilonSlope) : h);
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
                // Land is not diffused under the base level either, which a coast beside a deep
                // seabed would otherwise be.
                double next = h + 0.5 * p.DiffusionKDt * sum;
                a.Scratch.Set(i, h > p.BaseLevel ? math.max(next, p.BaseLevel + EpsilonSlope) : next);
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
