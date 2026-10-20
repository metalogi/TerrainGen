using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The three stress scenes beside EarthStress, against the same three things EarthScaleTests
    // checks for Earth: the octave count against the depth, the LOD relief allowance, and the
    // resident working set. All three run Earth's HysteresisFactor 1.1 and PreloadFactor 1. The
    // small planet also runs Earth's SplitFactor 4, at MorphStartFraction 0.12; the ring and the
    // terrarium keep SplitFactor 3, at 0.14 and 0.15, which a cylinder's larger allowance
    // carries (M5b).
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
        internal const float Hysteresis  = 1.1f;
        internal const float Preload     = 1f;
        internal const float SkirtDepth  = 1f;
        internal const float Lacunarity  = 2f;
        internal const uint  Seed        = 42u;

        internal sealed class Scene
        {
            public string     Name, Asset;
            public SurfaceDef Surface;
            public int        MaxDepth, OctaveCount, Budget;
            public float      HeightScale, SkirtFraction;
            public float      SplitFactor = 3f;
            public float      MorphStart = 0.15f;
            public float      Persistence = 0.5f;

            // The LOD measures in a world with every height scaled by this (LodVertical). Every
            // stress scene runs 0.2 with five times its M5b HeightScale, so its LOD sees the
            // relief, and wants the chunks, that M5b sized it for.
            public float      LodVerticalScale = 0.2f;

            // Close-up roughness (C10): octaves finer than RoughnessCrossover metres fall off by
            // FinePersistence. Each scene's 10 m features are the octave amplitude nearest 10 m,
            // TenMetreFeature, pinned by CloseUpRoughnessMeetsItsTarget.
            public double     RoughnessCrossover;
            public float      FinePersistence;
            public double     TenMetreFeature;
            public double     TenMetreTarget = 0.2;   // or a recorded shortfall below it
            public double     TenMetreOnOneSlope;     // HeightScale x 0.5^k at the same octave

            // C = halfRelief / (nodeSize x HeightScale / S0) for the roughest node at MaxDepth --
            // an ESTIMATE, as in EarthScaleTests, and the runtime's end-of-session report of the
            // roughest chunk is the arbiter. With the roughness spectrum: half again over the
            // roughest of 20,000 random nodes per depth (C10's method), and never below the
            // estimate it replaced. Measured with the low, mid and high bands at
            // ReliefMeasuredAt, which MacroTests.DetailDemand holds the scene's macro asset under.
            public double ReliefFactor;
            public float3 ReliefMeasuredAt = new float3(1f, 1f, 1f);

            // The worst-case working set: the largest over a scan of camera positions 2 m above a
            // root quad, refined on a 1/512 lattice, with every split hysteresis can hold
            // (2026-10-17). 0.5 m and 10 m up give the same figure at each of these.
            public int    WorstQuad, Worst;
            public double WorstU, WorstV;

            public RootQuad[] Roots => SurfaceMath.BuildRoots(Surface);

            public HeightParams Params => HeightParams.Create(Surface, Resolution, 0.0, OctaveCount,
                                                              HeightScale, Persistence, Lacunarity, Seed);

            // As the scene runs it: with its roughness spectrum.
            public HeightParams RoughParams => Params.WithRoughness(FinePersistence, RoughnessCrossover);

            // As TerrainRoot builds it: flattened by LodVerticalScale over the scene's height bound.
            public LodMath Lod(in HeightParams p) =>
                LodMath.Create(Surface, p, SplitFactor, MorphStart, Hysteresis, Preload, MaxDepth)
                       .WithVertical(MacroTests.Vertical(Name, RoughParams, LodVerticalScale));
        }

        // A quarter of Earth's radius, two depths shallower for the same 0.62 m vertex spacing.
        // HeightScale 6000 is 2.7 times Earth's relief relative to the planet, so this is the scene
        // that spends most of the relief allowance. The worst camera is Earth's, (20/256, 0) on a
        // face edge: the selection is scale-free, and only the depth differs.
        //
        // The relief spectrum (M5b), Earth's: persistence 0.7 above 5 km and 0.53 below, at
        // HeightScale 10,000 -- 405 m over 5 km, 825 m over 20 km, 1.7 km over 100 km before biomes
        // and ruggedness, three times that in the mountain belts, and 10 m features 1.33 m: five
        // times M5b's 2,000. With the bands at (1, 3, 1) the roughest of 20,000 random nodes per
        // depth has a half-relief of 0.174 of its node at depth 17 at M5b's relief (2026-10-18),
        // C 226.5, and 340 is half again over it. Flattened by LodVerticalScale 0.2, that needs
        // SplitFactor 4 at MorphStartFraction 0.123 or less; the small planet runs 0.12. The worst
        // working set, flattened, is 7,466 beside the face corner (2026-10-19, over the envelope
        // with the macro read's overshoot; M5b's unflattened scan found 7,206), and the budget is
        // 11% over that.
        internal static readonly Scene SmallPlanet = new Scene
        {
            Name = "SmallPlanetStress", Asset = "Assets/Settings/SmallPlanetTerrainSettings.asset",
            Surface = SurfaceDef.CubeSphere(1592750.0),
            MaxDepth = 17, OctaveCount = 22, Budget = 8300, HeightScale = 10000f, SkirtFraction = 1f,
            SplitFactor = 4f, MorphStart = 0.12f, Persistence = 0.7f,
            RoughnessCrossover = 5000.0, FinePersistence = 0.53f, TenMetreFeature = 1.332, TenMetreOnOneSlope = 0.0382,
            ReliefFactor = 340.0, ReliefMeasuredAt = new float3(1f, 3f, 1f),
            WorstQuad = 0, WorstU = 0.0, WorstV = 4.0 / 512.0, Worst = 7466,
        };

        // A ring of the small planet's radius, as wide as one column of its eight is long, viewed
        // from inside. Depth 16 gives 0.83 m vertex spacing. The worst camera is near a column
        // seam, partway across the ring -- and mirrored about the middle, at 277/512.
        //
        // The relief spectrum (M5b): persistence 0.7 above 5 km and 0.5 below, at HeightScale
        // 10,000 -- 575 m over 5 km, 1.2 km over 20 km, 2.4 km over 100 km before biomes and
        // ruggedness, three times that in the mountain belts, and the features nearest 10 m
        // (13.3 m across) 1.13 m: five times M5b's 2,000. With the bands at (1, 3, 1) the roughest
        // of 20,000 nodes per depth has C 113.7 at depth 16 (2026-10-18), and 171 is half again
        // over it. A cylinder's allowance is large enough that the ring stays at SplitFactor 3,
        // flattened by LodVerticalScale 0.2; the belts take its MorphStartFraction from 0.15,
        // where they would leave nothing to spare, to 0.14. The worst working set, flattened, is
        // 5,000 (2026-10-19; M5b's unflattened scan found 4,944).
        internal static readonly Scene Ringworld = new Scene
        {
            Name = "RingworldStress", Asset = "Assets/Settings/RingworldTerrainSettings.asset",
            Surface = SurfaceDef.Cylinder(1592750.0, 1250000.0, 8, 1),
            MaxDepth = 16, OctaveCount = 21, Budget = 5600, HeightScale = 10000f, SkirtFraction = 1f,
            MorphStart = 0.14f, Persistence = 0.7f,
            RoughnessCrossover = 5000.0, FinePersistence = 0.5f, TenMetreFeature = 1.126, TenMetreOnOneSlope = 0.0763,
            ReliefFactor = 171.0, ReliefMeasuredAt = new float3(1f, 3f, 1f),
            WorstQuad = 0, WorstU = 19.0 / 512.0, WorstV = 245.0 / 512.0, Worst = 5000,
        };

        // An O'Neill-style interior, 10 km in radius and 30 km long: depth 9 gives 0.65 m vertex
        // spacing. Its C is 9, not 15: TerrainReliefTests' full six-face scan measured 5.89 for the
        // roughest depth-8 node, the worst node's C climbs about 0.75 a depth, and 9 is half again
        // over that one-depth extrapolation -- the same margin Earth's 15 has over its largest
        // sample. The whole interior is within a few split distances of a camera anywhere in it,
        // so every root subdivides; the worst camera is in an interior row, on root 1 (row 1 of
        // column 0); a rim row's is 3,736. Unflattened, M5b's 32 roots wanted 3,124 chunks.
        // Flattened, they want 3,880 (2026-10-19), 24% more, and the budget went from 3,600 to
        // 4,400 for it: LodVertical's anchor puts the flattened terrain 2.1 km nearer the axis (0.8
        // of its 2.6 km envelope), which in a 10 km cylinder shrinks the world the LOD sees by a
        // fifth. That is the price of the flattened distance never being longer than the true one.
        //
        // Roughness: fine persistence 0.55 below 100 m made M5b's 10 m features 29 cm, and
        // HeightScale 1,000, five times M5b's 200, makes them 1.43 m. The roughest of 20,000 nodes
        // per depth has C 4.8 at that spectrum; 9 still covers it, and flattened by 0.2 the
        // terrarium's allowance has the room M5b's did.
        internal static readonly Scene Terrarium = new Scene
        {
            Name = "TerrariumStress", Asset = "Assets/Settings/TerrariumTerrainSettings.asset",
            Surface = SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4),
            MaxDepth = 9, OctaveCount = 14, Budget = 4400, HeightScale = 1000f, SkirtFraction = 1f,
            RoughnessCrossover = 100.0, FinePersistence = 0.55f, TenMetreFeature = 1.430, TenMetreOnOneSlope = 0.9766,
            ReliefFactor = 9.0,
            WorstQuad = 1, WorstU = 59.0 / 512.0, WorstV = 493.0 / 512.0, Worst = 3880,
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
                Assert.Less(sc.MorphStart, LodMath.MaxMorphStartFraction(sc.SplitFactor, Hysteresis, lod.SizeSpread), sc.Name);

                // Spent in the flattened world, so LodVerticalScale times the true demand.
                double demand   = MacroTests.DetailDemand(sc.ReliefFactor, sc.ReliefMeasuredAt, sc.HeightScale, sc.Name, p.S0, sc.LodVerticalScale);
                double combined = MacroTests.Demand(sc.ReliefFactor, sc.ReliefMeasuredAt, sc.HeightScale, sc.Name, p.S0, sc.LodVerticalScale);
                Assert.AreEqual(sc.LodVerticalScale, lod.Vertical.Scale, 1e-7, $"{sc.Name}: the LOD's vertical scale");
                for (int d = 0; d <= sc.MaxDepth; d++)
                {
                    double allowance = lod.MaxHalfRelief(d) / lod.NominalSize(d);
                    Assert.Greater(allowance, 2.0 * demand,
                        $"{sc.Name}, depth {d}: allowance {allowance:F4} leaves less than 2x the estimated demand {demand:F4}");
                    Assert.Greater(allowance, MacroTests.CombinedMargin * combined,
                        $"{sc.Name}, depth {d}: allowance {allowance:F4} leaves less than " +
                        $"{MacroTests.CombinedMargin}x the demand with the macro layer, {combined:F4}");
                }

                // And unflattened, the same terrain overruns it even at the roughest node actually
                // measured, C / 1.5, with no margin asked: the case for LodVerticalScale.
                double trueDemand = MacroTests.DetailDemand(sc.ReliefFactor, sc.ReliefMeasuredAt, sc.HeightScale, sc.Name, p.S0);
                Assert.Less(lod.MaxHalfRelief(sc.MaxDepth) / lod.NominalSize(sc.MaxDepth), trueDemand / 1.5,
                    $"{sc.Name}: the true relief now fits the allowance; LodVerticalScale may not be needed");
            }

            double cylinder = Ringworld.Lod(Ringworld.Params).MaxHalfRelief(0) / Ringworld.Lod(Ringworld.Params).NominalSize(0);
            double sphere   = SmallPlanet.Lod(SmallPlanet.Params).MaxHalfRelief(0) / SmallPlanet.Lod(SmallPlanet.Params).NominalSize(0);
            Assert.AreEqual(0.42,   cylinder, 1e-4, "a cylinder's allowance at SplitFactor 3, MorphStartFraction 0.14");
            Assert.AreEqual(0.5346, sphere,   1e-4, "the small planet's allowance at SplitFactor 4, MorphStartFraction 0.12");
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
                    int n = EarthScaleTests.Select(s, roots, lod, lod.Vertical.MapPoint(s, ground + (double3)up * alt));
                    Assert.Less(n, sc.Budget, $"{sc.Name}: {n} chunks wanted at {alt:F0} m; MaxResidentChunks is {sc.Budget}");
                    peak = math.max(peak, n);
                }

                int wanted = EarthScaleTests.Select(s, roots, lod, lod.Vertical.MapPoint(s, ground + (double3)up * 2.0));
                Assert.AreEqual(sc.Worst, wanted, sc.Worst / 100,
                    $"{sc.Name}: the worst-case working set has moved by more than 1%; re-measure it " +
                    "and re-check MaxResidentChunks against the new figure");
                Assert.GreaterOrEqual(wanted, peak, $"{sc.Name}: the descent found a worse working set above the ground");
                Assert.Greater(sc.Budget, 1.1 * wanted, $"{sc.Name}: MaxResidentChunks has under 10% headroom over the worst case");
                Assert.Less(sc.Budget, 1.2 * wanted, $"{sc.Name}: MaxResidentChunks is far above the worst case, which costs memory for nothing");
            }
        }

        // C10: every stress scene's 10 m features reach the close-up roughness target, 20 cm or
        // more. The figure is the configured spectrum's amplitude at the octave nearest a 10 m
        // wavelength, HeightScale times OctaveAmplitude -- an octave amplitude, not a measured
        // relief, and before biomes and ruggedness scale it locally. It is pinned as well as
        // bounded, so a retune that quietly smooths the ground, or roughens it past what the
        // pinned C was measured for, fails here. Where a scene falls short of 20 cm its shortfall
        // is the target instead, recorded beside the scene's constants. The figure a single 0.5
        // slope would give at the same HeightScale is asserted too, so the reason for the
        // two-slope spectrum stays on record.
        [Test]
        public void CloseUpRoughnessMeetsItsTarget()
        {
            var scenes = new[]
            {
                (name: "EarthStress", hs: EarthScaleTests.HeightScale, rough: EarthScaleTests.RoughParams,
                 feature: EarthScaleTests.TenMetreFeature, target: EarthScaleTests.TenMetreTarget, flat: EarthScaleTests.TenMetreOnOneSlope),
                (name: SmallPlanet.Name, hs: SmallPlanet.HeightScale, rough: SmallPlanet.RoughParams,
                 feature: SmallPlanet.TenMetreFeature, target: SmallPlanet.TenMetreTarget, flat: SmallPlanet.TenMetreOnOneSlope),
                (name: Ringworld.Name, hs: Ringworld.HeightScale, rough: Ringworld.RoughParams,
                 feature: Ringworld.TenMetreFeature, target: Ringworld.TenMetreTarget, flat: Ringworld.TenMetreOnOneSlope),
                (name: Terrarium.Name, hs: Terrarium.HeightScale, rough: Terrarium.RoughParams,
                 feature: Terrarium.TenMetreFeature, target: Terrarium.TenMetreTarget, flat: Terrarium.TenMetreOnOneSlope),
            };
            foreach (var (name, hs, rough, feature, target, flat) in scenes)
            {
                int k = (int)math.round(math.log2(rough.OctaveWavelength0 / 10.0));
                double now = hs * rough.OctaveAmplitude(k);
                double one = hs * math.pow(0.5, k);
                string at  = $"{name}, octave {k} ({rough.OctaveWavelength0 / math.pow(2.0, k):F1} m)";

                Assert.LessOrEqual(target, 0.2, $"{name}: the target is 20 cm, or a recorded shortfall below it");
                Assert.GreaterOrEqual(now, target, $"{at}: features {now * 100:F1} cm, short of the target");
                Assert.AreEqual(feature, now, 0.0005, $"{at}: the roughness has moved from its pinned figure");
                Assert.AreEqual(flat, one, 0.0005, $"{at}: on one slope of 0.5");
            }
        }

        // ── Skirts ───────────────────────────────────────────────────────────────────────────
        //
        // A skirt hides the gap along an edge where the tree is briefly more than one depth
        // apart, or one apart outside the morph's window. Each chunk's drop comes from its own
        // relief (ChunkMeshLayout.SkirtDrop): SkirtDepth + SkirtFraction x (max - min over its
        // grid + ChunkMeshLayout.ChordSag). These tests build real transient edges, at each
        // scene's real heights, macro layer and biomes included, and check that the side above
        // has the skirt to cover them.
        //
        // Measured on 400 edges per depth and per depth gap of one and two, in all four stress
        // scenes (2026-10-19), what the side above has to cover, net of SkirtDepth, is at most
        // 0.56 of its relief plus chord sag (Earth, depth 10, fine side; 0.47 on the small
        // planet, 0.53 on the ring, 0.33 in the terrarium), and 0.46 on the coarse side. So the
        // stress scenes run SkirtFraction 1, 1.8 times over the worst. The gap itself is at most
        // 0.046 of a node size anywhere, against the 2.1 to 2.9 node sizes the node-relative rule
        // hung: that rule's estimate stacked every worst case on one node, and the gap is the
        // difference of two samplings of one function, far smaller than either one's relief.
        //
        // The tests take one edge per depth and gap, and the relief on every fourth vertex, for
        // time: about 1.5 s of terrain sampling in .NET, several times that under the Editor's
        // Mono. Sampled coarser, the relief is a lower bound on the chunk's, so the drop a test
        // grants is never longer than the one the job hangs.
        internal const int    SkirtEdgesPerDepth = 1;    // per depth gap, of one and two
        internal const int    SkirtReliefStride  = 4;    // SceneHeights.ReliefStride
        internal const uint   SkirtSeed          = 2026u;

        // One transient edge: a chunk at depth Depth beside one Gap depths coarser, across an
        // edge inside one root quad, each drawn at morph factors 0, 1/2 and 1. GapFine is the
        // most the fine side's drawn edge stands above the coarse side's along the surface
        // normal, what the fine chunk's skirt has to hide; GapCoarse the most it stands below,
        // the coarse chunk's. Each relief is max - min over that chunk's R x R grid, which is
        // what ChunkMeshJob measures.
        internal struct TransientEdge
        {
            public int    Depth, Gap;
            public double GapFine, GapCoarse, ReliefFine, ReliefCoarse;
        }

        // A scene's height function as the sample jobs evaluate it, with the macro texels made
        // on demand: a managed MacroGenerator.Build of a planet's pyramid takes tens of seconds,
        // and an edge reads a few hundred texels of it.
        internal sealed class SceneHeights
        {
            readonly SurfaceDef   _s;
            readonly RootQuad[]   _roots;
            readonly HeightParams _hp;
            readonly MacroParams  _mp;
            readonly LazyTexels   _texels;

            public SceneHeights(string scene, in SurfaceDef s, in HeightParams rough, bool smooth = false)
            {
                var m = MacroTests.For(scene);
                _s     = s;
                _roots = SurfaceMath.BuildRoots(s);
                _hp    = rough.WithBands(m.LowBandEdge, m.MidBandEdge);
                _mp    = m.Params;
                if (smooth)
                {
                    // No terrain at all: the surface's own curvature is the whole gap.
                    _hp = HeightParams.Create(s, Resolution, 0.0, 4, 0f, 0.5f, Lacunarity, Seed);
                    var c = m.Config; c.BaseHeightScale = 0f;
                    _mp = MacroParams.Create(s, _hp, c);
                }
                _texels = new LazyTexels(s, _roots, _mp);
            }

            public SurfaceDef Surface => _s;
            public int QuadCount => _roots.Length;
            public double S0 => _hp.S0;

            // The vertex (i, j) of node n at the band limit and macro level of depth band, as
            // HeightSampleJob (band = n.Depth) or CoarseHeightSampleJob (n.Depth - 1) computes it.
            public double3 Vertex(NodeId n, int i, int j, int band, out float height)
            {
                ChunkGrid.VertexUV(n, i, j, Resolution, out double u, out double v);
                SurfaceMath.SurfaceFrame(_s, _roots[n.Quad], u, v, out double3 p, out float3 normal);
                var macro = Biomes.Modulate(
                    MacroSampler.Read(new TexelReader(_texels), _mp, n.Quad, _mp.Layout.MacroLevel(band), u, v),
                    _mp, out _);
                height = TerrainHeightFunction.Height(p, _hp.MaxOctave(band), macro, _hp);
                return p + (double3)(normal * height);
            }

            // Every ReliefStride-th vertex each way, from 0 so the edges and corners are in: at 1
            // what ChunkMeshJob measures, and coarser a lower bound on it, which can only shorten
            // the skirt a test grants.
            public int ReliefStride = 1;

            public double Relief(NodeId n)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int j = 0; j < Resolution; j += ReliefStride)
                for (int i = 0; i < Resolution; i += ReliefStride)
                {
                    Vertex(n, i, j, n.Depth, out float h);
                    lo = math.min(lo, h);
                    hi = math.max(hi, h);
                }
                return (double)(hi - lo);
            }

            public float3 Normal(int quad, double u, double v)
            {
                SurfaceMath.SurfaceFrame(_s, _roots[quad], u, v, out _, out float3 n);
                return n;
            }
        }

        internal sealed class LazyTexels
        {
            readonly SurfaceDef  _s;
            readonly RootQuad[]  _roots;
            readonly MacroParams _p;
            readonly System.Collections.Concurrent.ConcurrentDictionary<int, float4> _made =
                new System.Collections.Concurrent.ConcurrentDictionary<int, float4>();

            public LazyTexels(in SurfaceDef s, RootQuad[] roots, in MacroParams p) { _s = s; _roots = roots; _p = p; }

            // What MacroGenerator.Build stores there: the texel, or the one it is a copy of.
            public float4 Texel(int index) => _made.GetOrAdd(index, t =>
            {
                _p.Layout.Decode(t, out int q, out int l, out int i, out int j);
                _p.Layout.EdgeSource(_s, q, l, i, j, out int sq, out int si, out int sj);
                return MacroGenerator.Texel(_s, _roots[sq], _p, l, si, sj);
            });
        }

        readonly struct TexelReader : IMacroTexels
        {
            readonly LazyTexels _t;
            public TexelReader(LazyTexels t) { _t = t; }
            public float4 Texel(int index) => _t.Texel(index);
        }

        // edgesPerDepth transient edges for each depth from 1 to maxDepth and each depth gap of
        // one and two that fits, at random places on random root quads.
        internal static System.Collections.Generic.List<TransientEdge> TransientEdges(
            SceneHeights w, int maxDepth, int edgesPerDepth, uint seed, int minDepth = 1)
        {
            var list = new System.Collections.Generic.List<TransientEdge>();
            for (int d = minDepth; d <= maxDepth; d++)
            for (int gap = 1; gap <= 2 && gap <= d; gap++)
            for (int e = 0; e < edgesPerDepth; e++)
            {
                var rng = new Unity.Mathematics.Random(seed + (uint)(d * 7919 + gap * 104729 + e * 15485863));
                if ((1 << (d - gap)) < 2) continue;    // the coarse node needs a neighbour in its quad
                list.Add(Measure(w, d, gap, ref rng));
            }
            return list;
        }

        // The coarse node C at depth d - gap, the side of it the fine node F lies across, and F,
        // one of the depth-d nodes along that side. Edges are numbered South, East, North, West,
        // as ChunkMeshLayout walks them.
        static TransientEdge Measure(SceneHeights w, int d, int gap, ref Unity.Mathematics.Random rng)
        {
            int span = 1 << (d - gap), f = 1 << gap;
            int q    = rng.NextInt(0, w.QuadCount);
            int side = rng.NextInt(0, 4);
            int X    = rng.NextInt(side == 3 ? 1 : 0, side == 1 ? span - 1 : span);
            int Y    = rng.NextInt(side == 0 ? 1 : 0, side == 2 ? span - 1 : span);
            int r    = rng.NextInt(0, f);
            var C    = new NodeId(q, d - gap, X, Y);
            var F    = side switch
            {
                0 => new NodeId(q, d, X * f + r, Y * f - 1),
                1 => new NodeId(q, d, (X + 1) * f, Y * f + r),
                2 => new NodeId(q, d, X * f + r, (Y + 1) * f),
                _ => new NodeId(q, d, X * f - 1, Y * f + r),
            };
            int fineSide = (side + 2) & 3;
            bool alongV  = (side & 1) == 1;

            DrawnEdge(w, F, fineSide, out double[] sF, out double3[] fineF, out double3[] coarseF);
            DrawnEdge(w, C, side,     out double[] sC, out double3[] fineC, out double3[] coarseC);

            // Both drawn edges are piecewise linear in the along-edge parameter, and the coarse
            // side's vertices are a subset of the fine side's, so the extremes of the difference
            // lie at the fine side's vertices; four samples a cell also catch the vertices moving
            // along the edge as they morph.
            const int PerCell = 4;
            int    samples = (Resolution - 1) * PerCell;
            double lo = alongV ? F.VMin : F.UMin, hi = alongV ? F.VMax : F.UMax;
            double fixedC = side switch { 0 => C.VMin, 1 => C.UMax, 2 => C.VMax, _ => C.UMin };
            var    sF2 = new double[Resolution]; var pF = new double3[Resolution];
            var    sC2 = new double[Resolution]; var pC = new double3[Resolution];
            double gapFine = 0.0, gapCoarse = 0.0;
            foreach (double kf in Morphs)
            {
                Morph(sF, fineF, coarseF, kf, sF2, pF);
                foreach (double kc in Morphs)
                {
                    Morph(sC, fineC, coarseC, kc, sC2, pC);
                    for (int t = 0; t <= samples; t++)
                    {
                        double x = math.lerp(lo, hi, t / (double)samples);
                        float3 n = alongV ? w.Normal(q, fixedC, x) : w.Normal(q, x, fixedC);
                        double g = math.dot(At(sF2, pF, x) - At(sC2, pC, x), (double3)n);
                        gapFine   = math.max(gapFine, g);
                        gapCoarse = math.max(gapCoarse, -g);
                    }
                }
            }
            return new TransientEdge
            {
                Depth = d, Gap = gap, GapFine = gapFine, GapCoarse = gapCoarse,
                ReliefFine = w.Relief(F), ReliefCoarse = w.Relief(C),
            };
        }

        static readonly double[] Morphs = { 0.0, 0.5, 1.0 };

        // A chunk's edge vertices: their along-edge parameter, their position, and their morph
        // target -- the even vertex at the parent's band, which ChunkMeshJob writes to TEXCOORD2.
        static void DrawnEdge(SceneHeights w, NodeId n, int edge, out double[] s, out double3[] fine, out double3[] coarse)
        {
            int R = Resolution;
            s = new double[R]; fine = new double3[R]; coarse = new double3[R];
            for (int t = 0; t < R; t++)
            {
                int i = edge switch { 1 => R - 1, 3 => 0, _ => t };
                int j = edge switch { 0 => 0, 2 => R - 1, _ => t };
                ChunkGrid.VertexUV(n, i, j, R, out double u, out double v);
                s[t]      = (edge & 1) == 1 ? v : u;
                fine[t]   = w.Vertex(n, i, j, n.Depth, out _);
                coarse[t] = w.Vertex(n, i & ~1, j & ~1, n.Depth - 1, out _);
            }
        }

        // The edge drawn at morph factor k: each vertex slides to its target, the even vertex,
        // whose parameter is its own or the one before.
        static void Morph(double[] s, double3[] fine, double3[] coarse, double k, double[] sOut, double3[] pOut)
        {
            for (int t = 0; t < s.Length; t++)
            {
                sOut[t] = math.lerp(s[t], s[t & ~1], k);
                pOut[t] = math.lerp(fine[t], coarse[t], k);
            }
        }

        static double3 At(double[] s, double3[] p, double x)
        {
            for (int t = 0; t < s.Length - 1; t++)
                if (x <= s[t + 1])
                {
                    double len = s[t + 1] - s[t];
                    return len > 0.0 ? math.lerp(p[t], p[t + 1], math.saturate((x - s[t]) / len)) : p[t + 1];
                }
            return p[s.Length - 1];
        }

        // The drop a chunk at this depth with this relief hangs, as GenerationScheduler and
        // ChunkMeshJob compute it.
        internal static double Drop(in SurfaceDef s, double s0, int depth, float skirtDepth, float skirtFraction, double relief)
        {
            double curvature = s.Type == SurfaceType.PlaneGrid ? 0.0 : 1.0 / s.Radius;
            float  sag       = (float)ChunkMeshLayout.ChordSag(curvature, SurfaceMath.MaxNodeSizeSpread(s),
                                                               LodMath.NominalSize(s0, depth), Resolution);
            return ChunkMeshLayout.SkirtDrop(skirtDepth, skirtFraction, (double)(float)relief + sag);
        }

        // Each side's skirt covers its side of every edge, and the edges are real gaps: the
        // absolute SkirtDepth alone, SampleScene's kind of skirt, leaves most depths open.
        internal static void AssertSkirtsCover(string name, SceneHeights w, int maxDepth, float skirtFraction)
        {
            w.ReliefStride = SkirtReliefStride;
            var edges = TransientEdges(w, maxDepth, SkirtEdgesPerDepth, SkirtSeed);
            int open  = 0;
            foreach (var e in edges)
            {
                double fine   = Drop(w.Surface, w.S0, e.Depth, SkirtDepth, skirtFraction, e.ReliefFine);
                double coarse = Drop(w.Surface, w.S0, e.Depth - e.Gap, SkirtDepth, skirtFraction, e.ReliefCoarse);
                Assert.LessOrEqual(e.GapFine, fine,
                    $"{name}, depth {e.Depth} beside {e.Depth - e.Gap}: the fine side stands {e.GapFine:G4} m " +
                    $"above, and its skirt drops {fine:G4} m (relief {e.ReliefFine:G4} m)");
                Assert.LessOrEqual(e.GapCoarse, coarse,
                    $"{name}, depth {e.Depth} beside {e.Depth - e.Gap}: the coarse side stands {e.GapCoarse:G4} m " +
                    $"above, and its skirt drops {coarse:G4} m (relief {e.ReliefCoarse:G4} m)");
                if (math.max(e.GapFine, e.GapCoarse) > SkirtDepth) open++;
            }
            Assert.Greater(open, edges.Count / 2,
                $"{name}: a {SkirtDepth} m skirt would cover most of these edges; they test nothing");
        }

        [Test]
        public void SkirtCoversTheTransientGap()
        {
            foreach (var sc in All)
                AssertSkirtsCover(sc.Name, new SceneHeights(sc.Name, sc.Surface, sc.RoughParams), sc.MaxDepth, sc.SkirtFraction);
        }

        // On a smooth surface relief is zero, and what is left of the gap is the coarse side's
        // chords cutting under the curve (inside the ring, over it): measured on 80 edges a depth,
        // up to 1.9 km at Earth's depth 3 and 120 m on the ring at depths 2 and 3, which a share
        // of the relief alone would leave open. ChordSag covers it, and without it they are open.
        [Test]
        public void ChordSagCoversASmoothSurface()
        {
            var scenes = new[]
            {
                (name: "EarthStress", surface: EarthScaleTests.Earth, rough: EarthScaleTests.RoughParams,
                 depth: EarthScaleTests.MaxDepth, fraction: EarthScaleTests.SkirtFraction),
                (name: Ringworld.Name, surface: Ringworld.Surface, rough: Ringworld.RoughParams,
                 depth: Ringworld.MaxDepth, fraction: Ringworld.SkirtFraction),
            };
            foreach (var sc in scenes)
            {
                var w     = new SceneHeights(sc.name, sc.surface, sc.rough, smooth: true) { ReliefStride = 8 };
                var edges = TransientEdges(w, sc.depth, 1, SkirtSeed);
                double worst = 0.0;
                foreach (var e in edges)
                {
                    double fine   = Drop(w.Surface, w.S0, e.Depth, SkirtDepth, sc.fraction, e.ReliefFine);
                    double coarse = Drop(w.Surface, w.S0, e.Depth - e.Gap, SkirtDepth, sc.fraction, e.ReliefCoarse);
                    Assert.LessOrEqual(e.GapFine, fine, $"{sc.name} smooth, depth {e.Depth} beside {e.Depth - e.Gap}: fine side");
                    Assert.LessOrEqual(e.GapCoarse, coarse, $"{sc.name} smooth, depth {e.Depth} beside {e.Depth - e.Gap}: coarse side");
                    worst = math.max(worst, math.max(e.GapFine, e.GapCoarse) - SkirtDepth - sc.fraction * e.ReliefFine);
                }
                Assert.Greater(worst, 50.0, $"{sc.name}: the smooth surface's chords open no gap the relief term leaves");
            }
        }

        // No skirt is longer than its surface's radius, at any depth, whatever the terrain. On a
        // sphere a longer one reaches past the centre and out of the far side, drawn from orbit
        // as a fin over the near hemisphere -- which the node-relative rule did at depth 0 (Earth
        // 30,000 km) until the review capped it. Inside a cylinder skirts hang outwards, and a
        // longer one is a curtain outside the ring larger than the ring. The per-chunk rule needs
        // no cap: a chunk's relief is at most twice TerrainHeightFunction.MaxAbsHeight, so the
        // longest drop it can hang at depth d is SkirtDepth + SkirtFraction x (2 x MaxAbsHeight +
        // ChordSag(d)), largest at depth 0 where the sag is. On Earth that is about 2 x 84 km +
        // 328 km of sag, about 500 km against a 6,371 km radius.
        [Test]
        public void NoSkirtReachesPastTheCentre()
        {
            var scenes = new[]
            {
                (name: "EarthStress", surface: EarthScaleTests.Earth, rough: EarthScaleTests.RoughParams,
                 s0: EarthScaleTests.Params(EarthScaleTests.OctaveCount).S0, depth: EarthScaleTests.MaxDepth,
                 fraction: EarthScaleTests.SkirtFraction),
                (name: SmallPlanet.Name, surface: SmallPlanet.Surface, rough: SmallPlanet.RoughParams,
                 s0: SmallPlanet.Params.S0, depth: SmallPlanet.MaxDepth, fraction: SmallPlanet.SkirtFraction),
                (name: Ringworld.Name, surface: Ringworld.Surface, rough: Ringworld.RoughParams,
                 s0: Ringworld.Params.S0, depth: Ringworld.MaxDepth, fraction: Ringworld.SkirtFraction),
                (name: Terrarium.Name, surface: Terrarium.Surface, rough: Terrarium.RoughParams,
                 s0: Terrarium.Params.S0, depth: Terrarium.MaxDepth, fraction: Terrarium.SkirtFraction),
            };
            foreach (var sc in scenes)
            {
                double maxRelief = 2.0 * MacroTests.MaxAbsHeight(sc.name, sc.rough);
                for (int d = 0; d <= sc.depth; d++)
                {
                    double longest = Drop(sc.surface, sc.s0, d, SkirtDepth, sc.fraction, maxRelief);
                    Assert.Less(longest, sc.surface.Radius,
                        $"{sc.name}, depth {d}: a {longest / 1000.0:F0} km skirt is longer than the " +
                        $"{sc.surface.Radius / 1000.0:F0} km radius");
                }
            }
        }
    }
}
