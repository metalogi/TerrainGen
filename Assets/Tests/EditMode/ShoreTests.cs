using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The shore rule (Docs/Shore_Stability_Plan.md, amendments 2 and 3): a vertex drawn at depth d on
    // the other side of the sea from the height at HeightParams.ShoreReferenceDepth(d) takes that
    // height, so a coarse chunk draws the finer coast. These hold the identity when it is off, the
    // reference depth, the rule itself on a sphere without a macro layer, and the skip it takes far
    // from the sea against the evaluation it saves, over eroded maps with biomes. That the sample jobs
    // apply it, the coarse job the parent's, is GeomorphTests.ChildAtFullMorphEqualsParent's.
    public class ShoreTests
    {
        // Earth's detail at the stress scene's spectrum, bands, cutoff and gully term, without a macro
        // layer.
        static HeightParams Earth => EarthScaleTests.SpectrumParams.WithBands(150000.0, 5000.0)
                                                                   .WithContinentalCutoff(1000000.0, 0f)
                                                                   .WithGullies(8f, 3000.0, 0f);

        static readonly ArrayMacroTexels None = new ArrayMacroTexels(null, null);

        static float Drawn(in HeightParams p, double3 point, int depth, bool skip = true)
        {
            int oct = p.MaxOctave(depth);
            float h = TerrainHeightFunction.Height(point, oct, MacroSample.Zero, p);
            return TerrainHeightFunction.ShoreHeight(None, MacroParams.Disabled, p, 0, 0.0, 0.0, point, depth, oct, h, skip);
        }

        [Test]
        public void ShoreOffKeepsTheBits()
        {
            var p = Earth;
            Assert.IsFalse(p.HasShore, "Create left the shore rule on");
            var off = p.WithShore(5.0, 0, 20000.0);
            Assert.IsFalse(off.HasShore, "a lead of 0 turned the rule on");
            Assert.AreEqual(0.0, off.ShoreSea, "a lead of 0 kept a sea level");
            Assert.AreEqual(0, off.ShoreFloorDepth, "a lead of 0 kept a floor");

            var s   = EarthScaleTests.Earth;
            var rng = new Random(0x5A0Eu);
            for (int k = 0; k < 500; k++)
            {
                double3 point = SurfaceMath.SurfacePoint(s, SurfaceMath.BuildRoots(s)[rng.NextInt(0, 6)], rng.NextDouble(), rng.NextDouble());
                int   depth = rng.NextInt(0, 20);
                float h     = rng.NextFloat(-9000f, 9000f);
                foreach (var q in new[] { p, off })
                    Assert.AreEqual(math.asuint(h), math.asuint(TerrainHeightFunction.ShoreHeight(
                        None, MacroParams.Disabled, q, 0, 0.0, 0.0, point, depth, q.MaxOctave(depth), h)),
                        "the rule off changed a height");
            }
        }

        [Test]
        public void ReferenceDepthFollowsTheLeadAndTheFloor()
        {
            var p = Earth;
            var shore = p.WithShore(0.0, 2, 20000.0);
            int floor = shore.ShoreFloorDepth;
            Assert.LessOrEqual(p.S0 / (1L << floor), 20000.0, "the floor's nodes are larger than the reference size");
            Assert.Greater(p.S0 / (1L << (floor - 1)), 20000.0, "the floor is deeper than the first depth that fits");
            Assert.AreEqual(10, floor, "Earth's floor, as the probe measured it");
            for (int d = -1; d <= 20; d++)
                Assert.AreEqual(math.max(d + 2, floor), shore.ShoreReferenceDepth(d), $"the reference of depth {d}");

            // An exact power of two lands on its own depth however the division rounds.
            Assert.AreEqual(3, p.WithShore(0.0, 1, p.S0 / 8.0).ShoreFloorDepth, "a size of S0 / 8");
            Assert.AreEqual(0, p.WithShore(0.0, 1, 0.0).ShoreFloorDepth, "no floor");
            Assert.AreEqual(0, p.WithShore(0.0, 1, 2.0 * p.S0).ShoreFloorDepth, "a size past the root");

            Assert.Throws<System.ArgumentOutOfRangeException>(() => p.WithShore(0.0, -1, 0.0), "a negative lead");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => p.WithShore(double.NaN, 2, 0.0), "a NaN sea");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => p.WithShore(0.0, 2, -1.0), "a negative size");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => p.WithShore(0.0, 2, double.PositiveInfinity), "an infinite size");
            Assert.Throws<System.InvalidOperationException>(() => default(HeightParams).WithShore(0.0, 2, 0.0), "no Create");
        }

        // The rule itself: the drawn height is the vertex's own where it and the reference agree on the
        // side of the sea, and the reference's where they do not. Half the points put the sea between
        // the two, so every point where they differ flips; the other half put it at 0.
        [Test]
        public void ShoreTakesTheReferenceSide()
        {
            var s     = EarthScaleTests.Earth;
            var roots = SurfaceMath.BuildRoots(s);
            var rng   = new Random(0x5A1Eu);
            double bound = TerrainHeightFunction.MaxAbsHeight(Earth, MacroParams.Disabled);
            int flipped = 0, kept = 0;
            foreach (int depth in new[] { 0, 3, 8, 12, 16, 18 })
            {
                for (int k = 0; k < 400; k++)
                {
                    double3 point = SurfaceMath.SurfacePoint(s, roots[rng.NextInt(0, 6)], rng.NextDouble(), rng.NextDouble());
                    var    probe = Earth.WithShore(0.0, 2, 20000.0);
                    int    rd    = probe.ShoreReferenceDepth(depth);
                    float  h     = TerrainHeightFunction.Height(point, Earth.MaxOctave(depth), MacroSample.Zero, Earth);
                    float  r     = TerrainHeightFunction.Height(point, Earth.MaxOctave(rd), MacroSample.Zero, Earth);
                    double sea   = (k & 1) == 0 ? 0.0 : 0.5 * ((double)h + r);
                    var    p     = Earth.WithShore(sea, 2, 20000.0);

                    float drawn    = Drawn(p, point, depth);
                    bool  agree    = (h - sea > 0.0) == (r - sea > 0.0);
                    float expected = agree ? h : r;
                    Assert.AreEqual(math.asuint(expected), math.asuint(drawn),
                        $"depth {depth}, sea {sea}: drew {drawn}, own {h}, reference {r}");
                    Assert.AreEqual(r - sea > 0.0, drawn - sea > 0.0, $"depth {depth}: not the reference's side");
                    Assert.LessOrEqual(math.abs((double)drawn), bound, "past MaxAbsHeight");
                    if (agree) kept++; else flipped++;
                }
            }
            TestContext.WriteLine($"{flipped} vertices took the reference's height, {kept} kept their own");
            Assert.Greater(flipped, 1000, "the rule was hardly exercised");
            Assert.Greater(kept, 1000, "the rule hardly kept a height");
        }

        // The skip is exact: a vertex further from the sea than the added octaves' largest amplitude
        // cannot change side, so skipping it draws the bits evaluating it would. Over eroded maps with
        // biomes and wet gullies, at depths where the reference reads the same macro level, with the
        // sea swept across the gap between the two heights and well past it.
        [Test]
        public void SkipFarFromTheSeaIsExact()
        {
            var c     = ErosionBuildTests.Make("plane", SurfaceDef.PlaneGrid(100000.0, 2, 2), ErosionBuildTests.Incising(), general: true);
            var built = ErosionBuildTests.Build(c, out float2[] drainage, out _, out _);
            var maps  = new MacroTests.ArrayTexels(built, drainage);
            var mp    = c.Macro;
            var hp    = c.Height.WithGullies(8f, c.Height.OctaveWavelength0 / 32.0, 2f);
            Assert.Greater(hp.GullyStartOctave, ErosionChannels.ErodedOctave(hp, mp), "the gullies reach the eroded octaves");
            int top = mp.Layout.LevelCount - 1;

            var rng = new Random(0x5A2Eu);
            int flipped = 0, compared = 0;
            for (int depth = top; depth <= top + 4; depth++)
            {
                int oct = hp.MaxOctave(depth), rd = depth + 2;
                Assert.AreEqual(mp.Layout.MacroLevel(depth), mp.Layout.MacroLevel(rd), "the levels differ, so nothing is skipped");
                Assert.Greater(hp.MaxOctave(rd), oct, "the reference adds no octave");
                for (int k = 0; k < 300; k++)
                {
                    int quad = rng.NextInt(0, c.Roots.Length);
                    double u = rng.NextDouble(), v = rng.NextDouble();
                    double3 point = SurfaceMath.SurfacePoint(c.Surface, c.Roots[quad], u, v);
                    var   own = TerrainHeightFunction.Macro(maps, mp, hp, quad, mp.Layout.MacroLevel(depth), u, v, oct, out _);
                    float h   = TerrainHeightFunction.Height(point, oct, own, hp);
                    var   ref_ = TerrainHeightFunction.Macro(maps, mp, hp, quad, mp.Layout.MacroLevel(rd), u, v, hp.MaxOctave(rd), out _);
                    float r   = TerrainHeightFunction.Height(point, hp.MaxOctave(rd), ref_, hp);
                    double gap = math.max(1.0, math.abs((double)r - h));
                    foreach (double offset in new[] { -0.5, 0.3, 1.5, -4.0, 20.0, -300.0 })
                    {
                        var p = hp.WithShore((double)h + offset * gap * math.sign(r - h + 1e-30f), 2, 0.0);
                        float skipped = TerrainHeightFunction.ShoreHeight(maps, mp, p, quad, u, v, point, depth, oct, h, true);
                        float full    = TerrainHeightFunction.ShoreHeight(maps, mp, p, quad, u, v, point, depth, oct, h, false);
                        Assert.AreEqual(math.asuint(full), math.asuint(skipped),
                            $"depth {depth}, offset {offset}: the skip drew {skipped}, the evaluation {full}");
                        compared++;
                        if (full != h) flipped++;
                    }
                }
            }
            TestContext.WriteLine($"{compared} comparisons, {flipped} flipped");
            Assert.Greater(flipped, compared / 10, "the sea hardly fell between the two heights");
        }
    }
}
