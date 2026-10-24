using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The continents survive the detail noise (Docs/Continents_Plan.md, K3 and Task 6): over
    // area-uniform points, the final terrain is on the continent map's side of sea level nearly
    // everywhere, and its land share is the scene's target, which each scene's LandFraction is
    // calibrated to reach.
    public class ContinentTests
    {
        // What the prototype's sweep reported, over the same definitions (Docs/Continents_Outline.md):
        // agreement is the share of area where the final terrain and the map are on the same side of
        // sea level, 0; flooded is the share of the map's land below it, islands the share of its
        // ocean above it.
        internal struct ProbeResult
        {
            public int    Points;
            public double MapLand, FinalLand, Agreement, Flooded, Islands, MedianLand, MedianOcean;

            public override string ToString() =>
                $"{Points:N0} points: map land {MapLand:P1}, final land {FinalLand:P2}, agreement {Agreement:P2}, " +
                $"flooded {Flooded:P1}, islands {Islands:P1}, median land {MedianLand:F0} m, median ocean {MedianOcean:F0} m";
        }

        // The probe on a stress scene, or the synthetic one, as it is configured: its height
        // parameters through SceneParams, its texels with its continent channel, and the height at
        // MaxDepth through HeightAtPoint, the function TerrainRoot.HeightAt calls.
        internal static ProbeResult ProbeScene(MacroTests.SceneMacro m, in HeightParams rough, int points, uint seed)
        {
            var built = MacroTests.ContinentBuild(m);
            return Probe(m.Surface, m.MaxDepth, MacroTests.SceneParams(m, rough), built.Macro, built.Params,
                         built.Channel, built.Map, points, seed);
        }

        internal static ProbeResult Probe(in SurfaceDef s, int maxDepth, in HeightParams hp, in MacroParams mp,
                                          in ContinentParams cp, float[] channel, ContinentMap map, int points, uint seed)
        {
            var roots  = SurfaceMath.BuildRoots(s);
            var texels = new StressSceneTests.TexelReader(new StressSceneTests.LazyTexels(s, roots, mp, channel));
            var tables = new ArrayContinentTables(map);
            var rng    = new Unity.Mathematics.Random(seed);
            var lands  = new List<double>();
            var oceans = new List<double>();
            int mapLand = 0, agree = 0, flooded = 0, islands = 0;
            for (int k = 0; k < points; k++)
            {
                double3 p = ContinentMap.AreaUniformPoint(s, roots, ref rng);
                double  h = TerrainHeightFunction.HeightAtPoint(s, p, maxDepth, hp, mp, texels);
                bool mapL = ContinentField.Sample(p, tables, cp).S > 0.0;
                bool finL = h > 0.0;
                if (mapL) mapLand++;
                if (mapL == finL) agree++;
                if (mapL && !finL) flooded++;
                if (!mapL && finL) islands++;
                (finL ? lands : oceans).Add(h);
            }
            return new ProbeResult
            {
                Points      = points,
                MapLand     = (double)mapLand / points,
                FinalLand   = (double)lands.Count / points,
                Agreement   = (double)agree / points,
                Flooded     = mapLand > 0 ? (double)flooded / mapLand : 0.0,
                Islands     = mapLand < points ? (double)islands / (points - mapLand) : 0.0,
                MedianLand  = Median(lands),
                MedianOcean = Median(oceans),
            };
        }


        static double Median(List<double> values)
        {
            if (values.Count == 0) return double.NaN;
            values.Sort();
            return values[values.Count / 2];
        }

        // The measuring tools run only when asked for: [Explicit] did not keep them out of an Editor
        // run, where they took 53 minutes and three hit the Test Runner's 180 s timeout. Set
        // SONOMA_MEASURE=1 and run them through the out-of-Editor harness; elsewhere they skip.
        internal static void MeasuringToolsOnly()
        {
            if (Environment.GetEnvironmentVariable("SONOMA_MEASURE") != "1")
                Assert.Ignore("A measuring tool, minutes outside the Editor and up to 40 under its Mono: set " +
                              "SONOMA_MEASURE=1 and run it through the out-of-Editor harness.");
        }

        // The stress scenes with continents. The tests below also run the probe on the synthetic
        // scene, which keeps it honest whatever this returns.
        internal static IEnumerable<MacroTests.SceneMacro> ContinentScenes()
        {
            foreach (var m in MacroTests.StressMacros)
                if (m.Continents.HasValue) yield return m;
        }

        // The continent scenes the explicit measuring tools run on: all of them, or those the
        // environment variable SONOMA_SCENES names, comma-separated, so retuning one scene does
        // not re-measure the rest.
        internal static IEnumerable<MacroTests.SceneMacro> MeasuredScenes()
        {
            string only = Environment.GetEnvironmentVariable("SONOMA_SCENES");
            var    names = string.IsNullOrWhiteSpace(only) ? null : new HashSet<string>(only.Split(','));
            foreach (var m in ContinentScenes())
                if (names == null || names.Contains(m.Scene)) yield return m;
        }

        // At 20,000 points: agreement of at least 90%, and final land within 1.5 points of each
        // scene's target. The synthetic scene's detail is a tenth of its continents' heights, so its
        // final land is its map's to within the same 1.5 points.
        [Test]
        public void ContinentsSurviveTheDetailNoise()
        {
            var syn = ProbeScene(MacroTests.SyntheticContinents, MacroTests.SyntheticRough, 5000, 0xC0A57u);
            TestContext.WriteLine($"synthetic: {syn}");
            Assert.GreaterOrEqual(syn.Agreement, 0.9, $"synthetic: {syn}");
            Assert.AreEqual(syn.MapLand, syn.FinalLand, 0.015, $"synthetic: {syn}");

            foreach (var m in ContinentScenes())
            {
                var r = ProbeScene(m, MacroTests.RoughParams(m.Scene), 20000, 0xC0A57u);
                TestContext.WriteLine($"{m.Scene}: {r}");
                Assert.GreaterOrEqual(r.Agreement, 0.9, $"{m.Scene}: {r}");
                Assert.AreEqual(m.LandTarget, r.FinalLand, 0.015, $"{m.Scene}: final land off target; recalibrate LandFraction. {r}");
            }
        }

        // D4's evidence: the climate over land with continents against M5b's. Continents put land
        // kilometres above the sea, so at the same lapse rate interiors are colder. Prints, over the
        // final land of 20,000 area-uniform points, the mean temperature and each biome's share of
        // the weights. M5b's land is the 30% above its own sea level, the height's 70th percentile,
        // as the ocean plan set it; its base height is M5b's BaseHeightScale with no continents and
        // no cutoff.
        [Test, Explicit("Evidence for the lapse-rate decision, printed.")]
        public void ClimateOverLand()
        {
            MeasuringToolsOnly();
            var m5bBaseHeight = new Dictionary<string, float>
            {
                ["EarthStress"] = 3000f, ["SmallPlanetStress"] = 2000f, ["RingworldStress"] = 1500f, ["NarrowRingStress"] = 1500f,
            };
            foreach (var m in MeasuredScenes())
            {
                var rough = MacroTests.RoughParams(m.Scene);
                var built = MacroTests.ContinentBuild(m);
                TestContext.WriteLine($"{m.Scene} with continents: " +
                    Climate(m, MacroTests.SceneParams(m, rough), built.Macro, built.Channel, seaShare: -1.0));

                var c = m.Config; c.BaseHeightScale = m5bBaseHeight[m.Scene]; c.Continents = null;
                var mp = MacroParams.Create(m.Surface, HeightParams.Create(m.Surface, 33, 0.0, 20, 1f, 0.5f, 2f, 42u), c);
                TestContext.WriteLine($"{m.Scene} as M5b:        " +
                    Climate(m, rough.WithBands(m.LowBandEdge, m.MidBandEdge), mp, null, seaShare: 0.70));
            }
        }

        // seaShare below zero puts sea level at 0; otherwise at that quantile of the sampled heights.
        static string Climate(MacroTests.SceneMacro m, in HeightParams hp, in MacroParams mp, float[] channel, double seaShare)
        {
            const int Points = 20000;
            var roots  = SurfaceMath.BuildRoots(m.Surface);
            var texels = new StressSceneTests.TexelReader(new StressSceneTests.LazyTexels(m.Surface, roots, mp, channel));
            var rng    = new Unity.Mathematics.Random(0xC11u);
            int top    = mp.Layout.LevelCount - 1;
            var h      = new double[Points];
            var t      = new double[Points];
            var w      = new float4[Points];
            for (int k = 0; k < Points; k++)
            {
                double3 p = ContinentMap.AreaUniformPoint(m.Surface, roots, ref rng);
                h[k] = TerrainHeightFunction.HeightAtPoint(m.Surface, p, m.MaxDepth, hp, mp, texels);
                SurfaceMath.Locate(m.Surface, p, out int q, out double u, out double v);
                var sample = MacroSampler.Read(texels, mp, q, top, u, v);
                t[k] = sample.Temperature;
                w[k] = Biomes.Classify(sample, mp);
            }
            double sea = 0.0;
            if (seaShare >= 0.0)
            {
                var sorted = (double[])h.Clone();
                Array.Sort(sorted);
                sea = sorted[(int)(seaShare * (Points - 1))];
            }
            double temperature = 0.0; float4 share = 0f; int land = 0;
            for (int k = 0; k < Points; k++)
            {
                if (h[k] <= sea) continue;
                land++; temperature += t[k]; share += w[k];
            }
            share /= math.max(1, land);
            string biomes = "";
            for (int b = 0; b < mp.BiomeCount; b++)
                biomes += $"{System.IO.Path.GetFileNameWithoutExtension(MacroTests.BiomeTable[b].Asset)} {share[b]:P0}  ";
            return $"land {land * 100.0 / Points:F1}% above {sea:F0} m, mean land temperature {temperature / math.max(1, land):F1} C; {biomes}";
        }

        // The retuning tool: 100,000 points per continent scene, printed.
        [Test, Explicit("A retuning tool: about a minute a scene outside the Editor.")]
        public void ContinentsSurviveTheDetailNoiseAtFullSample()
        {
            MeasuringToolsOnly();
            foreach (var m in MeasuredScenes())
                TestContext.WriteLine($"{m.Scene}: {ProbeScene(m, MacroTests.RoughParams(m.Scene), 100000, 0x5EA1EAFu)}");
        }
    }
}
