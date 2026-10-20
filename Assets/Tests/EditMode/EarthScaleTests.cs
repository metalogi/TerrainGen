using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;
using Sample = Sonoma.Tests.TerrainReliefTests;

namespace Sonoma.Tests
{
    // The Earth-scale configuration, Assets/Settings/EarthTerrainSettings.asset, against the
    // three things that go wrong at planetary scale and cannot go wrong on SampleScene's 300 m
    // sphere: the octave count running out before the depth does, the LOD relief allowance, and
    // the resident working set. Docs/M4_FloatingOrigin_Plan.md C1-C3 and C5 derive each number.
    //
    // UnityEngine-free, so it runs in the out-of-Editor harness. The constants below are copies
    // of the asset, for the same reason TerrainReliefTests copies SampleScene's.
    public class EarthScaleTests
    {
        internal const double Radius        = 6371000.0;
        internal const int    Resolution    = 33;
        internal const int    MaxDepth      = 19;
        internal const float  HeightScale   = 20000f;
        internal const float  SplitFactor   = 4f;
        internal const float  Hysteresis    = 1.1f;
        internal const float  MorphStart    = 0.10f;
        internal const float  Preload       = 1f;
        internal const int    Budget        = 9400;   // MaxResidentChunks
        internal const float  SkirtDepth    = 1f;
        internal const float  SkirtFraction = 2.9f;

        // The LOD measures in a world with every height scaled by this (LodVertical), so Earth at
        // HeightScale 20,000 spends the relief allowance, and wants the chunks, that it did at
        // 4,000 -- the relief M5b sized SplitFactor 4 and the budget for. Five times the relief
        // without it would need SplitFactor about 10 (EarthConfigurationClearsTheReliefAllowance).
        internal const float  LodVerticalScale = 0.2f;
        internal const int    OctaveCount   = 24;
        internal const float  Persistence   = 0.7f;
        internal const float  Lacunarity    = 2f;
        internal const uint   Seed          = 42u;

        // The relief spectrum (C10): persistence 0.7 above 5 km and 0.53 below, so the octaves
        // fall off slowly at the scale of hills and mountains -- 395 m over 5 km, 805 m over
        // 20 km, 1.6 km over 100 km before biomes and ruggedness, at HeightScale 20,000 -- and at
        // about the old rate below it, which leaves 10 m features 1.3 m. One slope of 0.5 at this
        // HeightScale would make them 2 cm, and 5 km features 10 m. M5b shipped 4,000, a fifth of
        // all of these; the user found it flat from altitude, and five times the relief at every
        // scale, walking scale included, was the look asked for.
        internal const double RoughnessCrossover = 5000.0;
        internal const float  FinePersistence    = 0.53f;
        internal const double TenMetreFeature    = 1.305;
        internal const double TenMetreTarget     = 0.2;
        internal const double TenMetreOnOneSlope = 0.019;

        // The relief demand at Earth depth, C = halfRelief / (nodeSize * HeightScale / S0) for
        // the roughest node -- an ESTIMATE, not a measurement. A face at depth 19 is 4^19 nodes,
        // so nothing has scanned it.
        //
        // On M4's single-slope spectrum, 20,000 random depth-19 nodes gave a maximum of 8.05 and
        // 15 was half again over the largest. With the mountain spectrum the same method --
        // 20,000 random nodes per depth, half again over the roughest -- gave 350.4 at depth 19,
        // so 526, and the biomes' 1.44 on every band needed SplitFactor 4 at MorphStartFraction
        // 0.10 (2026-10-18).
        //
        // With the mountain belts, ruggedness scales the 5-150 km band alone, up to 2.97, and the
        // fine band is left at the biomes' 0.9 at most. Measured with the bands at
        // EarthReliefMeasuredAt, (1, 3, 1), over each of those: the roughest depth-19 node has a
        // half-relief of 0.173 of its size, 449 in these units, so C is 674 (2026-10-18). That is
        // less than 526 x 1.44: the fine band, no longer amplified, was most of the cost. In units
        // of HeightScale / S0 it still needs SplitFactor 4 -- at 3 no usable MorphStartFraction
        // carries it (asserted below) -- and 0.10 carries it with a little to spare. The arbiter is
        // the runtime: LodSelector names the roughest chunk it generated when the terrain shuts
        // down. If that ever exceeds this, raise it here and re-check.
        //
        // C is scale-free, so it holds at HeightScale 20,000 unchanged; what the allowance sees is
        // C x LodVerticalScale x HeightScale / S0, the figure it saw at 4,000.
        internal const double EarthReliefFactor = 674.0;
        internal static readonly float3 EarthReliefMeasuredAt = new float3(1f, 3f, 1f);

        internal static SurfaceDef Earth => SurfaceDef.CubeSphere(Radius);

