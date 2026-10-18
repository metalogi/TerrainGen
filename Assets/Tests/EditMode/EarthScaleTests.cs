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
        internal const float  HeightScale   = 9000f;
        internal const float  SplitFactor   = 3f;
        internal const float  Hysteresis    = 1.1f;
        internal const float  MorphStart    = 0.15f;
        internal const float  Preload       = 1f;
        internal const int    Budget        = 5800;   // MaxResidentChunks
        internal const float  SkirtDepth    = 1f;
        internal const float  SkirtFraction = 0.07f;
        internal const int    OctaveCount   = 24;
        internal const float  Persistence   = 0.5f;
        internal const float  Lacunarity    = 2f;
        internal const uint   Seed          = 42u;

        // The relief demand at Earth depth, C = halfRelief / (nodeSize * HeightScale / S0) for
        // the roughest node -- an ESTIMATE, not a measurement. A face at depth 19 is 4^19 nodes,
        // so nothing has scanned it: 20,000 random depth-19 nodes gave a maximum of 8.05, the
        // same sample at depth 16 already 9.74, and scaling the depth-19 99.9th percentile by
        // depth 8's max/p99.9 ratio gives about 9. 15 is half again over the largest sampled
        // value. The arbiter is the runtime: LodSelector names the roughest chunk it generated
        // when the terrain shuts down. If that ever exceeds this, raise it here and re-check.
        const double EarthReliefFactor = 15.0;

        internal static SurfaceDef Earth => SurfaceDef.CubeSphere(Radius);

        internal static HeightParams Params(int octaveCount) =>
            HeightParams.Create(Earth, Resolution, 0.0, octaveCount, HeightScale, Persistence,
                                Lacunarity, Seed);

        internal static LodMath Lod(in HeightParams p) =>
            LodMath.Create(Earth, p, SplitFactor, MorphStart, Hysteresis, Preload, MaxDepth);

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

        // C1: SplitFactor 3 is safe on Earth even though it was unsafe on SampleScene, because the
        // relief demand is C x HeightScale / S0 and S0 grows with the radius while HeightScale
        // need not. Both directions are asserted, so the table in the plan cannot rot into a
        // comment nobody re-derives: Earth's LOD configuration clears Earth's terrain with room
        // to spare, and does NOT clear SampleScene's.
        [Test]
        public void EarthConfigurationClearsTheReliefAllowance()
        {
            var p   = Params(OctaveCount);
            var lod = Lod(p);

            // LodMath.Create accepted 0.15, which it does only under this ceiling.
            Assert.AreEqual(0.1882, LodMath.MaxMorphStartFraction(SplitFactor, Hysteresis, lod.SizeSpread), 1e-4);

            // MaxHalfRelief is a fixed fraction of the node at every depth; demand is not, since C
            // grows with depth at persistence 0.5. EarthReliefFactor is taken at the deepest
            // level and applied to all of them, which is conservative everywhere above it.
            double plain = EarthReliefFactor * HeightScale / p.S0;
            Assert.AreEqual(0.0130, plain, 1e-4, "Earth relief demand as a fraction of node size");
            // At EarthMacro's largest band amplitude, 2.25 (C7), and with its base height added.
            double demand   = MacroTests.DetailDemand(EarthReliefFactor, HeightScale, "EarthStress", p.S0);
            double combined = MacroTests.Demand(EarthReliefFactor, HeightScale, "EarthStress", p.S0);
            Assert.AreEqual(0.0292, demand,   1e-4, "Earth relief demand at the largest biome amplitude");
            Assert.AreEqual(0.0312, combined, 1e-4, "Earth relief demand with EarthMacro, as a fraction of node size");
            for (int d = 0; d <= MaxDepth; d++)
            {
                double allowance = lod.MaxHalfRelief(d) / lod.NominalSize(d);
                Assert.AreEqual(0.1146, allowance, 1e-4, $"depth {d}: allowance as a fraction of node size");
                // Twice over, because the demand is an estimate rather than a bound. Raising
                // HeightScale to about 40,000 fails here without biomes, about 17,000 with them; at
                // 100,000 the allowance is overrun outright, as asserted below.
                Assert.Greater(allowance, 2.0 * demand,
                    $"depth {d}: the Earth configuration leaves less than 2x the estimated relief demand");
                // And with EarthMacro's base height added, by the margin StressSceneTests asks.
                Assert.Greater(allowance, MacroTests.CombinedMargin * combined,
                    $"depth {d}: the Earth configuration leaves less than {MacroTests.CombinedMargin}x " +
                    $"the relief demand with the macro layer, {combined:F4}");
            }
            Assert.Less(lod.MaxHalfRelief(MaxDepth) / lod.NominalSize(MaxDepth),
                        EarthReliefFactor * 100000.0 / p.S0,
                "HeightScale 100,000 should overrun the SplitFactor 3 allowance; the C1 table has moved");

            // SampleScene's terrain under Earth's LOD configuration: its measured worst node at
            // depth 8 (a full six-face scan, not an estimate) needs five times what SplitFactor 3
            // allows. Not 3.15, which was a sample and would have passed here by less.
            var sp   = HeightParams.Create(SurfaceDef.CubeSphere(Sample.Radius), Sample.Resolution, 0.0,
                                           Sample.OctaveCount, Sample.HeightScale, Sample.Persistence,
                                           Sample.Lacunarity, Sample.Seed);
            var slod = LodMath.Create(SurfaceDef.CubeSphere(Sample.Radius), sp, SplitFactor, MorphStart,
                                      Hysteresis, Sample.Preload, Sample.MaxDepth);
            double sampleDemand    = Sample.WorstReliefFactor * Sample.HeightScale / sp.S0;
            double sampleAllowance = slod.MaxHalfRelief(Sample.MaxDepth) / slod.NominalSize(Sample.MaxDepth);
            Assert.AreEqual(0.601, sampleDemand, 1e-3, "SampleScene's relief demand at depth 8");
            Assert.Greater(sampleDemand, 5.0 * sampleAllowance,
                "SampleScene's terrain should overrun the Earth LOD configuration about 5x over");
        }

        // C2: the resident working set at MaxDepth 19 stays under MaxResidentChunks all the way
        // down a descent from orbit, and its worst case is pinned so a change to SplitFactor or
        // PreloadFactor that quietly multiplies memory fails here rather than in the profiler.
        //
        // Select is WorkingSetTests.Select without the relief. At Earth scale the roughest node's
        // pad is about 0.013 of its size at EarthReliefFactor, against 0.60 on SampleScene, so
        // zero heights are close enough to the truth -- and NodeDistance with zero heights is the
        // selector's own distance for a node with no relief, not a padless copy of it.
        //
        // The pinned camera is the worst over face 0, from a 257 x 257 scan of camera positions
        // at 0.5, 2 and 10 m altitude on 2026-10-10: 5118 at 0.5 and 2 m, 5110 at 10 m, all at
        // (u, v) = (20/256, 0) -- on a face edge, near but not at the corner. With no split held by
        // hysteresis the worst is 4458, at the corner itself. A cube's six faces are congruent and
        // the geometry has no terrain in it, so face 0 stands for all of them.
        [Test]
        public void WorkingSetFitsTheResidentBudget()
        {
            var s     = Earth;
            var roots = SurfaceMath.BuildRoots(s);
            var p     = Params(OctaveCount);
            var lod   = Lod(p);

            SurfaceMath.SurfaceFrame(s, roots[0], 20.0 / 256.0, 0.0, out double3 ground, out float3 up);

            // From 10,000 km down to the ground, roughly two steps an octave of altitude.
            int peak = 0;
            for (double alt = 1e7; alt >= 2.0; alt *= 0.7)
            {
                int n = Select(s, roots, lod, ground + (double3)up * alt);
                Assert.Less(n, Budget, $"{n} chunks wanted at {alt:F0} m; MaxResidentChunks is {Budget}");
                peak = math.max(peak, n);
            }

            int wanted = Select(s, roots, lod, ground + (double3)up * 2.0);
            Assert.AreEqual(5118, wanted, 51,
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
                    double dist = LodMath.NodeDistance(s, roots[node.Quad], node, camera, 0f, 0f);
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
        // size -- 6.2% at EarthReliefFactor, at the largest biome amplitude and with EarthMacro's
        // base height (MacroTests.Demand); 2.6% for the detail noise alone -- which is about a
        // metre at depth 19 and two kilometres at depth 8. SampleScene's absolute 2 m would hide
        // nothing coarser than depth 19; SkirtFraction 0.07 covers the estimate at every depth,
        // 12% over.
        [Test]
        public void SkirtCoversTheTransientGapAtEveryDepth()
        {
            var    p      = Params(OctaveCount);
            double demand = MacroTests.Demand(EarthReliefFactor, HeightScale, "EarthStress", p.S0);

            for (int d = 0; d <= MaxDepth; d++)
            {
                double size = LodMath.NominalSize(p.S0, d);
                double gap  = 2.0 * demand * size;
                Assert.Greater(ChunkMeshLayout.SkirtDrop(SkirtDepth, SkirtFraction, size), gap,
                    $"depth {d}: the skirt is shorter than the relief of a node there");
            }

            // An absolute 2 m skirt, as SampleScene has, covers only the finest depth.
            for (int d = 0; d <= MaxDepth; d++)
            {
                double gap = 2.0 * demand * LodMath.NominalSize(p.S0, d);
                Assert.AreEqual(d >= 19, 2.0 > gap, $"depth {d}: an absolute 2 m skirt");
            }
        }
    }
}
