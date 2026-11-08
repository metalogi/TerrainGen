using System;
using System.Collections.Generic;
using System.Globalization;
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
    // break. The pinned constants' comments say which of these produced them, and each tool prints a
    // PIN line per constant it measures (Measuring.Pin).
    //
    // In the Editor, run them from Tools > Sonoma > Measure Open Scene, which measures the open
    // scene's continent asset and writes what they print to Logs/Measurements; the eroded scans and
    // the eroded build then run in Burst (Measuring.Fast). Out of the Editor, set SONOMA_MEASURE=1 and
    // run them through the harness, where every scan runs managed.
    [Explicit("Measurements for retuning, minutes a scene: run by name.")]
    [Timeout(24 * 60 * 60 * 1000)]   // the Test Runner's default is 180 s, which several of these exceed
    public class ReliefMeasurements
    {
        const int Resolution = 33;

        [SetUp]
        public void OnlyWhenAsked() => ContinentTests.MeasuringToolsOnly();

        [OneTimeTearDown]
        public void PutTheCopiesBack() => Measuring.Restore();

        // The detail noise's relief factor C at the scene's cutoff, by C10's method: 20,000 random
        // nodes per depth, every vertex of each, the bands at the amplitudes the scene's C is
        // measured at, and C = half-relief / (nominal node size x HeightScale / S0). The pinned C is
        // half again over the roughest. Printed beside the same measurement without the cutoff,
        // the figure the scene's C was pinned from before continents, as a check on the method.
        [Test]
        public void DetailReliefFactor()
        {
            foreach (var m in ContinentTests.MeasuredScenes())
            {
                var rough = MacroTests.RoughParams(m.Scene);
                float3 at = MeasuredAt(m.Scene);
                foreach (bool cut in new[] { true, false })
                {
                    var hp = cut ? MacroTests.SceneParams(m, rough) : rough.WithBands(m.LowBandEdge, m.MidBandEdge);
                    double worst = 0.0; int worstDepth = 0;
                    for (int d = 0; d <= m.MaxDepth; d++)
                    {
                        StepDepth(m, d, cut ? 0f : 0.5f, 0.5f);
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
        // A scene that erodes pins ErodedMacroReliefFactor's figure instead.
        [Test]
        public void MacroReliefFactor()
        {
            foreach (var m in ContinentTests.MeasuredScenes())
            {
                var built = MacroTests.ContinentBuild(m);
                var roots = SurfaceMath.BuildRoots(m.Surface);
                var maps  = new MacroTests.ArrayTexels(MacroGenerator.Build(m.Surface, roots, built.Macro, built.Params, out _));
                var mp    = built.Macro;
                double s0 = MacroTests.RoughParams(m.Scene).S0, scale = mp.ContinentBound + mp.BaseHeightScale;
                double worst = 0.0; int worstDepth = 0;
                for (int d = 0; d <= m.MaxDepth; d++)
                {
                    StepDepth(m, d);
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
                if (!m.Erosion.HasValue) PinReliefFactor(m, worst);
            }
        }

        // The skirt share each side of a transient edge needs, net of SkirtDepth and its chord sag,
        // as a share of its own relief: 400 edges per depth and depth gap through
        // StressSceneTests.TransientEdges, the relief at every vertex as ChunkMeshJob measures it.
        // SkirtFraction is half again over the worst, rounded up to a tenth. A scene that erodes pins
        // ErodedSkirtShares' figure instead.
        [Test]
        public void SkirtShares()
        {
            foreach (var m in ContinentTests.MeasuredScenes())
            {
                var w = new StressSceneTests.SceneHeights(m.Scene, m.Surface, MacroTests.RoughParams(m.Scene)) { ReliefStride = 1 };
                double all = WorstShares(m, w, "");
                if (!m.Erosion.HasValue) PinSkirtFraction(m, all);
            }
        }

        // ── Erosion (Docs/M5_MacroLayer_Plan.md, E5) ──
        //
        // The same measurements on each eroding scene's built eroded maps (MacroTests.ErodedBuild,
        // seconds a scene), since erosion is not local and the lazy texels the others read cannot
        // carry it.

        // The stress scenes with erosion, SONOMA_SCENES limiting them as for MeasuredScenes.
        static IEnumerable<MacroTests.SceneMacro> ErodedScenes()
        {
            string only = Environment.GetEnvironmentVariable(Measuring.ScenesVar);
            var    names = string.IsNullOrWhiteSpace(only) ? null : new HashSet<string>(only.Split(','));
            foreach (var m in MacroTests.StressMacros)
                if (m.Erosion.HasValue && (names == null || names.Contains(m.Scene))) yield return m;
        }

        // The largest |BaseHeight'| each eroded build stores, to pin as SceneMacro.ErodedMaxAbsBaseHeight
        // (printed to the bit), and the envelope it gives against the analytic one.
        [Test]
        public void ErodedMaps()
        {
            foreach (var m in ErodedScenes())
            {
                Measuring.Step($"{m.Scene}: building the eroded maps", 0f, 1f);
                var    built    = MacroTests.ErodedBuild(m);
                double analytic = TerrainHeightFunction.MaxAbsHeight(built.Height, m.Params);
                double eroded   = TerrainHeightFunction.MaxAbsHeight(built.Height, built.Macro, built.Stats.MaxAbsBaseHeight);
                TestContext.WriteLine($"{m.Scene}: largest |BaseHeight'| {built.Stats.MaxAbsBaseHeight:R} m; envelope " +
                                      $"{analytic:F0} m analytic, {eroded:F0} m eroded; deepest cut {built.Stats.MaxCut:F0} m");
                Measuring.Pin(m.Scene, "ErodedMaxAbsBaseHeight", "exact", R(built.Stats.MaxAbsBaseHeight), R(m.ErodedMaxAbsBaseHeight));
            }
        }

        // C_m on the eroded maps, in the same units as the pinned one: (ContinentBound +
        // BaseHeightScale) / S0, BaseHeightScale alone without continents.
        [Test]
        public void ErodedMacroReliefFactor()
        {
            foreach (var m in ErodedScenes())
            {
                var built = MacroTests.ErodedBuild(m);
                double worst = 0.0; int worstDepth = 0;
                for (int d = 0; d <= m.MaxDepth; d++)
                {
                    StepDepth(m, d);
                    double c = Max(Measuring.Fast != null
                        ? Measuring.Fast.MacroRelief(m, built, d, 20000, MacroReliefSeed(d))
                        : MacroReliefNodes(m, built, d, 20000, MacroReliefSeed(d)));
                    TestContext.WriteLine($"{m.Scene} eroded depth {d}: roughest C_m {c:F2}");
                    if (c > worst) { worst = c; worstDepth = d; }
                }
                TestContext.WriteLine($"{m.Scene} eroded: roughest C_m {worst:F2} at depth {worstDepth}; pinned half again over, " +
                                      $"{1.5 * worst:F1}; pinned uneroded {m.ReliefFactor}");
                PinReliefFactor(m, worst);
            }
        }

        // The relief the LOD boundary has to cover on an eroded scene, measured whole: detail and
        // eroded base height together, as ChunkMeshJob measures a chunk, 20,000 random nodes per depth,
        // the largest half-relief as a fraction of the node. Erosion couples the two -- its correction
        // was computed from the detail octaves, and the steepest channels lie where detail was cut
        // away -- so summing each one's roughest node (MacroTests.Demand) overstates it: 0.556 of a
        // node on the terrarium against a measured 0.25 of its allowance. Unflattened; the tests scale
        // it by LodVerticalScale. Pinned half again over the roughest as ErodedCombinedRelief.
        [Test]
        public void ErodedCombinedRelief()
        {
            foreach (var m in ErodedScenes())
            {
                var rough = MacroTests.RoughParams(m.Scene);
                var built = MacroTests.ErodedBuild(m);
                var w     = new StressSceneTests.SceneHeights(m.Scene, m.Surface, rough, eroded: true) { ReliefStride = 1 };
                double worst = 0.0; int worstDepth = 0;
                for (int d = 0; d <= m.MaxDepth; d++)
                {
                    StepDepth(m, d);
                    double c = Max(Measuring.Fast != null
                        ? Measuring.Fast.CombinedRelief(m, built, w.Height, d, 20000, CombinedReliefSeed(d))
                        : CombinedReliefNodes(m, w, d, 20000, CombinedReliefSeed(d)));
                    TestContext.WriteLine($"{m.Scene} eroded depth {d}: roughest half-relief {c:F5} of the node");
                    if (c > worst) { worst = c; worstDepth = d; }
                }
                TestContext.WriteLine($"{m.Scene} eroded: roughest combined half-relief {worst:F5} of the node at depth " +
                                      $"{worstDepth}; pinned half again over, {1.5 * worst:F4}");
                Measuring.Pin(m.Scene, "ErodedCombinedRelief", "bound", (1.5 * worst).ToString("F4", CultureInfo.InvariantCulture),
                              R(m.ErodedCombinedRelief));
            }
        }

        // The skirt shares on the eroded heights, as SkirtShares measures them on the uneroded.
        [Test]
        public void ErodedSkirtShares()
        {
            foreach (var m in ErodedScenes())
            {
                var w = new StressSceneTests.SceneHeights(m.Scene, m.Surface, MacroTests.RoughParams(m.Scene), eroded: true)
                        { ReliefStride = 1 };
                PinSkirtFraction(m, WorstShares(m, w, " eroded,"));
            }
        }

        // The worst share each depth gap needs, printed, and the worst of all.
        static double WorstShares(MacroTests.SceneMacro m, StressSceneTests.SceneHeights w, string label)
        {
            var worst = new double[ChunkMeshLayout.SkirtDepthGap + 1];
            int k = 0, total = 400 * (m.MaxDepth + 1) * ChunkMeshLayout.SkirtDepthGap;
            Measuring.Step($"{m.Scene}: transient edges", 0f, 1f);
            foreach (var e in StressSceneTests.TransientEdges(w, m.MaxDepth, 400, 0x5C1A7u))
            {
                worst[e.Gap] = math.max(worst[e.Gap], Share(w, e.Depth, e.GapFine, e.ReliefFine));
                worst[e.Gap] = math.max(worst[e.Gap], Share(w, e.Depth - e.Gap, e.GapCoarse, e.ReliefCoarse));
                if (++k % 50 == 0) Measuring.Tick((float)k / total);
            }
            double all = 0.0;
            string prefix = label.Length > 0 ? $"{m.Scene}{label} depth gap" : $"{m.Scene} depth gap";
            for (int g = 1; g < worst.Length; g++)
            {
                TestContext.WriteLine($"{prefix} {g}: the worst side needs {worst[g]:F3} of its relief");
                all = math.max(all, worst[g]);
            }
            TestContext.WriteLine($"{m.Scene}{label.TrimEnd(',')}: worst share {all:F3}; SkirtFraction half again over, rounded up, " +
                                  $"{math.ceil(15.0 * all) / 10.0:F1}");
            return all;
        }

        static double Share(StressSceneTests.SceneHeights w, int depth, double gap, double relief)
        {
            double sag = ChunkMeshLayout.SkirtChordSag(w.Surface, 1f, LodMath.NominalSize(w.S0, depth), Resolution);
            double net = gap - StressSceneTests.SkirtDepth - sag;
            return net > 0.0 && relief > 0.0 ? net / relief : 0.0;
        }

        // LandFraction calibrated so the final terrain reaches the scene's target, by bisection on
        // 50,000 points, then the result checked on 100,000. The final land rises with the map's,
        // not quite smoothly, since each step moves whole micro cells. Superseded by the designer's
        // Regenerate, which calibrates on the eroded terrain (the continent designer amendment).
        [Test]
        public void CalibrateLandFraction()
        {
            foreach (var m in ContinentTests.MeasuredScenes())
            {
                var    rough = MacroTests.RoughParams(m.Scene);
                double lo = 0.2, hi = 0.5, best = m.Continents.Value.LandFraction;
                double bestMiss = double.MaxValue;
                for (int it = 0; it < 10; it++)
                {
                    Measuring.Step($"{m.Scene}: bisection step {it + 1} of 10", it / 11f, (it + 1) / 11f);
                    double mid = 0.5 * (lo + hi);
                    var r = ProbeAt(m, rough, (float)mid, 50000, 0x5EA1EAFu);
                    TestContext.WriteLine($"{m.Scene} LandFraction {(float)mid:F4}: {r}");
                    if (math.abs(r.FinalLand - m.LandTarget) < bestMiss) { bestMiss = math.abs(r.FinalLand - m.LandTarget); best = mid; }
                    if (r.FinalLand < m.LandTarget) lo = mid; else hi = mid;
                }
                Measuring.Step($"{m.Scene}: the check on 100,000 points", 10f / 11f, 1f);
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
        // Earth's pinned held figure is at its own camera, (76/512, 0), printed after them. An eroded
        // scene's envelope comes from its pinned ErodedMaxAbsBaseHeight; where the build measures
        // another, the sets are measured again at that envelope, and those are the ones to pin.
        [Test]
        public void WorkingSets()
        {
            var scenes = new List<MacroTests.SceneMacro>(ContinentTests.MeasuredScenes());
            foreach (var e in ErodedScenes()) if (!scenes.Contains(e)) scenes.Add(e);
            for (int i = 0; i < scenes.Count; i++)
            {
                var m = scenes[i];
                Measuring.Step($"{m.Scene}: the replica at the pinned cameras", (float)i / scenes.Count, (i + 1f) / scenes.Count);
                double pinned = m.ErodedMaxAbsBaseHeight;
                bool   moved  = false;
                if (m.Erosion.HasValue)
                {
                    double measured = MacroTests.ErodedBuild(m).Stats.MaxAbsBaseHeight;
                    moved = measured != pinned;
                    if (moved)
                    {
                        PrintWorkingSets(m, "", pin: false);
                        m.ErodedMaxAbsBaseHeight = measured;
                    }
                }
                try { PrintWorkingSets(m, moved ? " at the measured envelope" : "", pin: true); }
                finally { m.ErodedMaxAbsBaseHeight = pinned; }
            }
        }

        static void PrintWorkingSets(MacroTests.SceneMacro m, string label, bool pin)
        {
            if (m.Scene == "EarthStress")
            {
                var s = EarthScaleTests.Earth; var roots = SurfaceMath.BuildRoots(s);
                var lod = EarthScaleTests.Lod(EarthScaleTests.Params(EarthScaleTests.OctaveCount));
                SurfaceMath.SurfaceFrame(s, roots[0], 0.0, 0.0, out double3 ground, out float3 up);
                double3 cam = lod.Vertical.MapPoint(s, ground + (double3)up * 2.0);
                int fresh = EarthScaleTests.Select(s, roots, lod, cam, held: false);
                TestContext.WriteLine($"EarthStress{label}: envelope {lod.Vertical.Envelope / 1000.0:F1} km; held working set at the " +
                                      $"face corner {EarthScaleTests.Select(s, roots, lod, cam)}, fresh {fresh}");
                SurfaceMath.SurfaceFrame(s, roots[0], EarthScaleTests.WorstU, 0.0, out ground, out up);
                int held = EarthScaleTests.Select(s, roots, lod, lod.Vertical.MapPoint(s, ground + (double3)up * 2.0));
                TestContext.WriteLine($"EarthStress{label}: held working set at the pinned camera ({EarthScaleTests.WorstU * 512.0:F0}/512, 0) {held}");
                if (pin)
                {
                    Measuring.Pin(m.Scene, "WorstWorkingSet", "count", I(held), I(EarthScaleTests.WorstWorkingSet), I(EarthScaleTests.Budget));
                    Measuring.Pin(m.Scene, "FreshCornerWorkingSet", "count", I(fresh), I(EarthScaleTests.FreshCornerWorkingSet));
                }
                return;
            }
            var sc = Array.Find(StressSceneTests.All, x => x.Name == m.Scene);
            var l  = sc.Lod(sc.Params);
            SurfaceMath.SurfaceFrame(sc.Surface, sc.Roots[sc.WorstQuad], sc.WorstU, sc.WorstV, out double3 g, out float3 n);
            int worst = EarthScaleTests.Select(sc.Surface, sc.Roots, l, l.Vertical.MapPoint(sc.Surface, g + (double3)n * 2.0));
            TestContext.WriteLine($"{m.Scene}{label}: envelope {l.Vertical.Envelope / 1000.0:F1} km; worst working set {worst}");
            if (pin) Measuring.Pin(m.Scene, "Worst", "count", I(worst), I(sc.Worst), I(sc.Budget));
        }

        static void PinReliefFactor(MacroTests.SceneMacro m, double worst)
            => Measuring.Pin(m.Scene, "ReliefFactor", "bound", math.round(1.5 * worst).ToString("F0", CultureInfo.InvariantCulture),
                             R(m.ReliefFactor));

        static void PinSkirtFraction(MacroTests.SceneMacro m, double share)
        {
            float current = m.Scene == "EarthStress" ? EarthScaleTests.SkirtFraction
                                                     : Array.Find(StressSceneTests.All, x => x.Name == m.Scene).SkirtFraction;
            Measuring.Pin(m.Scene, "SkirtFraction", "bound", (math.ceil(15.0 * share) / 10.0).ToString("F1", CultureInfo.InvariantCulture),
                          current.ToString("R", CultureInfo.InvariantCulture), note: $"worst share {share:F3}");
        }

        static string R(double x) => x.ToString("R", CultureInfo.InvariantCulture);
        static string I(int x)    => x.ToString(CultureInfo.InvariantCulture);

        static float3 MeasuredAt(string scene) => scene == "EarthStress"
            ? EarthScaleTests.EarthReliefMeasuredAt
            : Array.Find(StressSceneTests.All, x => x.Name == scene).ReliefMeasuredAt;

        static void StepDepth(MacroTests.SceneMacro m, int d, float from = 0f, float span = 1f)
            => Measuring.Step($"{m.Scene}: depth {d} of {m.MaxDepth}", from + span * d / (m.MaxDepth + 1f),
                              from + span * (d + 1f) / (m.MaxDepth + 1f));

        // ── The scans ──
        //
        // The eroded scans' managed references, one value per drawn node, which the Editor's Burst
        // versions (Measuring.Fast) are held to node for node (MeasuringScanTests).

        internal static uint MacroReliefSeed(int depth)    => (uint)(depth + 1) * 104729u;
        internal static uint CombinedReliefSeed(int depth) => (uint)(depth + 1) * 15485863u;

        // The k-th of a scan's random nodes at this depth. It depends on k alone, so a scan in any
        // order, on any thread, in Burst or managed, draws the same nodes.
        internal static NodeId DrawNode(int quadCount, int depth, uint seed, int k)
        {
            var rng = Unity.Mathematics.Random.CreateFromIndex(seed + (uint)k * 2654435761u);
            int q   = rng.NextInt(quadCount);
            int x   = rng.NextInt(1 << depth);
            int y   = rng.NextInt(1 << depth);
            return new NodeId(q, depth, x, y);
        }

        // C_m of each drawn node on the eroded maps.
        internal static double[] MacroReliefNodes(MacroTests.SceneMacro m, MacroTests.SceneErosion built, int depth, int count, uint seed)
        {
            var maps  = new ArrayMacroTexels(built.Texels, built.Drainage);
            var mp    = built.Macro;
            var roots = SurfaceMath.BuildRoots(m.Surface);
            double s0 = built.Height.S0, scale = mp.ContinentBound + mp.BaseHeightScale;
            int level = mp.Layout.MacroLevel(depth);
            return OverNodes(roots.Length, depth, count, seed, n =>
            {
                Relief(m.Surface, roots[n.Quad], n, (_, u, v) => MacroSampler.Read(maps, mp, n.Quad, level, u, v).BaseHeight,
                       out double lo, out double hi);
                return 0.5 * (hi - lo) / (LodMath.NominalSize(s0, n.Depth) * scale / s0);
            });
        }

        // Each drawn node's half-relief over its size, detail and eroded base together.
        internal static double[] CombinedReliefNodes(MacroTests.SceneMacro m, StressSceneTests.SceneHeights w, int depth, int count, uint seed)
        {
            double size = LodMath.NominalSize(w.S0, depth);
            return OverNodes(w.QuadCount, depth, count, seed, n => 0.5 * w.Relief(n) / size);
        }

        static double Max(double[] values)
        {
            double worst = 0.0;
            foreach (double x in values) worst = math.max(worst, x);
            return worst;
        }

        // f over `count` random nodes at this depth, in parallel, in slices so the progress bar
        // moves between them. Each node's draw depends on its index alone, so the result does not
        // depend on the order.
        static double[] OverNodes(int quadCount, int depth, int count, uint seed, Func<NodeId, double> f)
        {
            var values = new double[count];
            const int Slice = 1000;
            for (int start = 0; start < count; start += Slice)
            {
                int end = Math.Min(count, start + Slice);
                Parallel.For(start, end, k => values[k] = f(DrawNode(quadCount, depth, seed, k)));
                Measuring.Tick((float)end / count);
            }
            return values;
        }

        // The largest of f over `count` random nodes at this depth.
        static double MaxOverNodes(in SurfaceDef s, int depth, int count, uint seed, Func<RootQuad, NodeId, double> f)
        {
            var roots = SurfaceMath.BuildRoots(s);
            return Max(OverNodes(roots.Length, depth, count, seed, n => f(roots[n.Quad], n)));
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