        internal static HeightParams Params(int octaveCount) =>
            HeightParams.Create(Earth, Resolution, 0.0, octaveCount, HeightScale, Persistence,
                                Lacunarity, Seed);

        // As EarthStress runs it, with the roughness spectrum.
        internal static HeightParams RoughParams =>
            Params(OctaveCount).WithRoughness(FinePersistence, RoughnessCrossover);

        // As TerrainRoot builds it: flattened by LodVerticalScale over EarthStress's height bound.
        internal static LodMath Lod(in HeightParams p) =>
            LodMath.Create(Earth, p, SplitFactor, MorphStart, Hysteresis, Preload, MaxDepth)
                   .WithVertical(MacroTests.Vertical("EarthStress", RoughParams, LodVerticalScale));

        // C3: at depth 19 the band limit wants octave 23. With the OctaveCount of 20 every other
        // configuration carries, it clamps to 19 -- a 19.8 m finest wavelength over 0.62 m vertex
        // spacing, so the last four depths subdivide smooth ground. It looks like a rendering
        // bug and the cause is one integer; this is the test that would have caught it.
        [Test]
        public void DeepestDepthIsNotOctaveStarved()
        {
            var p = Params(OctaveCount);
            Assert.AreEqual(4, p.K0, "K0 for octave-0 wavelength S0 at resolution 33");

            Assert.IsFalse(p.IsOctaveStarved(MaxDepth),
                $"OctaveCount {OctaveCount} runs out before MaxDepth {MaxDepth}");
            Assert.AreEqual(MaxDepth + p.K0, p.MaxOctave(MaxDepth), "the band limit at MaxDepth is clamped");

            // The same thing as wavelengths: the finest octave sits at Nyquist for the vertex
            // spacing -- at least two vertices per wavelength, and fewer than four, or a finer
            // octave would still have fitted.
            double spacing = LodMath.NominalSize(p.S0, MaxDepth) / (Resolution - 1);
            double finest  = p.OctaveWavelength0 / math.pow(Lacunarity, p.MaxOctave(MaxDepth));
            Assert.GreaterOrEqual(finest, 2.0 * spacing, "the finest octave is below Nyquist");
            Assert.Less(finest, 4.0 * spacing, "a finer octave would still be representable");
            Assert.AreEqual(1.24, finest, 0.01, "finest wavelength at depth 19, metres");
            Assert.AreEqual(0.62, spacing, 0.01, "vertex spacing at depth 19, metres");

            // And the count every other configuration uses starves exactly the last four depths.
            var old = Params(20);
            for (int d = 0; d <= MaxDepth; d++)
                Assert.AreEqual(d >= 16, old.IsOctaveStarved(d), $"OctaveCount 20 at depth {d}");
            double oldFinest = old.OctaveWavelength0 / math.pow(Lacunarity, old.MaxOctave(MaxDepth));
            Assert.AreEqual(19.8, oldFinest, 0.1, "finest wavelength at depth 19 with 20 octaves");
        }

