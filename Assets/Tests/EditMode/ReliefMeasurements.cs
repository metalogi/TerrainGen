using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The measurements a continent scene's pinned figures come from (Docs/Continents_Plan.md, K7 and
    // Task 7), kept as explicit tests so the next scene re-runs them unchanged: each one iterates
    // over every scene with continents, prints what it measures, and asserts nothing a retune could
    // break. Minutes each outside the Editor and far longer under its Mono, so run them through the
    // out-of-Editor harness. The pinned constants' comments say which of these produced them.
    [Explicit("Measurements for retuning, minutes a scene: run by name.")]
    public class ReliefMeasurements
    {
        const int Resolution = 33;

        // The detail noise's relief factor C at the scene's cutoff, by C10's method: 20,000 random
        // nodes per depth, every vertex of each, the bands at the amplitudes the scene's C is
        // measured at, and C = half-relief / (nominal node size x HeightScale / S0). The pinned C is
        // half again over the roughest. Printed beside the same measurement without the cutoff,
        // the figure the scene's C was pinned from before continents, as a check on the method.
        [Test]
        public void DetailReliefFactor()
        {
            foreach (var m in ContinentTests.ContinentScenes())
            {
                var rough = MacroTests.RoughParams(m.Scene);
                float3 at = MeasuredAt(m.Scene);
                foreach (bool cut in new[] { true, false })
                {
                    var hp = cut ? MacroTests.SceneParams(m, rough) : rough.WithBands(m.LowBandEdge, m.MidBandEdge);
                    double worst = 0.0; int worstDepth = 0;
                    for (int d = 0; d <= m.MaxDepth; d++)
                    {
                        double c = MaxOverNodes(m.Surface, d, 20000, (uint)(d + 1) * 7919u, (q, n) =>
                        {
                            var macro = new MacroSample { BandGain = at - 1f };
                            Relief(m.Surface, q, n, (p, _, _) => TerrainHeightFunction.Height(p, hp.MaxOctave(n.Depth), macro, hp),
                                   out double lo, out double hi);
                            return 0.5 * (hi - lo) / (LodMath.NominalSize(hp.S0, n.Depth) * hp.HeightScale / hp.S0);
                        });
                        TestContext.WriteLine($"{m.Scene} {(cut ? "cut" : "uncut")} depth {d}: roughest C {c:F2}");
                        if (c > worst) { worst = c; worstDepth = d; }
                    }
                    TestContext.WriteLine($"{m.Scene} {(cut ? "with the cutoff" : "without it")}: roughest C {worst:F1} at depth " +
                                          $"{worstDepth}; pinned half again over, {1.5 * worst:F0}");
                }
            }
        }

        // The macro layer's relief factor C_m on the real built maps, continent and fBm together:
        // 20,000 random nodes per depth, the base height read at the node's macro level at every
        // vertex, C_m = half-relief / (nominal node size x (ContinentBound + BaseHeightScale) / S0).
        [Test]
        public void MacroReliefFactor()
        {
            foreach (var m in ContinentTests.ContinentScenes())
            {
                var built = MacroTests.ContinentBuild(m);
                var roots = SurfaceMath.BuildRoots(m.Surface);
                var maps  = new MacroTests.ArrayTexels(MacroGenerator.Build(m.Surface, roots, built.Macro, built.Params, out _));
                var mp    = built.Macro;
                double s0 = MacroTests.RoughParams(m.Scene).S0, scale = mp.ContinentBound + mp.BaseHeightScale;
                double worst = 0.0; int worstDepth = 0;
                for (int d = 0; d <= m.MaxDepth; d++)
                {
                    int level = mp.Layout.MacroLevel(d);
                    double c = MaxOverNodes(m.Surface, d, 20000, (uint)(d + 1) * 104729u, (q, n) =>
                    {
                        Relief(m.Surface, q, n, (_, u, v) => MacroSampler.Read(maps, mp, n.Quad, level, u, v).BaseHeight,
                               out double lo, out double hi);
                        return 0.5 * (hi - lo) / (LodMath.NominalSize(s0, n.Depth) * scale / s0);
                    });
                    TestContext.WriteLine($"{m.Scene} depth {d}: roughest C_m {c:F2}");
                    if (c > worst) { worst = c; worstDepth = d; }
                }
                TestContext.WriteLine($"{m.Scene}: roughest C_m {worst:F2} at depth {worstDepth}; pinned half again over, " +
                                      $"{1.5 * worst:F1}, in units of (ContinentBound {mp.ContinentBound:F0} + BaseHeightScale " +
                                      $"{mp.BaseHeightScale:F0}) / S0");
            }
        }

        // The skirt share each side of a transient edge needs, net of SkirtDepth and its chord sag,
        // as a share of its own relief: 400 edges per depth and depth gap through
        // StressSceneTests.TransientEdges, the relief at every vertex as ChunkMeshJob measures it.
        // SkirtFraction is half again over the worst, rounded up to a tenth.
        [Test]
        public void SkirtShares()
        {
            foreach (var m in ContinentTests.ContinentScenes())
            {
                var w  = new StressSceneTests.SceneHeights(m.Scene, m.Surface, MacroTests.RoughParams(m.Scene)) { ReliefStride = 1 };
                var worst = new double[ChunkMeshLayout.SkirtDepthGap + 1];
                foreach (var e in StressSceneTests.TransientEdges(w, m.MaxDepth, 400, 0x5C1A7u))
                {
                    worst[e.Gap] = math.max(worst[e.Gap], Share(w, e.Depth, e.GapFine, e.ReliefFine));
                    worst[e.Gap] = math.max(worst[e.Gap], Share(w, e.Depth - e.Gap, e.GapCoarse, e.ReliefCoarse));
                }
                double all = 0.0;
                for (int g = 1; g < worst.Length; g++)
                {
                    TestContext.WriteLine($"{m.Scene} depth gap {g}: the worst side needs {worst[g]:F3} of its relief");
                    all = math.max(all, worst[g]);
                }
                TestContext.WriteLine($"{m.Scene}: worst share {all:F3}; SkirtFraction half again over, rounded up, " +
                                      $"{math.ceil(15.0 * all) / 10.0:F1}");
            }
        }

        static double Share(StressSceneTests.SceneHeights w, int depth, double gap, double relief)
        {
            double sag = ChunkMeshLayout.SkirtChordSag(w.Surface, 1f, LodMath.NominalSize(w.S0, depth), Resolution);
            double net = gap - StressSceneTests.SkirtDepth - sag;
            return net > 0.0 && relief > 0.0 ? net / relief : 0.0;
        }

        // LandFraction calibrated so the final terrain reaches the scene's target, by bisection on
        // 50,000 points, then the result checked on 100,000. The final land rises with the map's,
        // not quite smoothly, since each step moves whole micro cells.
        [Test]
        public void CalibrateLandFraction()
        {
            foreach (var m in ContinentTests.ContinentScenes())
            {
                var    rough = MacroTests.RoughParams(m.Scene);
                double lo = 0.2, hi = 0.5, best = m.Continents.Value.LandFraction;
                double bestMiss = double.MaxValue;
                for (int it = 0; it < 10; it++)
                {
                    double mid = 0.5 * (lo + hi);
                    var r = ProbeAt(m, rough, (float)mid, 50000, 0x5EA1EAFu);
                    TestContext.WriteLine($"{m.Scene} LandFraction {(float)mid:F4}: {r}");
                    if (math.abs(r.FinalLand - m.LandTarget) < bestMiss) { bestMiss = math.abs(r.FinalLand - m.LandTarget); best = mid; }
                    if (r.FinalLand < m.LandTarget) lo = mid; else hi = mid;
                }
                float chosen = (float)math.round(best * 1000.0) / 1000f;
                TestContext.WriteLine($"{m.Scene}: LandFraction {chosen:F3} on 100,000 points: {ProbeAt(m, rough, chosen, 100000, 0xC0FFEEu)}");
            }
        }

        static ContinentTests.ProbeResult ProbeAt(MacroTests.SceneMacro m, in HeightParams rough, float landFraction, int points, uint seed)
        {
            var c = m.Config;
            var k = c.Continents.Value; k.LandFraction = landFraction; c.Continents = k;
            var mp = MacroParams.Create(m.Surface, HeightParams.Create(m.Surface, Resolution, 0.0, 20, 1f, 0.5f, 2f, 42u), c);
            var cp = ContinentParams.Create(m.Surface, mp, c);
            var roots = SurfaceMath.BuildRoots(m.Surface);
            float[] channel = MacroGenerator.ContinentChannel(m.Surface, roots, mp, cp, out ContinentMap map);
            var hp = rough.WithBands(m.LowBandEdge, m.MidBandEdge).WithContinentalCutoff(k.DetailCutoff, k.CoarseOctaveAmplitude);
            return ContinentTests.Probe(m.Surface, m.MaxDepth, hp, mp, cp, channel, map, points, seed);
        }

        // The worst-case working sets the scenes pin, from the replica, flattened through the
        // scene's height envelope: Earth's held one at the face corner (EarthScaleTests) and its
        // fresh one there (LodSelectorTests), and each other continent scene's at its pinned camera.
        [Test]
        public void WorkingSets()
        {
            foreach (var m in ContinentTests.ContinentScenes())
            {
                if (m.Scene == "EarthStress")
                {
                    var s = EarthScaleTests.Earth; var roots = SurfaceMath.BuildRoots(s);
                    var lod = EarthScaleTests.Lod(EarthScaleTests.Params(EarthScaleTests.OctaveCount));
                    SurfaceMath.SurfaceFrame(s, roots[0], 0.0, 0.0, out double3 ground, out float3 up);
                    double3 cam = lod.Vertical.MapPoint(s, ground + (double3)up * 2.0);
                    TestContext.WriteLine($"EarthStress: envelope {lod.Vertical.Envelope / 1000.0:F1} km; held working set at the " +
                                          $"face corner {EarthScaleTests.Select(s, roots, lod, cam)}, fresh " +
                                          $"{EarthScaleTests.Select(s, roots, lod, cam, held: false)}");
                    continue;
                }
                var sc = Array.Find(StressSceneTests.All, x => x.Name == m.Scene);
                var l  = sc.Lod(sc.Params);
                SurfaceMath.SurfaceFrame(sc.Surface, sc.Roots[sc.WorstQuad], sc.WorstU, sc.WorstV, out double3 g, out float3 n);
                TestContext.WriteLine($"{m.Scene}: envelope {l.Vertical.Envelope / 1000.0:F1} km; worst working set " +
                                      $"{EarthScaleTests.Select(sc.Surface, sc.Roots, l, l.Vertical.MapPoint(sc.Surface, g + (double3)n * 2.0))}");
            }
        }

        static float3 MeasuredAt(string scene) => scene == "EarthStress"
            ? EarthScaleTests.EarthReliefMeasuredAt
            : Array.Find(StressSceneTests.All, x => x.Name == scene).ReliefMeasuredAt;

        // The largest of f over `count` random nodes at this depth, in parallel; each node's draw
        // depends on its index alone, and the maximum on no order, so the result is deterministic.
        static double MaxOverNodes(in SurfaceDef s, int depth, int count, uint seed, Func<RootQuad, NodeId, double> f)
        {
            var    roots = SurfaceMath.BuildRoots(s);
            int    span  = 1 << depth;
            double worst = 0.0;
            object gate  = new object();
            Parallel.For(0, count, () => 0.0, (k, _, local) =>
            {
                var rng  = Unity.Mathematics.Random.CreateFromIndex(seed + (uint)k * 2654435761u);
                int q    = rng.NextInt(roots.Length);
                var node = new NodeId(q, depth, rng.NextInt(span), rng.NextInt(span));
                return math.max(local, f(roots[q], node));
            }, local => { lock (gate) worst = math.max(worst, local); });
            return worst;
        }

        // The lowest and highest of h over every vertex of the node, as ChunkMeshJob measures a
        // chunk's relief.
        static void Relief(in SurfaceDef s, in RootQuad root, NodeId n, Func<double3, double, double, double> h, out double lo, out double hi)
        {
            lo = double.MaxValue; hi = double.MinValue;
            for (int j = 0; j < Resolution; j++)
            for (int i = 0; i < Resolution; i++)
            {
                ChunkGrid.VertexUV(n, i, j, Resolution, out double u, out double v);
                double x = h(SurfaceMath.SurfacePoint(s, root, u, v), u, v);
                lo = math.min(lo, x); hi = math.max(hi, x);
            }
        }
    }
}
