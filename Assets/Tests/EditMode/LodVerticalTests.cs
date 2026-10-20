using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // LodVertical: the flattened world LOD selection and the morph measure distance in, so that a
    // terrain with five times the relief spends the LOD relief allowance as the original did.
    //
    // Three claims, each tested here rather than argued: the identity is the old arithmetic to the
    // bit, so SampleScene and every figure measured on it stand; a flattened distance is never
    // longer than the true one, so the LOD it picks is never coarser than true distance would pick;
    // and, the one that matters, LOD boundaries over real terrain close at five times the relief
    // with the flattening and do not without it.
    //
    // UnityEngine-free, so it runs in the out-of-Editor harness.
    public class LodVerticalTests
    {
        static readonly (string Name, SurfaceDef Surface)[] Surfaces =
        {
            ("cube sphere", SurfaceDef.CubeSphere(6371000.0)),
            ("cylinder",    SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4)),
            ("plane grid",  SurfaceDef.PlaneGrid(1000.0, 3, 2)),
        };

        // ── The identity ─────────────────────────────────────────────────────

        // default(LodVertical), and a LodVerticalScale of 1, run NodeDistance as M5b shipped it,
        // to the bit -- held against a frozen copy rather than against the forwarding overload,
        // which would only be the code agreeing with itself. And they move no point.
        [Test]
        public void IdentityIsBitExact()
        {
            var rng = new Random(0x51A7u);
            foreach (var (name, s) in Surfaces)
            {
                var roots   = SurfaceMath.BuildRoots(s);
                var created = LodVertical.Create(s, 1f, 5000.0);
                Assert.IsTrue(created.IsIdentity, $"{name}: a vertical scale of 1 is the identity");
                Assert.IsTrue(default(LodVertical).IsIdentity, "default(LodVertical) is the identity");

                for (int t = 0; t < 2000; t++)
                {
                    int quad  = rng.NextInt(roots.Length);
                    int depth = rng.NextInt(0, 13);
                    var node  = new NodeId(quad, depth, rng.NextInt(1 << depth), rng.NextInt(1 << depth));

                    SurfaceMath.SurfaceFrame(s, roots[rng.NextInt(roots.Length)], rng.NextDouble(), rng.NextDouble(),
                                             out double3 foot, out float3 up);
                    double3 camera = foot + (double3)up * rng.NextDouble(-100.0, 1.0e5);
                    float   min    = rng.NextFloat(-3000f, 3000f);
                    float   max    = min + rng.NextFloat(0f, 3000f);

                    double expected = FrozenNodeDistance(s, roots[quad], node, camera, min, max);
                    Assert.AreEqual(expected, LodMath.NodeDistance(s, roots[quad], node, camera, min, max),
                        $"{name}: the plain overload has moved from M5b's arithmetic");
                    Assert.AreEqual(expected, LodMath.NodeDistance(s, roots[quad], node, camera, min, max, created),
                        $"{name}: LodVerticalScale 1 has moved from M5b's arithmetic");

                    Assert.AreEqual(camera, created.MapPoint(s, camera), $"{name}: the identity moved a point");
                    double h = rng.NextDouble(-5000.0, 5000.0);
                    Assert.AreEqual(h, created.MapHeight(h), $"{name}: the identity changed a height");
                    Assert.AreEqual(h, created.MapRelief(h), $"{name}: the identity changed a relief");
                }
            }
        }

        // LodMath.NodeDistance as M5b shipped it, frozen.
        static double FrozenNodeDistance(in SurfaceDef surface, in RootQuad root, NodeId node,
                                         double3 camera, float minHeight, float maxHeight)
        {
            SurfaceMath.SurfaceFrame(surface, root,
                0.5 * (node.UMin + node.UMax), 0.5 * (node.VMin + node.VMax),
                out double3 centre, out float3 normal);
            double radius = 0.5 * SurfaceMath.NodeWorldSize(surface, root, node);
            centre += (double3)normal * (0.5 * (minHeight + maxHeight));
            radius += 0.5 * (maxHeight - minHeight);
            return math.max(0.0, math.distance(camera, centre) - radius);
        }

        // ── Never coarser ────────────────────────────────────────────────────

        // Between a vertex anywhere in the envelope and a camera anywhere -- among the terrain, in
        // orbit, below the ground, on a cylinder's axis -- the flattened distance is never longer
        // than the true one. So the LOD the flattened world picks is never coarser than true
        // distance would pick at the same SplitFactor.
        //
        // The flattened vertex is also exactly what the shader computes, positionWS minus
        // Flatten (elevation - Anchor) topoUp: base point plus MapHeight(h) along the normal.
        [Test]
        public void FlattenedDistanceIsNeverLonger()
        {
            foreach (var (name, s) in Surfaces)
            foreach (float scale in new[] { 0.2f, 0.5f })
            {
                double envelope = s.Type == SurfaceType.Cylinder ? 2000.0 : 30000.0;
                var    v        = LodVertical.Create(s, scale, envelope);
                int    worse    = CountLonger(s, v, out double worst, checkShaderForm: true);
                Assert.AreEqual(0, worse,
                    $"{name} at {scale}: {worse} flattened distances are longer than the true one, " +
                    $"worst by {worst:E2} relative");
            }
        }

        // The anchor is what makes it so. At height 0 the map moves points outwards wherever the
        // terrain is below zero, which lengthens distances across a sphere a little; inside a
        // cylinder it also moves a camera near the axis a whole Flatten x envelope towards
        // whichever wall it happens to be nearer, which lengthens its distance to the far wall by
        // up to twice that. Both caught here, so the anchor rule cannot be tidied away.
        [Test]
        public void AnchorAtZeroWouldBeCoarser()
        {
            foreach (var (name, s) in Surfaces)
            {
                if (s.Type == SurfaceType.PlaneGrid) continue;   // a plane has no curvature to get wrong
                double envelope = s.Type == SurfaceType.Cylinder ? 2000.0 : 30000.0;
                var    zero     = new LodVertical { Flatten = 0.8, Envelope = envelope, Anchor = 0.0 };
                int    worse    = CountLonger(s, zero, out double worst, checkShaderForm: false);
                Assert.Greater(worse, 0, $"{name}: an anchor at zero should lengthen some distances");
                if (s.Type == SurfaceType.Cylinder)
                    Assert.Greater(worst, 0.05, $"{name}: near the axis an anchor at zero should be badly coarser");
            }
        }

        static int CountLonger(in SurfaceDef s, in LodVertical v, out double worst, bool checkShaderForm)
        {
            var rng   = new Random(0xC0A75Eu);
            var roots = SurfaceMath.BuildRoots(s);
            int worse = 0;
            worst = 0.0;

            // Up to the axis inside a cylinder; well past orbit outside a sphere; a long way up
            // over a plane.
            double top = s.Type == SurfaceType.Cylinder ? s.Radius : s.Type == SurfaceType.CubeSphere ? 3.0 * s.Radius : 1.0e6;

            for (int t = 0; t < 20000; t++)
            {
                SurfaceMath.SurfaceFrame(s, roots[rng.NextInt(roots.Length)], rng.NextDouble(), rng.NextDouble(),
                                         out double3 foot, out float3 n);
                double  h      = rng.NextDouble(-v.Envelope, v.Envelope);
                double3 vertex = foot + (double3)n * h;

                // Cameras near the vertex as well as across the world: most of the LOD is decided
                // within a few node sizes.
                double3 camera;
                if ((t & 1) == 0)
                {
                    SurfaceMath.SurfaceFrame(s, roots[rng.NextInt(roots.Length)], rng.NextDouble(), rng.NextDouble(),
                                             out double3 cfoot, out float3 cn);
                    double a = (t & 2) == 0 ? rng.NextDouble(-2.0 * v.Envelope, 2.0 * v.Envelope)
                                            : rng.NextDouble(-2.0 * v.Envelope, top);
                    camera = cfoot + (double3)cn * a;
                }
                else
                    camera = vertex + rng.NextDouble3Direction() * rng.NextDouble(0.1, 4.0 * v.Envelope);

                double trueDist = math.distance(camera, vertex);
                double flatDist = math.distance(v.MapPoint(s, camera), v.MapPoint(s, vertex));
                double excess   = (flatDist - trueDist) / math.max(trueDist, 1.0);
                if (excess > 1e-12)
                {
                    worse++;
                    worst = math.max(worst, excess);
                }

                if (checkShaderForm)
                {
                    // The shader moves along topoUp, the float normal; MapPoint along the exact one,
                    // by the altitude it measures, which the float normal leaves about 1e-7 of the
                    // height off h. Both are a tenth of a millimetre over kilometres, and that is
                    // all they differ by.
                    double3 shader    = vertex - (double3)n * (v.Flatten * (h - v.Anchor));
                    double  tolerance = 1e-6 * (math.abs(h) + math.abs(v.Anchor)) + 1e-9 * math.max(1.0, s.Radius);
                    Assert.AreEqual(0.0, math.distance(shader, v.MapPoint(s, vertex)), tolerance,
                        "the flattened vertex is not what the shader computes from topoUp and elevation");
                    Assert.AreEqual(0.0, math.distance(shader, foot + (double3)n * v.MapHeight(h)), tolerance,
                        "the flattened vertex is not its base point plus MapHeight along the normal");
                }
            }
            return worse;
        }

        // ── Boundaries close ─────────────────────────────────────────────────

        // The test that matters. LodBoundaryTests checks the morph ranges against a selected tree
        // with the relief stood in for by a pad; this selects over REAL terrain, every node padded
        // by its own measured height range as the selector pads it, and checks each LOD boundary
        // at the vertices the shader actually morphs: every fine edge vertex against the coarse
        // vertices its morph target lands between, each at its own band limit and its own height,
        // flattened as the shader flattens it. Two depths agree when depth - k agrees; two depths
        // apart is a crack outright.
        //
        // The base terrain fits the allowance unflattened and every boundary closes. Five times it,
        // flattened by 0.2, spends exactly the allowance the base terrain did -- the roughest node
        // selected over sits at the same fraction of it -- and every boundary closes again.
        [Test]
        public void FlattenedBoundariesCloseAtFiveTimesTheRelief()
        {
            foreach (var (name, s) in BoundarySurfaces)
            {
                double hs = BaseHeightScale(s);

                var fits = Simulate(s, hs, 1f);
                Assert.Less(fits.WorstRelief, 1.0, $"{name}: the base terrain should fit the allowance unflattened");
                Assert.AreEqual(0, fits.Bad, $"{name}: {fits.Bad} of {fits.Total} boundary samples open at the base relief, unflattened");

                var flat = Simulate(s, 5.0 * hs, 0.2f);
                Assert.AreEqual(fits.WorstRelief, flat.WorstRelief, 0.01 * fits.WorstRelief,
                    $"{name}: five times the relief, flattened by 0.2, should spend the allowance the base relief did");
                Assert.AreEqual(0, flat.Bad, $"{name}: {flat.Bad} of {flat.Total} boundary samples open at five times the relief, flattened");

                Assert.Greater(fits.Total, 2000, $"{name}: too few cross-depth samples for this to mean anything");
                Assert.Greater(flat.Total, 2000, $"{name}: too few cross-depth samples for this to mean anything");
            }
        }

        // The other direction: the same five times the relief, unflattened, overruns the allowance,
        // so the boundary proof no longer covers it -- which is what makes LodVerticalScale worth
        // having.
        //
        // Deliberately not an assertion that boundaries open. The bound is a worst case stacked on
        // one boundary -- the widest node-size spread, full hysteresis and the roughest relief --
        // and past it a crack is a matter of where the camera happens to be: these 18 cameras open
        // none. Cameras ringed at the split thresholds of the 60 roughest nodes do (2026-10-19,
        // 6,720 of them, this simulation at twice this test's height scale): 72 of 17.4M samples
        // on the plane at 2.0x the allowance, and 9,251 of 18.6M on the sphere at 4.7x, against
        // none at all flattened by 0.2, over 16.6M and 24.5M. That run takes minutes, so the test
        // keeps to the bound, and the runtime warning reports an overrun as it happens.
        [Test]
        public void UnflattenedFiveTimesTheReliefOverrunsTheAllowance()
        {
            foreach (var (name, s) in BoundarySurfaces)
            {
                var sharp = Simulate(s, 5.0 * BaseHeightScale(s), 1f);
                Assert.Greater(sharp.WorstRelief, 1.0, $"{name}: five times the relief should overrun the allowance unflattened");
            }
        }

        // -- The simulation -----------------------------------------------------

        // Earth's sphere, as LodBoundaryTests uses, and a plane grid, which has no curvature to
        // confuse the relief with.
        static readonly (string Name, SurfaceDef Surface)[] BoundarySurfaces =
        {
            ("cube sphere", SurfaceDef.CubeSphere(6371000.0)),
            ("plane grid",  SurfaceDef.PlaneGrid(100000.0, 2, 2)),
        };

        // Small chunks keep it fast; the boundary proof does not care how many vertices a chunk
        // has, only that each lies inside its node's sphere.
        const int   Resolution  = 5;
        const int   MaxDepth    = 8;
        const float SplitFactor = 4f;
        const float MorphStart  = 0.15f;
        const float Hysteresis  = 1.1f;

        // The stress scenes' mountain persistence, which is what makes relief grow against the node
        // as the depth does -- and lets the relief reach the allowance at heights a few percent of
        // a sphere's radius rather than tens.
        const float Persistence = 0.7f;

        // A height scale whose relief sits under the allowance unflattened -- the roughest node
        // selected over at 0.48 of it on the sphere and 0.23 on the plane -- and so overruns it at
        // five times, 2.4x and 1.1x. As a fraction of the root size, since C is scale-free.
        static double BaseHeightScale(in SurfaceDef s) => 0.008 * S0(s);

        static double S0(in SurfaceDef s)
            => SurfaceMath.NodeWorldSize(s, SurfaceMath.BuildRoots(s)[0], new NodeId(0, 0, 0, 0));

        struct Outcome { public long Total, Bad; public double WorstRelief; }

        static Outcome Simulate(in SurfaceDef s, double heightScale, float verticalScale)
        {
            var roots = SurfaceMath.BuildRoots(s);
            var p     = HeightParams.Create(s, Resolution, 0.0, 20, (float)heightScale, Persistence, 2f, 42u);
            var v     = LodVertical.Create(s, verticalScale, TerrainHeightFunction.MaxAbsHeight(p, MacroParams.Disabled));
            var lod   = LodMath.Create(s, p, SplitFactor, MorphStart, Hysteresis, 1.5f, MaxDepth).WithVertical(v);
            var sim   = new Sim(s, roots, p, lod);

            // LodBoundaryTests' cameras -- a face centre, an off-centre point, hard against a corner,
            // both hysteresis states -- at the lower three of its altitudes as fractions of the
            // deepest node: on Earth 100 m, 2 km and 50 km. Above the ground under them, which is
            // kilometres from the base surface at these height scales.
            var outcome = new Outcome();
            foreach (float h in new[] { 1.0f, Hysteresis })
            foreach (double clearance in new[] { 0.0025, 0.05, 1.25 })
            foreach (var uv in new[] { (0.5, 0.5), (0.31, 0.77), (0.02, 0.98) })
            {
                SurfaceMath.SurfaceFrame(s, roots[0], uv.Item1, uv.Item2, out double3 foot, out float3 n);
                double ground  = TerrainHeightFunction.Height(foot, p.MaxOctave(MaxDepth), MacroSample.Zero, p);
                double above   = clearance * lod.NominalSize(MaxDepth);
                double3 camera = foot + (double3)n * (ground + above);
                sim.Select(v.MapPoint(s, camera), h);
                sim.Walk(ref outcome);
            }
            outcome.WorstRelief = sim.WorstRelief;
            return outcome;
        }

        sealed class Sim
        {
            readonly SurfaceDef   _s;
            readonly RootQuad[]   _roots;
            readonly HeightParams _p;
            readonly LodMath      _lod;
            readonly Dictionary<NodeId, (float min, float max)> _relief = new Dictionary<NodeId, (float, float)>();
            readonly HashSet<NodeId> _leaves = new HashSet<NodeId>();
            double3 _camera;
            float   _hysteresis;

            // The roughest node selected over, as a fraction of what the allowance lets it carry:
            // its flattened half-relief over LodMath.MaxHalfRelief.
            public double WorstRelief;

            public Sim(in SurfaceDef s, RootQuad[] roots, in HeightParams p, in LodMath lod)
            {
                _s = s; _roots = roots; _p = p; _lod = lod;
            }

            public void Select(double3 flatCamera, float hysteresis)
            {
                _camera     = flatCamera;
                _hysteresis = hysteresis;
                _leaves.Clear();
                for (int q = 0; q < _roots.Length; q++) Descend(new NodeId(q, 0, 0, 0));
            }

            void Descend(NodeId node)
            {
                var r = Relief(node);
                WorstRelief = math.max(WorstRelief,
                    _lod.Vertical.MapRelief(0.5 * (r.max - r.min)) / _lod.MaxHalfRelief(node.Depth));

                double dist = LodMath.NodeDistance(_s, _roots[node.Quad], node, _camera, r.min, r.max, _lod.Vertical);
                if (node.Depth >= MaxDepth || !(dist < _lod.SplitDistance(node.Depth) * _hysteresis))
                {
                    _leaves.Add(node);
                    return;
                }
                for (int i = 0; i < 4; i++) Descend(node.Child(i));
            }

            // The height range over the node's own vertices at its own band limit, as
            // ChunkMeshJob measures it and the selector pads with.
            (float min, float max) Relief(NodeId node)
            {
                if (_relief.TryGetValue(node, out var r)) return r;
                float min = float.MaxValue, max = float.MinValue;
                for (int j = 0; j < Resolution; j++)
                for (int i = 0; i < Resolution; i++)
                {
                    ChunkGrid.VertexUV(node, i, j, Resolution, out double u, out double v);
                    float h = Height(node, u, v, node.Depth, out _);
                    min = math.min(min, h);
                    max = math.max(max, h);
                }
                return _relief[node] = (min, max);
            }

            float Height(NodeId node, double u, double v, int depth, out double3 vertex)
            {
                SurfaceMath.SurfaceFrame(_s, _roots[node.Quad], u, v, out double3 foot, out float3 n);
                float h = TerrainHeightFunction.Height(foot, _p.MaxOctave(depth), MacroSample.Zero, _p);
                vertex = foot + (double3)(n * h);
                return h;
            }

            // Every fine edge vertex against the coarse vertices its morph target lands between,
            // where a leaf meets a leaf one depth coarser inside one root quad. A fine vertex at an
            // even index is a coarse vertex too; one at an odd index sits between two, and has to
            // be fully morphed while both are not morphed at all.
            public void Walk(ref Outcome outcome)
            {
                const double eps = 1e-9;
                int last = Resolution - 1;

                foreach (var leaf in _leaves)
                for (int edge = 0; edge < 4; edge++)
                for (int t = 0; t < Resolution; t++)
                {
                    int i = edge == 1 ? last : edge == 3 ? 0 : t;
                    int j = edge == 0 ? 0 : edge == 2 ? last : t;
                    ChunkGrid.VertexUV(leaf, i, j, Resolution, out double u, out double v);
                    double ou = edge == 1 ? u + eps : edge == 3 ? u - eps : u;
                    double ov = edge == 0 ? v - eps : edge == 2 ? v + eps : v;
                    // Skip the corners' diagonal ambiguity: along an edge only.
                    if (t == 0 || t == last) continue;

                    if (!LeafAt(leaf.Quad, ou, ov, out var coarse)) continue;   // root quad edge
                    if (coarse.Depth >= leaf.Depth) continue;                  // same depth, or seen from the other side

                    // Two depths apart in a settled selection is a crack no morph can close.
                    if (coarse.Depth < leaf.Depth - 1)
                    {
                        outcome.Total++;
                        outcome.Bad++;
                        continue;
                    }

                    Height(leaf, u, v, leaf.Depth, out double3 fine);
                    double a = leaf.Depth - K(fine, leaf.Depth);

                    for (int side = 0; side < 2; side++)
                    {
                        int te = side == 0 ? (t & ~1) : ((t + 1) & ~1);
                        int ie = edge == 1 ? last : edge == 3 ? 0 : te;
                        int je = edge == 0 ? 0 : edge == 2 ? last : te;
                        ChunkGrid.VertexUV(leaf, ie, je, Resolution, out double ue, out double ve);
                        Height(leaf, ue, ve, coarse.Depth, out double3 coarseVertex);
                        double b = coarse.Depth - K(coarseVertex, coarse.Depth);

                        outcome.Total++;
                        if (math.abs(a - b) > 1e-9) outcome.Bad++;
                    }
                }
            }

            // The shader's k: the vertex flattened as SonomaMorphFactor flattens it, measured from
            // the flattened camera.
            double K(double3 vertex, int depth)
                => _lod.MorphFactor(math.distance(_lod.Vertical.MapPoint(_s, vertex), _camera), depth);

            bool LeafAt(int quad, double u, double v, out NodeId leaf)
            {
                leaf = default;
                if (u < 0.0 || u > 1.0 || v < 0.0 || v > 1.0) return false;
                for (int d = 0; d <= MaxDepth; d++)
                {
                    int span = 1 << d;
                    var node = new NodeId(quad, d, math.clamp((int)(u * span), 0, span - 1),
                                                    math.clamp((int)(v * span), 0, span - 1));
                    if (_leaves.Contains(node)) { leaf = node; return true; }
                }
                return false;
            }
        }
    }
}
