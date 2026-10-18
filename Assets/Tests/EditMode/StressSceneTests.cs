using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The three stress scenes beside EarthStress, against the same three things EarthScaleTests
    // checks for Earth: the octave count against the depth, the LOD relief allowance, and the
    // resident working set. The scenes differ in topology and size, not in LOD policy: all three
    // run Earth's SplitFactor 3, MorphStartFraction 0.15, HysteresisFactor 1.1 and PreloadFactor 1.
    //
    //   SmallPlanetStress  a cube sphere of a quarter Earth's radius, 1,592.75 km
    //   RingworldStress    the inside of a ring of that radius, 1,250 km wide: one row of eight
    //                      root quads, each square in arc length
    //   TerrariumStress    the inside of a cylinder 10 km in radius and 30 km long, 8 x 4 roots
    //
    // UnityEngine-free, so it runs in the out-of-Editor harness. The constants below are copies
    // of each scene's TerrainSettings asset and TerrainRoot, held to them by StressSceneAssetTests.
    public class StressSceneTests
    {
        internal const int   Resolution  = 33;
        internal const float SplitFactor = 3f;
        internal const float Hysteresis  = 1.1f;
        internal const float MorphStart  = 0.15f;
        internal const float Preload     = 1f;
        internal const float SkirtDepth  = 1f;
        internal const float Persistence = 0.5f;
        internal const float Lacunarity  = 2f;
        internal const uint  Seed        = 42u;

        internal sealed class Scene
        {
            public string     Name, Asset;
            public SurfaceDef Surface;
            public int        MaxDepth, OctaveCount, Budget;
            public float      HeightScale, SkirtFraction;

            // C = halfRelief / (nodeSize x HeightScale / S0) for the roughest node at MaxDepth --
            // an ESTIMATE, as in EarthScaleTests, and the runtime's end-of-session report of the
            // roughest chunk is the arbiter.
            public double ReliefFactor;

            // The worst-case working set: the largest over a scan of camera positions 2 m above a
            // root quad, refined on a 1/512 lattice, with every split hysteresis can hold
            // (2026-10-17). 0.5 m and 10 m up give the same figure at each of these.
            public int    WorstQuad, Worst;
            public double WorstU, WorstV;

            public RootQuad[] Roots => SurfaceMath.BuildRoots(Surface);

            public HeightParams Params => HeightParams.Create(Surface, Resolution, 0.0, OctaveCount,
                                                              HeightScale, Persistence, Lacunarity, Seed);

            public LodMath Lod(in HeightParams p) =>
                LodMath.Create(Surface, p, SplitFactor, MorphStart, Hysteresis, Preload, MaxDepth);
        }

        // A quarter of Earth's radius, two depths shallower for the same 0.62 m vertex spacing.
        // HeightScale 6000 is 2.7 times Earth's relief relative to the planet, so this is the scene
        // that spends most of the relief allowance; Earth's C of 15 is an estimate for depth 19 and
        // C grows with depth, so it is conservative at 17. The worst camera is Earth's, (20/256, 0)
        // on a face edge: the selection is scale-free, and only the depth differs.
        internal static readonly Scene SmallPlanet = new Scene
        {
            Name = "SmallPlanetStress", Asset = "Assets/Settings/SmallPlanetTerrainSettings.asset",
            Surface = SurfaceDef.CubeSphere(1592750.0),
            MaxDepth = 17, OctaveCount = 22, Budget = 5100, HeightScale = 6000f, SkirtFraction = 0.12f,
            ReliefFactor = 15.0,
            WorstQuad = 0, WorstU = 20.0 / 256.0, WorstV = 0.0, Worst = 4510,
        };

        // A ring of the small planet's radius, as wide as one column of its eight is long, viewed
        // from inside. Depth 16 gives 0.83 m vertex spacing. The worst camera is near a column
        // seam, partway across the ring -- and mirrored about the middle, at 277/512.
        internal static readonly Scene Ringworld = new Scene
        {
            Name = "RingworldStress", Asset = "Assets/Settings/RingworldTerrainSettings.asset",
            Surface = SurfaceDef.Cylinder(1592750.0, 1250000.0, 8, 1),
            MaxDepth = 16, OctaveCount = 21, Budget = 5600, HeightScale = 4000f, SkirtFraction = 0.18f,
            ReliefFactor = 15.0,
            WorstQuad = 0, WorstU = 42.0 / 512.0, WorstV = 213.0 / 512.0, Worst = 4944,
        };

        // An O'Neill-style interior, 10 km in radius and 30 km long: depth 9 gives 0.65 m vertex
        // spacing. Its C is 9, not 15: TerrainReliefTests' full six-face scan measured 5.89 for the
        // roughest depth-8 node, the worst node's C climbs about 0.75 a depth, and 9 is half again
        // over that one-depth extrapolation -- the same margin Earth's 15 has over its largest
        // sample. The whole interior is within a few split distances of a camera anywhere in it,
        // so every root subdivides and 32 roots want 3,124 chunks; the worst camera is in an
        // interior row, on root 1 (row 1 of column 0). A rim row's worst is 3,020.
        internal static readonly Scene Terrarium = new Scene
        {
            Name = "TerrariumStress", Asset = "Assets/Settings/TerrariumTerrainSettings.asset",
            Surface = SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4),
            MaxDepth = 9, OctaveCount = 14, Budget = 3600, HeightScale = 200f, SkirtFraction = 0.66f,
            ReliefFactor = 9.0,
            WorstQuad = 1, WorstU = 105.0 / 512.0, WorstV = 429.0 / 512.0, Worst = 3124,
        };

        internal static readonly Scene[] All = { SmallPlanet, Ringworld, Terrarium };

        // OctaveCount is exactly enough for MaxDepth: one fewer starves the deepest level (C3 of
        // the M4 plan, the "smooth mush" trap), and every scene reaches sub-metre vertex spacing.
        [Test]
        public void DeepestDepthIsNotOctaveStarved()
        {
            foreach (var sc in All)
            {
                var p = sc.Params;
                Assert.AreEqual(4, p.K0, $"{sc.Name}: K0 for octave-0 wavelength S0 at resolution 33");
                Assert.IsFalse(p.IsOctaveStarved(sc.MaxDepth), $"{sc.Name}: OctaveCount {sc.OctaveCount} runs out before MaxDepth {sc.MaxDepth}");
                Assert.AreEqual(sc.OctaveCount - 1, p.MaxOctave(sc.MaxDepth), $"{sc.Name}: OctaveCount is more than MaxDepth uses");

                double spacing = LodMath.NominalSize(p.S0, sc.MaxDepth) / (Resolution - 1);
                Assert.Less(spacing, 1.0, $"{sc.Name}: vertex spacing at MaxDepth, metres");
                Assert.Greater(spacing, 0.5, $"{sc.Name}: MaxDepth is deeper than sub-metre needs");
            }
        }

        // The relief demand C x HeightScale / S0 is under half the allowance at every depth, as
        // EarthScaleTests asks of Earth, and with each scene's macro layer added
        // (MacroTests.Demand) it is under the allowance by MacroTests.CombinedMargin. So raising
        // a macro asset's BaseHeightScale -- through its copy in MacroTests.StressMacros, which
        // MacroMapsTests holds to the asset -- fails here before it overruns at runtime. A
        // cylinder's allowance is three times a cube sphere's at the same LOD settings (0.39
        // against 0.1146), because its node sizes spread by 1.02 rather than pi/2 -- which is what
        // lets the terrarium carry a HeightScale of a fiftieth of its root node's size at
        // SplitFactor 3.
        [Test]
        public void ReliefAllowanceLeavesTwiceTheEstimatedDemand()
        {
            foreach (var sc in All)
            {
                var p   = sc.Params;
                var lod = sc.Lod(p);
                Assert.Less(MorphStart, LodMath.MaxMorphStartFraction(SplitFactor, Hysteresis, lod.SizeSpread), sc.Name);

                double demand   = MacroTests.DetailDemand(sc.ReliefFactor, sc.HeightScale, sc.Name, p.S0);
                double combined = MacroTests.Demand(sc.ReliefFactor, sc.HeightScale, sc.Name, p.S0);
                for (int d = 0; d <= sc.MaxDepth; d++)
                {
                    double allowance = lod.MaxHalfRelief(d) / lod.NominalSize(d);
                    Assert.Greater(allowance, 2.0 * demand,
                        $"{sc.Name}, depth {d}: allowance {allowance:F4} leaves less than 2x the estimated demand {demand:F4}");
                    Assert.Greater(allowance, MacroTests.CombinedMargin * combined,
                        $"{sc.Name}, depth {d}: allowance {allowance:F4} leaves less than " +
                        $"{MacroTests.CombinedMargin}x the demand with the macro layer, {combined:F4}");
                }
            }

            double cylinder = Ringworld.Lod(Ringworld.Params).MaxHalfRelief(0) / Ringworld.Lod(Ringworld.Params).NominalSize(0);
            double sphere   = SmallPlanet.Lod(SmallPlanet.Params).MaxHalfRelief(0) / SmallPlanet.Lod(SmallPlanet.Params).NominalSize(0);
            Assert.AreEqual(0.39,   cylinder, 1e-4, "a cylinder's allowance at SplitFactor 3");
            Assert.AreEqual(0.1146, sphere,   1e-4, "a cube sphere's allowance at SplitFactor 3");
        }

        // SurfaceMath.MaxNodeSizeSpread's cylinder entry, 1.02, was measured on one 8-column
        // cylinder of near-square root quads, and the spread depends on the shape: it is the arc
        // over the chord of a column, so fewer columns or long thin roots raise it. Both cylinders
        // here are 8 columns of near-square roots, and this re-measures each against the table,
        // since a tabled spread below the geometry's cracks LOD boundaries rather than rounding.
        [Test]
        public void CylinderSizeSpreadStaysUnderTheTable()
        {
            foreach (var sc in new[] { Ringworld, Terrarium })
            {
                var    roots = sc.Roots;
                double s0    = SurfaceMath.NodeWorldSize(sc.Surface, roots[0], new NodeId(0, 0, 0, 0));
                double worst = 1.0;
                for (int d = 1; d <= 8; d++)
                {
                    int    span    = 1 << d;
                    double nominal = s0 / (double)(1L << d);
                    for (int x = 0; x < span; x++)
                    for (int y = 0; y < span; y++)
                        worst = math.max(worst, SurfaceMath.NodeWorldSize(sc.Surface, roots[0], new NodeId(0, d, x, y)) / nominal);
                }
                Assert.LessOrEqual(worst, SurfaceMath.MaxNodeSizeSpread(sc.Surface),
                    $"{sc.Name}: measured node size spread {worst:F5} exceeds the tabled {SurfaceMath.MaxNodeSizeSpread(sc.Surface):F5}");
            }
        }

        // The resident working set stays under MaxResidentChunks all the way down a descent, and
        // its worst case is pinned within 1%, so a change that multiplies memory fails here. Each
        // budget is about 13% over its scene's worst case, the headroom SampleScene and Earth have.
        // EarthScaleTests.Select is the replica LodSelectorTests holds to the real selector.
        [Test]
        public void WorkingSetFitsTheResidentBudget()
        {
            foreach (var sc in All)
            {
                var s     = sc.Surface;
                var roots = sc.Roots;
                var lod   = sc.Lod(sc.Params);

                SurfaceMath.SurfaceFrame(s, roots[sc.WorstQuad], sc.WorstU, sc.WorstV, out double3 ground, out float3 up);

                // From high up to the ground. A cylinder's interior is only as high as its axis.
                double top  = s.Type == SurfaceType.Cylinder ? 0.99 * s.Radius : 1.6 * s.Radius;
                int    peak = 0;
                for (double alt = top; alt >= 2.0; alt *= 0.7)
                {
                    int n = EarthScaleTests.Select(s, roots, lod, ground + (double3)up * alt);
                    Assert.Less(n, sc.Budget, $"{sc.Name}: {n} chunks wanted at {alt:F0} m; MaxResidentChunks is {sc.Budget}");
                    peak = math.max(peak, n);
                }

                int wanted = EarthScaleTests.Select(s, roots, lod, ground + (double3)up * 2.0);
                Assert.AreEqual(sc.Worst, wanted, sc.Worst / 100,
                    $"{sc.Name}: the worst-case working set has moved by more than 1%; re-measure it " +
                    "and re-check MaxResidentChunks against the new figure");
                Assert.GreaterOrEqual(wanted, peak, $"{sc.Name}: the descent found a worse working set above the ground");
                Assert.Greater(sc.Budget, 1.1 * wanted, $"{sc.Name}: MaxResidentChunks has under 10% headroom over the worst case");
                Assert.Less(sc.Budget, 1.2 * wanted, $"{sc.Name}: MaxResidentChunks is far above the worst case, which costs memory for nothing");
            }
        }

        // The skirt hides the transient gap between two depths, on the order of the coarse node's
        // relief: twice the demand, macro layer included (MacroTests.Demand), of its size.
        // SkirtFraction covers that at every depth, about 10% over: 0.12 on the small planet, 0.18
        // on the ring, at their largest biome amplitudes. The terrarium's is large, 0.66
        // of a node, because its hills and its base height are each a fiftieth of its radius; a
        // skirt is underground and its only cost is culling bounds.
        [Test]
        public void SkirtCoversTheTransientGapAtEveryDepth()
        {
            foreach (var sc in All)
            {
                var    p      = sc.Params;
                double demand = MacroTests.Demand(sc.ReliefFactor, sc.HeightScale, sc.Name, p.S0);
                for (int d = 0; d <= sc.MaxDepth; d++)
                {
                    double size = LodMath.NominalSize(p.S0, d);
                    double gap  = 2.0 * demand * size;
                    Assert.Greater(ChunkMeshLayout.SkirtDrop(SkirtDepth, sc.SkirtFraction, size), gap,
                        $"{sc.Name}, depth {d}: the skirt is shorter than the relief of a node there");
                }
            }
        }
    }
}
