using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // Whether the terrain cracks, reduced to one number.
    //
    // A chunk at depth d with morph factor k draws the geometry of depth `d - k`: at k = 1 it
    // has fully morphed onto its parent and is band-limited exactly as the parent is. So the
    // condition for two chunks meeting at a surface point to meet *exactly* is
    //
    //     depth_A - k_A  ==  depth_B - k_B
    //
    // Same-depth neighbours satisfy it for free, because k is a function of the vertex's own
    // distance and both sides compute the same distance for a shared vertex. Cross-depth
    // neighbours satisfy it only when the fine side has reached k = 1 and the coarse side has
    // not yet left k = 0, which is what LodMath's ranges exist to arrange.
    //
    // This is the test M3b was missing. Everything in LodMathTests checks the ranges against
    // each other; nothing checked them against an actual selected tree, and the range
    // definition was off by one level in a way that passed every one of those checks while
    // putting 100% of real boundaries exactly 1.0 effective LOD apart.
    //
    // UnityEngine-free, so it runs in the out-of-Editor harness.
    public class LodBoundaryTests
    {
        const double EarthRadius = 6371000.0;
        const int    Resolution  = 33;
        const int    MaxDepth    = 8;

        // The shipped defaults, kept in step with TerrainSettings.
        const float SplitFactor = 4f;
        const float MorphStart  = 0.15f;
        const float Hysteresis  = 1.1f;

        SurfaceDef _surface;
        RootQuad[] _roots;
        LodMath    _lod;
        double3    _camera;
        float      _hysteresis;

        readonly HashSet<NodeId> _leaves = new HashSet<NodeId>();

        [Test]
        public void EveryLodBoundaryAgreesOnEffectiveLod()
        {
            Setup(SurfaceDef.CubeSphere(EarthRadius), SplitFactor, MorphStart, Hysteresis);

            long total = 0, bad = 0;
            double worst = 0.0;
            string where = "";

            foreach (var state in States())
            {
                Select(state.camera, state.hysteresis);
                Walk((leaf, other, distance) =>
                {
                    total++;
                    double a = leaf.Depth  - _lod.MorphFactor(distance, leaf.Depth);
                    double b = other.Depth - _lod.MorphFactor(distance, other.Depth);
                    double diff = math.abs(a - b);
                    if (diff > 1e-9) bad++;
                    if (diff > worst)
                    {
                        worst = diff;
                        where = $"depth {leaf.Depth} (k={_lod.MorphFactor(distance, leaf.Depth):F3}) " +
                                $"against depth {other.Depth} " +
                                $"(k={_lod.MorphFactor(distance, other.Depth):F3})";
                    }
                });
            }

            Assert.Greater(total, 10000,
                "too few cross-depth boundaries sampled for this to mean anything");
            Assert.AreEqual(0, bad,
                $"{bad} of {total} cross-depth boundary samples disagree on effective LOD, " +
                $"worst {worst:F4} at {where}. Every one of those is a visible seam.");
        }

        // The mutation: end_d taken as the node's OWN split distance rather than its parent's,
        // which is how SonomaRevisedPlan.md section 4.4 and the M3 plan both word it.
        //
        // A chunk then finishes morphing where it hands over to its children instead of where
        // its parent takes over, so at a boundary both sides are saturated at k = 1 and are a
        // full level apart. Reproduced here rather than described, because every assertion in
        // LodMathTests passes under it.
        [Test]
        public void OwnSplitDistanceAsMorphEndBreaksEveryBoundary()
        {
            Setup(SurfaceDef.CubeSphere(EarthRadius), SplitFactor, MorphStart, Hysteresis);

            long total = 0, bad = 0;
            double worst = 0.0;

            foreach (var state in States())
            {
                Select(state.camera, state.hysteresis);
                Walk((leaf, other, distance) =>
                {
                    total++;
                    double a = leaf.Depth  - MutatedMorphFactor(distance, leaf.Depth);
                    double b = other.Depth - MutatedMorphFactor(distance, other.Depth);
                    double diff = math.abs(a - b);
                    if (diff > 1e-9) bad++;
                    worst = math.max(worst, diff);
                });
            }

            Assert.AreEqual(total, bad,
                "the off-by-one should break every single cross-depth boundary, not some of them");
            Assert.AreEqual(1.0, worst, 1e-9,
                "the off-by-one should put boundaries exactly one effective LOD apart");
        }

        // LodMath.MorphFactor with end_d taken as SplitDistance(d) instead of SplitDistance(d-1).
        double MutatedMorphFactor(double distance, int depth)
        {
            double end   = _lod.SplitDistance(depth);
            double start = end * (1.0 - MorphStart);
            return math.saturate((distance - start) / (end - start));
        }

        // -- Harness -----------------------------------------------------------

        void Setup(in SurfaceDef surface, float splitFactor, float morphStart, float hysteresis)
        {
            _surface = surface;
            _roots   = SurfaceMath.BuildRoots(_surface);
            var p    = HeightParams.Create(_surface, Resolution, 0.0, 20, 50f, 0.5f, 2f, 42u);
            _lod     = LodMath.Create(_surface, p, splitFactor, morphStart, hysteresis, 1.5f, MaxDepth);
        }

        // Camera positions chosen to put boundaries in awkward places: a face centre, an
        // off-centre point, and hard against a face corner where node size distortion peaks.
        // Both hysteresis states, because a node held split by hysteresis reaches further out
        // than the split distance alone allows, and that is what tightens the bound.
        IEnumerable<(double3 camera, float hysteresis)> States()
        {
            foreach (float h in new[] { 1.0f, Hysteresis })
            foreach (double altitude in new[] { 100.0, 2000.0, 50000.0, 800000.0 })
            foreach (var uv in new[] { (0.5, 0.5), (0.31, 0.77), (0.02, 0.98) })
            {
                double3 surface = SurfaceMath.SurfacePoint(_surface, _roots[0], uv.Item1, uv.Item2);
                yield return (surface * (1.0 + altitude / EarthRadius), h);
            }
        }

        // The selection LodSelector performs, reduced to what decides geometry: split while
        // the bounding-sphere distance is inside the split distance, and hysteresis widens
        // that for a node that is already split.
        void Select(double3 camera, float hysteresis)
        {
            _camera     = camera;
            _hysteresis = hysteresis;
            _leaves.Clear();
            for (int q = 0; q < _roots.Length; q++) Descend(new NodeId(q, 0, 0, 0), 0);
        }

        void Descend(NodeId node, int depth)
        {
            if (depth >= MaxDepth || !(NodeDistance(node) < _lod.SplitDistance(depth) * _hysteresis))
            {
                _leaves.Add(node);
                return;
            }
            for (int i = 0; i < 4; i++) Descend(node.Child(i), depth + 1);
        }

        // LodSelector.NodeDistance, including the terrain padding it applies to the bounding
        // sphere -- which this modelled without until it was noticed that the selector pads and
        // this did not, so the one test that checks the ranges against a real tree was checking
        // a selector nobody ships.
        //
        // The pad here is exactly LodMath.MaxHalfRelief: the most relief the configuration
        // claims to tolerate at that depth. That makes this the check on the claim. If the
        // inversion in MaxHalfRelief is wrong in the unsafe direction, boundaries opened at
        // precisely the relief it permits, and EveryLodBoundaryAgreesOnEffectiveLod fails.
        //
        // No mid-elevation offset, because no meshes are generated here and every node would
        // shift by the same amount anyway; only the radius term affects the split decision.
        double NodeDistance(NodeId node)
        {
            var     root   = _roots[node.Quad];
            double3 centre = SurfaceMath.SurfacePoint(_surface, root,
                                 0.5 * (node.UMin + node.UMax), 0.5 * (node.VMin + node.VMax));
            double  radius = 0.5 * SurfaceMath.NodeWorldSize(_surface, root, node)
                             + math.max(0.0, _lod.MaxHalfRelief(node.Depth));
            return math.max(0.0, math.distance(_camera, centre) - radius);
        }

        // Every point where a leaf meets a leaf of a different depth, inside one root quad.
        // Cross-quad boundaries are excluded: two faces reach a shared cube edge by different
        // parameterisations, which is a separate, bounded discrepancy that skirts cover.
        void Walk(System.Action<NodeId, NodeId, double> visit)
        {
            const double eps = 1e-9;

            foreach (var leaf in _leaves)
            {
                var root = _roots[leaf.Quad];

                for (int edge = 0; edge < 4; edge++)
                for (int step = 1; step < 16; step++)
                {
                    double f = step / 16.0;
                    double u, v, ou, ov;
                    switch (edge)
                    {
                        case 0:  u = math.lerp(leaf.UMin, leaf.UMax, f); v = leaf.VMin;
                                 ou = u; ov = v - eps; break;
                        case 1:  u = leaf.UMax; v = math.lerp(leaf.VMin, leaf.VMax, f);
                                 ou = u + eps; ov = v; break;
                        case 2:  u = math.lerp(leaf.UMin, leaf.UMax, f); v = leaf.VMax;
                                 ou = u; ov = v + eps; break;
                        default: u = leaf.UMin; v = math.lerp(leaf.VMin, leaf.VMax, f);
                                 ou = u - eps; ov = v; break;
                    }

                    if (!LeafAt(leaf.Quad, ou, ov, out var other)) continue;   // root quad edge
                    if (other.Depth == leaf.Depth) continue;                   // agrees for free

                    double distance = math.distance(_camera,
                                          SurfaceMath.SurfacePoint(_surface, root, u, v));
                    visit(leaf, other, distance);
                }
            }
        }

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
