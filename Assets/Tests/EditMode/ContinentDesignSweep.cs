using System;
using NUnit.Framework;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The measurements ContinentDesign's mapping is fitted from (Docs/Continents_Plan.md, the
    // continent designer amendment): over a grid of continent counts and map land shares, how many
    // continents and islands the map really makes, how much of the land the largest holds, and the
    // land share and saturation it reaches. Stage A only, managed; seconds a configuration outside
    // the Editor. Prints, asserts nothing.
    [Explicit("Measurements for fitting the continent designer, minutes: run by name.")]
    public class ContinentDesignSweep
    {
        const int Resolution = 33;

        [SetUp]
        public void OnlyWhenAsked() => ContinentTests.MeasuringToolsOnly();

        // The macro cells scaled so each continent keeps today's 2.25 land cells (ContinentDesign.
        // MacroCellsFor), with no separation and with two: a separation of 0.5 already did most of
        // what 2 does on Earth (2026-11-07). An earlier run with today's fixed 30 cells, and with no
        // BFS floor, is in the amendment; the floor changed nothing.
        [Test]
        public void ContinentCountsAndLand()
        {
            int[]   counts = { 1, 2, 4, 8, 16, 24 };
            float[] lands  = { 0.1f, 0.3f, 0.5f, 0.7f };
            foreach (var m in ContinentTests.MeasuredScenes())
            foreach (string rule in new[] { "scaled", "sep-2" })
            foreach (float land in lands)
            foreach (int n in counts)
            {
                var k = m.Continents.Value;
                k.ContinentCount = n;
                k.LandFraction   = land;
                k.MacroCells     = ContinentDesign.MacroCellsFor(n, land);
                if (rule != "scaled") k.Separation = float.Parse(rule.Substring(4), System.Globalization.CultureInfo.InvariantCulture);
                var map = Build(m, k, out double ms);
                var st  = map.Stats;
                TestContext.WriteLine($"{m.Scene} {rule,-14} land {land:F1} N {n,2} M {k.MacroCells,3}: " +
                                      $"continents {st.Continents,2}, islands {st.Islands,4}, largest {st.LargestContinent:P0}, " +
                                      $"pieces {Pieces(map, 6)}, map land {st.LandFraction:P1}, saturation {st.SaturationRadius:F2}, {ms:F0} ms");
            }
        }

        internal static ContinentMap Build(MacroTests.SceneMacro m, ContinentConfig k, out double ms)
        {
            var c  = m.Config; c.Continents = k;
            var mp = MacroParams.Create(m.Surface, HeightParams.Create(m.Surface, Resolution, 0.0, 20, 1f, 0.5f, 2f, 42u), c);
            var cp = ContinentParams.Create(m.Surface, mp, c);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var map = ContinentMap.Build(m.Surface, SurfaceMath.BuildRoots(m.Surface), mp.Layout, cp);
            ms = clock.Elapsed.TotalMilliseconds;
            return map;
        }

        // The largest few pieces as shares of the land.
        static string Pieces(ContinentMap map, int count)
        {
            double land = 0.0;
            foreach (double a in map.LandPieces) land += a;
            var s = new System.Text.StringBuilder();
            for (int i = 0; i < Math.Min(count, map.LandPieces.Length); i++)
                s.Append(i == 0 ? "" : " ").Append((map.LandPieces[i] / land * 100.0).ToString("F0"));
            return s.ToString();
        }
    }
}