        // C1, revisited in M5b and again for five times the relief: the relief demand is
        // C x HeightScale / S0, and a spectrum with mountains in it has a large C. The allowance is
        // spent in the flattened world, so the demand on it is LodVerticalScale times that. Both
        // directions are asserted, so the reason for the configuration cannot rot into a comment
        // nobody re-derives: Earth's LOD configuration, SplitFactor 4 at MorphStartFraction 0.10,
        // clears Earth's flattened terrain at the relief tests' margins; it does not clear the true
        // terrain, which is why LodVerticalScale exists; and SplitFactor 3 does not clear even the
        // flattened terrain at any MorphStartFraction the inspector allows.
        [Test]
        public void EarthConfigurationClearsTheReliefAllowance()
        {
            var p   = Params(OctaveCount);
            var lod = Lod(p);

            // LodMath.Create accepted 0.10, which it does only under this ceiling.
            Assert.AreEqual(0.2537, LodMath.MaxMorphStartFraction(SplitFactor, Hysteresis, lod.SizeSpread), 1e-4);
            Assert.AreEqual(LodVerticalScale, lod.Vertical.Scale, 1e-7, "the LOD's vertical scale");

            // MaxHalfRelief is a fixed fraction of the node at every depth; demand is not, since C
            // grows with depth -- steeply below the roughness crossover. EarthReliefFactor is taken
            // at the deepest level and applied to all of them, which is conservative everywhere
            // above it. It was measured at the mountain belts' band amplitudes (C7), which
            // DetailDemand holds EarthMacro to; and with EarthMacro's base height added.
            double demand   = MacroTests.DetailDemand(EarthReliefFactor, EarthReliefMeasuredAt, HeightScale, "EarthStress", p.S0, LodVerticalScale);
            double combined = MacroTests.Demand(EarthReliefFactor, EarthReliefMeasuredAt, HeightScale, "EarthStress", p.S0, LodVerticalScale);
            Assert.AreEqual(0.2591, demand,   1e-4, "Earth's flattened relief demand in the mountain belts, as a fraction of node size");
            Assert.AreEqual(0.2595, combined, 1e-4, "Earth's flattened relief demand with EarthMacro, as a fraction of node size");
            for (int d = 0; d <= MaxDepth; d++)
            {
                double allowance = lod.MaxHalfRelief(d) / lod.NominalSize(d);
                Assert.AreEqual(0.6146, allowance, 1e-4, $"depth {d}: allowance as a fraction of node size");
                // Twice over, because the demand is an estimate rather than a bound. Raising
                // HeightScale to about 23,500 fails here; at 100,000 the allowance is overrun
                // outright, as asserted below.
                Assert.Greater(allowance, 2.0 * demand,
                    $"depth {d}: the Earth configuration leaves less than 2x the estimated relief demand");
                // And with EarthMacro's base height added, by the margin StressSceneTests asks.
                Assert.Greater(allowance, MacroTests.CombinedMargin * combined,
                    $"depth {d}: the Earth configuration leaves less than {MacroTests.CombinedMargin}x " +
                    $"the relief demand with the macro layer, {combined:F4}");
            }
            Assert.Less(lod.MaxHalfRelief(MaxDepth) / lod.NominalSize(MaxDepth),
                        LodVerticalScale * EarthReliefFactor * 100000.0 / p.S0,
                "HeightScale 100,000 should overrun the SplitFactor 4 allowance; the C1 table has moved");

            // Unflattened, the same terrain overruns the allowance -- even the roughest node
            // actually measured, C / 1.5, overruns it outright, with no margin asked -- and taking
            // SplitFactor far enough to carry it would cost several times the chunks. That is the
            // case for LodVerticalScale; if this ever passes, it is no longer needed.
            double trueDemand = MacroTests.DetailDemand(EarthReliefFactor, EarthReliefMeasuredAt, HeightScale, "EarthStress", p.S0);
            double allowed    = lod.MaxHalfRelief(MaxDepth) / lod.NominalSize(MaxDepth);
            Assert.AreEqual(1.2957, trueDemand, 1e-4, "Earth's true relief demand, as a fraction of node size");
            Assert.Less(allowed, trueDemand / 1.5,
                "the true relief now fits the allowance; LodVerticalScale may not be needed");
            var nine = LodMath.Create(Earth, p, 9f, MorphStart, Hysteresis, Preload, MaxDepth);
            Assert.Less(nine.MaxHalfRelief(MaxDepth) / nine.NominalSize(MaxDepth), 2.0 * trueDemand,
                "SplitFactor 9 now carries the true relief at the relief tests' margin");

            // And SplitFactor 3 cannot carry this terrain at all: even at the lowest
            // MorphStartFraction the inspector allows, 0.02, its allowance is under twice the demand.
            var three = LodMath.Create(Earth, p, 3f, 0.02f, Hysteresis, Preload, MaxDepth);
            Assert.Less(three.MaxHalfRelief(MaxDepth) / three.NominalSize(MaxDepth), 2.0 * demand,
                "SplitFactor 3 now clears Earth's terrain; SplitFactor 4 and its chunks may not be needed");
        }

        // C2: the resident working set at MaxDepth 19 stays under MaxResidentChunks all the way
        // down a descent from orbit, and its worst case is pinned so a change to SplitFactor or
        // PreloadFactor that quietly multiplies memory fails here rather than in the profiler.
        //
        // Select is WorkingSetTests.Select without the relief, and NodeDistance with zero heights
        // is the selector's own distance for a node with no relief, not a padless copy of it. The
        // mountain spectrum (M5b) pads the roughest nodes by up to a tenth of their size; padding
        // every node by that much, which overstates it, raised M5b's worst case from 8,198 to
        // 8,394, and the budget is 12% over that. The flattened world pads them by what M5b's
        // relief did, since LodVerticalScale x HeightScale is M5b's HeightScale.
        //
        // The pinned camera is the worst over the first quadrant of face 0 at SplitFactor 4, from a
        // 65 x 65 scan of camera positions 2 m up refined on a 1/512 lattice, flattened as the
        // runtime flattens them (2026-10-19): 8,286 at the face corner. Unflattened, M5b's scan
        // found 8,198 at (76/512, 0), on a face edge; LodVertical's anchor moves the flattened
        // terrain 65 km towards the centre (0.8 of its 81 km envelope), which shrinks the world
        // the LOD sees by 1% and adds about that to the working set. At SplitFactor 3 it was
        // 5,118, at (20/256, 0) -- the working set scales as the square of SplitFactor, near
        // enough. A cube's six faces are congruent and the geometry has no terrain in it, so
        // face 0 stands for all of them.
        [Test]
        public void WorkingSetFitsTheResidentBudget()
        {
            var s     = Earth;
            var roots = SurfaceMath.BuildRoots(s);
            var p     = Params(OctaveCount);
            var lod   = Lod(p);

            SurfaceMath.SurfaceFrame(s, roots[0], 0.0, 0.0, out double3 ground, out float3 up);

            // From 10,000 km down to the ground, roughly two steps an octave of altitude. The
            // camera is flattened as TerrainRoot flattens it before handing it to the selector.
            int peak = 0;
            for (double alt = 1e7; alt >= 2.0; alt *= 0.7)
            {
                int n = Select(s, roots, lod, lod.Vertical.MapPoint(s, ground + (double3)up * alt));
                Assert.Less(n, Budget, $"{n} chunks wanted at {alt:F0} m; MaxResidentChunks is {Budget}");
                peak = math.max(peak, n);
            }

            int wanted = Select(s, roots, lod, lod.Vertical.MapPoint(s, ground + (double3)up * 2.0));
            Assert.AreEqual(8286, wanted, 82,
                "the worst-case working set has moved by more than 1%; re-measure it and re-check " +
                "MaxResidentChunks against the new figure");
            Assert.GreaterOrEqual(wanted, peak, "the descent found a worse working set above the ground");
            Assert.Less(wanted, Budget);
        }

