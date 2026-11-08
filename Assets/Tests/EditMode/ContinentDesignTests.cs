using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The continent designer (Docs/Continents_Plan.md, the continent designer amendment): what
    // ContinentDesign.Derive may change and what it must keep, that fragmentation and the
    // separation make the continents they promise, that the land's pieces add up, and that the
    // calibration's secant lands on its target.
    public class ContinentDesignTests
    {
        static readonly float[] Fragmentations = { 0f, 0.1f, 0.25f, 0.436f, 0.5f, 0.75f, 0.9f, 1f };
        static readonly float[] MapLands       = { 0.01f, 0.05f, 0.3f, 0.5f, 0.7f, 0.95f, 0.99f };

        // Derive changes the land fraction, the count, the macro cells and the separation, and
        // nothing else: the micro cells, the coast and the heights decide how steep the coasts are,
        // which the LOD relief allowance is pinned against. Every result validates, with no more
        // centres than cells.
        [Test]
        public void DeriveKeepsEverythingElseToTheBit()
        {
            foreach (var m in ContinentTests.ContinentScenes())
            foreach (float f in Fragmentations)
            foreach (float land in MapLands)
            {
                ContinentConfig k = m.Continents.Value, d = ContinentDesign.Derive(k, f, land);
                string at = $"{m.Scene}, fragmentation {f}, land {land}";
                Assert.DoesNotThrow(() => d.Validate(), at);
                Assert.LessOrEqual(d.ContinentCount, d.MacroCells, at);
                Assert.AreEqual(land, d.LandFraction, at);
                Assert.AreEqual(ContinentDesign.Separation, d.Separation, at);

                // Put back what Derive may change; the rest must be k's bits.
                ContinentConfig back = d;
                back.LandFraction = k.LandFraction; back.ContinentCount = k.ContinentCount;
                back.MacroCells = k.MacroCells; back.Separation = k.Separation;
                Assert.AreEqual(k, back, $"{at}: Derive changed a field it must keep");
            }
        }

        // Fragmentation 0 is one centre and 1 is MaxContinents, never falling between; the inverse
        // gives each count back, so an asset written before the designer opens at its own count.
        [Test]
        public void FragmentationSetsTheCount()
        {
            Assert.AreEqual(1, ContinentDesign.ContinentCountFor(0f));
            Assert.AreEqual(ContinentDesign.MaxContinents, ContinentDesign.ContinentCountFor(1f));
            Assert.AreEqual(1, ContinentDesign.ContinentCountFor(-1f), "clamped below");
            Assert.AreEqual(ContinentDesign.MaxContinents, ContinentDesign.ContinentCountFor(2f), "clamped above");
            int last = 0;
            for (int i = 0; i <= 1000; i++)
            {
                int n = ContinentDesign.ContinentCountFor(i / 1000f);
                Assert.GreaterOrEqual(n, last, $"fragmentation {i / 1000f}");
                last = n;
            }
            foreach (int n in new[] { 1, 2, 3, 4, 8, 16, 24 })
                Assert.AreEqual(n, ContinentDesign.ContinentCountFor(ContinentDesign.FragmentationFor(n)), $"{n} continents");

            // A NaN is refused, under the name of the argument that carried it.
            var bad = Assert.Throws<System.ArgumentOutOfRangeException>(
                () => ContinentDesign.Derive(ContinentConfig.Default, float.NaN, 0.3f));
            Assert.AreEqual("fragmentation", bad.ParamName);
            bad = Assert.Throws<System.ArgumentOutOfRangeException>(
                () => ContinentDesign.Derive(ContinentConfig.Default, 0.5f, float.NaN));
            Assert.AreEqual("mapLand", bad.ParamName);
        }

        // A sphere small enough to build in a test, with Earth's seeds and spread: 2,000 micro cells
        // of about four top-level texels.
        static ContinentMap BuildSphere(float fragmentation, float land, bool separate)
        {
            var k = ContinentConfig.Default;
            k.MicroCells = 2000; k.LandHeight = 2000f; k.OceanDepth = 6000f;
            k = ContinentDesign.Derive(k, fragmentation, land);
            if (!separate) k.Separation = 0f;
            var c = ContinentMapTests.Make("design", SurfaceDef.CubeSphere(6371000.0), k, 33, 2);
            return ContinentMapTests.Build(c);
        }

        // One continent at fragmentation 0, and many small ones at 1, at a land share where
        // without the separation they merge: the fit ContinentDesignSweep made on Earth, at a size
        // a test can build.
        [Test]
        public void FragmentationMakesTheContinents()
        {
            foreach (float land in new[] { 0.3f, 0.6f })
            {
                var one = BuildSphere(0f, land, true);
                TestContext.WriteLine($"land {land}, one: {one.Stats.Continents} continents, largest {one.Stats.LargestContinent:P0}");
                Assert.GreaterOrEqual(one.Stats.LargestContinent, 0.8, $"land {land}: fragmentation 0 is one continent");

                var many = BuildSphere(1f, land, true);
                TestContext.WriteLine($"land {land}, many: {many.Stats.Continents} continents, largest {many.Stats.LargestContinent:P0}");
                Assert.GreaterOrEqual(many.Stats.Continents, ContinentDesign.MaxContinents / 2, $"land {land}: fragmentation 1");
                Assert.LessOrEqual(many.Stats.LargestContinent, 0.25, $"land {land}: fragmentation 1's largest");
            }

            // The mutation: without the separation, a land share of 0.6 makes one supercontinent of
            // any count.
            var merged = BuildSphere(1f, 0.6f, false);
            TestContext.WriteLine($"land 0.6, many, unseparated: {merged.Stats.Continents} continents, largest {merged.Stats.LargestContinent:P0}");
            Assert.GreaterOrEqual(merged.Stats.LargestContinent, 0.6, "without the separation the continents merge");
        }

        // The pieces partition the land: their areas sum to the land micro cells', largest first,
        // and every piece is a continent or an island.
        [Test]
        public void LandPiecesPartitionTheLand()
        {
            foreach (var c in ContinentMapTests.Cases())
            foreach (bool sep in new[] { false, true })
            {
                var k = c.Config.Continents.Value; k.Separation = sep ? 2f : 0f;
                var map = ContinentMapTests.Build(ContinentMapTests.Make(c.Name, c.Surface, k));
                double land = 0.0, pieces = 0.0;
                for (int i = 0; i < map.MicroLand.Length; i++) if (map.MicroLand[i] != 0) land += map.MicroArea[i];
                for (int p = 0; p < map.LandPieces.Length; p++)
                {
                    pieces += map.LandPieces[p];
                    if (p > 0) Assert.LessOrEqual(map.LandPieces[p], map.LandPieces[p - 1], $"{c.Name}: largest first");
                }
                Assert.AreEqual(land, pieces, land * 1e-12, $"{c.Name}, separation {sep}: the pieces' area");
                Assert.AreEqual(map.LandPieces.Length, map.Stats.Continents + map.Stats.Islands, $"{c.Name}: every piece counted");
                Assert.GreaterOrEqual(map.Stats.Continents, 1, $"{c.Name}: some land is a continent");
                Assert.AreEqual(map.LandPieces[0] / land, map.Stats.LargestContinent, 1e-12, $"{c.Name}: the largest's share");
            }
        }

        // Each cell's owner is a centre, and a centre owns itself: the separation's borders are
        // between the centres' own domains.
        [Test]
        public void OwnersAreCentres()
        {
            foreach (var c in ContinentMapTests.Cases())
            {
                var map = ContinentMapTests.Build(c);
                for (int a = 0; a < map.MacroOwner.Length; a++)
                {
                    int o = map.MacroOwner[a];
                    if (map.MacroCentre[a]) Assert.AreEqual(a, o, $"{c.Name}: centre {a} owns itself");
                    if (map.MacroElevation[a] > 0.0) Assert.IsTrue(o >= 0 && map.MacroCentre[o], $"{c.Name}: cell {a}'s owner {o}");
                    else Assert.AreEqual(-1, o, $"{c.Name}: cell {a} was never reached");
                }
            }
        }

        // The calibration's step, on a final land share like the real ones -- the map's less a few
        // points, a little curved, with measurement noise of a few tenths of a point -- lands within
        // the designer's half point in its four builds, for targets across the slider's range.
        [Test]
        public void CalibrationReachesItsTarget()
        {
            static float Final(float map) =>
                (float)(map - 0.03 - 0.1 * (map - 0.3) * (map - 0.3) + 0.002 * math.sin(97.0 * map));

            // Up to 0.9: this curve reaches at most 0.912, at the map's 0.99.
            for (float target = 0.05f; target <= 0.901f; target += 0.05f)
            {
                float map = math.clamp(target + 0.02f, 0.01f, 0.99f), map0 = float.NaN, final0 = float.NaN;
                float best = float.MaxValue;
                for (int b = 0; b < 4; b++)
                {
                    float final = Final(map);
                    best = math.min(best, math.abs(final - target));
                    if (best <= 0.005f) break;
                    float next = float.IsNaN(map0) ? ContinentDesign.NextMapLand(target, map, final)
                                                   : ContinentDesign.NextMapLand(target, map0, final0, map, final);
                    map0 = map; final0 = final; map = next;
                }
                Assert.LessOrEqual(best, 0.005f, $"target {target}");
            }

            // A target past what the map can make ends at the map's limit, the closest it can come,
            // which the designer keeps and reports as not converged.
            {
                float target = 0.95f, map = 0.97f, map0 = float.NaN, final0 = float.NaN;
                for (int b = 0; b < 4; b++)
                {
                    float final = Final(map);
                    float next = float.IsNaN(map0) ? ContinentDesign.NextMapLand(target, map, final)
                                                   : ContinentDesign.NextMapLand(target, map0, final0, map, final);
                    map0 = map; final0 = final; map = next;
                }
                Assert.AreEqual(0.99f, map, "an unreachable target");
            }

            // A flat or backwards secant halves the miss rather than leaping; the result stays in range.
            Assert.AreEqual(0.35f, ContinentDesign.NextMapLand(0.3f, 0.3f, 0.2f, 0.3f, 0.2f), 1e-6f, "a repeated try");
            Assert.AreEqual(0.99f, ContinentDesign.NextMapLand(0.95f, 0.9f, 0.5f));
            Assert.AreEqual(0.01f, ContinentDesign.NextMapLand(0.05f, 0.05f, 0.5f));
        }
    }
}
