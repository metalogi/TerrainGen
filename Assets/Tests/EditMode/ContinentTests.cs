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
                double3 p = AreaUniform(s, roots, ref rng);
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

        // Uniform over the surface's area: rejection in the cube on a sphere, a uniform quad and
        // (u, v) on a cylinder or plane grid.
        internal static double3 AreaUniform(in SurfaceDef s, RootQuad[] roots, ref Unity.Mathematics.Random rng)
        {
            if (s.Type == SurfaceType.CubeSphere)
                while (true)
                {
                    double3 v  = rng.NextDouble3(-1.0, 1.0);
                    double  l2 = math.dot(v, v);
                    if (l2 > 1e-6 && l2 <= 1.0) return v * (s.Radius / math.sqrt(l2));
                }
            var q = roots[rng.NextInt(roots.Length)];
            return SurfaceMath.SurfacePoint(s, q, rng.NextDouble(), rng.NextDouble());
        }

        static double Median(List<double> values)
        {
            if (values.Count == 0) return double.NaN;
            values.Sort();
            return values[values.Count / 2];
        }

        // The stress scenes with continents. The tests below also run the probe on the synthetic
        // scene, which keeps it honest whatever this returns.
        internal static IEnumerable<MacroTests.SceneMacro> ContinentScenes()
        {
            foreach (var m in MacroTests.StressMacros)
                if (m.Continents.HasValue) yield return m;
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

        // The retuning tool: 100,000 points per continent scene, printed.
        [Test, Explicit("A retuning tool: about a minute a scene outside the Editor.")]
        public void ContinentsSurviveTheDetailNoiseAtFullSample()
        {
            foreach (var m in ContinentScenes())
                TestContext.WriteLine($"{m.Scene}: {ProbeScene(m, MacroTests.RoughParams(m.Scene), 100000, 0x5EA1EAFu)}");
        }
    }
}