        // WorkingSetTests.Select, minus the relief and the job system: every wanted node, every
        // preloaded child, and -- with `held` -- every split hysteresis can hold
        // (currentlySplit: true), which is what a camera that has just backed away leaves behind.
        // Without `held` it is a fresh selection, which LodSelectorTests checks against the real
        // selector so that this replica's figures stand on more than agreeing with themselves.
        internal static int Select(in SurfaceDef s, RootQuad[] roots, in LodMath lod, double3 camera,
                                   bool held = true)
        {
            var wanted = new HashSet<NodeId>();
            var level  = new List<NodeId>();
            var next   = new List<NodeId>();
            for (int q = 0; q < roots.Length; q++) level.Add(new NodeId(q, 0, 0, 0));

            for (int depth = 0; level.Count > 0; depth++)
            {
                foreach (var node in level) wanted.Add(node);
                if (depth >= lod.MaxDepth) break;

                next.Clear();
                foreach (var node in level)
                {
                    double dist = LodMath.NodeDistance(s, roots[node.Quad], node, camera, 0f, 0f, lod.Vertical);
                    if (lod.ShouldSplit(dist, depth, currentlySplit: held))
                        for (int i = 0; i < 4; i++) next.Add(node.Child(i));
                    else if (dist < lod.PreloadDistance(depth))
                        for (int i = 0; i < 4; i++) wanted.Add(node.Child(i));
                }
                (level, next) = (next, level);
            }
            return wanted.Count;
        }

        // C5: on Earth the skirt has to scale with the node. The gap it hides in a transient
        // two-depth state is on the order of the coarse node's relief, twice the demand of its
        // size -- 2.6 node sizes at HeightScale 20,000 with the mountain belts and EarthMacro's
        // base height (MacroTests.Demand), 52% at M5b's 4,000, and 2.6% on the single-slope
        // spectrum before biomes -- which is about 51 m at depth 19 and 106 km at depth 8. The
        // TRUE relief, not the flattened one: a skirt is geometry, and LodVerticalScale flattens
        // only what the LOD measures. SampleScene's absolute 2 m would hide nothing at all;
        // SkirtFraction 2.9 covers the estimate at every depth, 12% over. A skirt is underground,
        // so the cost is culling bounds.
        [Test]
        public void SkirtCoversTheTransientGapAtEveryDepth()
        {
            var    p      = Params(OctaveCount);
            double demand = MacroTests.Demand(EarthReliefFactor, EarthReliefMeasuredAt, HeightScale, "EarthStress", p.S0);

            for (int d = 0; d <= MaxDepth; d++)
            {
                double size = LodMath.NominalSize(p.S0, d);
                double gap  = 2.0 * demand * size;
                Assert.Greater(ChunkMeshLayout.SkirtDrop(SkirtDepth, SkirtFraction, size), gap,
                    $"depth {d}: the skirt is shorter than the relief of a node there");
            }

            // An absolute 2 m skirt, as SampleScene has, covers no depth at all: with the roughness
            // spectrum at HeightScale 20,000 even a depth-19 node's gap is 51 m.
            for (int d = 0; d <= MaxDepth; d++)
            {
                double gap = 2.0 * demand * LodMath.NominalSize(p.S0, d);
                Assert.IsFalse(2.0 > gap, $"depth {d}: an absolute 2 m skirt");
            }
        }
    }
}
