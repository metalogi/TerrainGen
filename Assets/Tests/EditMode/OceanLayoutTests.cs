using System;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The ocean's configuration and layout (Docs/Ocean_A_Plan.md), UnityEngine-free so the
    // out-of-Editor harness runs it: OceanParams, the kinds, the per-kind layout and the water's skirt.
    public class OceanLayoutTests
    {
        const int    R        = 33;
        const double Envelope = 36600.0;

        static OceanParams Create(bool ocean = true, double sea = 0.0, float overlap = 0f, int resolution = R,
                                  double envelope = Envelope, bool erosion = false, double baseLevel = 0.0) =>
            OceanParams.Create(ocean, sea, overlap, resolution, envelope, erosion, baseLevel);

        // default(OceanParams) is "no ocean", and so is a scene with Ocean off, whatever its sea
        // level: every field at zero, so no path can read a stale sea from a disabled ocean.
        [Test]
        public void DefaultOceanParamsMeanNoOcean()
        {
            Assert.IsFalse(default(OceanParams).Enabled);
            Assert.AreEqual(default(OceanParams), OceanParams.Disabled);
            Assert.AreEqual(default(OceanParams), Create(ocean: false, sea: 68.0, overlap: 5f));

            var on = Create(sea: 68.0, overlap: 5f);
            Assert.IsTrue(on.Enabled);
            Assert.AreEqual(68.0, on.SeaLevel);
            Assert.AreEqual(68f,  on.SeaLevelFloat);
            Assert.AreEqual(5f,   on.TerrainOverlap);
            Assert.AreEqual(0f,   on.WaveAmplitude, "no waves until ocean-b");
        }

        // C1: the sea lies strictly inside the terrain's height envelope, either side of the base
        // surface. Outside it the whole world is one kind and the sea leaves the band LodVertical
        // measures in. With the ocean off the sea level is only checked for being a number.
        [Test]
        public void SeaLevelOutsideTheEnvelopeIsRefused()
        {
            double inside = Envelope - 1e-6;
            Assert.DoesNotThrow(() => Create(sea: inside));
            Assert.DoesNotThrow(() => Create(sea: -inside));
            Assert.Throws<ArgumentOutOfRangeException>(() => Create(sea: Envelope));
            Assert.Throws<ArgumentOutOfRangeException>(() => Create(sea: -Envelope));
            Assert.Throws<ArgumentOutOfRangeException>(() => Create(sea: 0.0, envelope: double.NaN),
                "an envelope that is not a number bounds nothing");
            Assert.DoesNotThrow(() => Create(ocean: false, sea: 2.0 * Envelope));

            foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => Create(sea: bad));
                Assert.Throws<ArgumentOutOfRangeException>(() => Create(ocean: false, sea: bad));
            }
            foreach (float bad in new[] { -1f, float.NaN, float.PositiveInfinity })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => Create(overlap: bad));
                Assert.Throws<ArgumentOutOfRangeException>(() => Create(ocean: false, overlap: bad));
            }
        }

        // A3: with erosion the sea level is its base level, ocean or not, because the erosion's
        // outlets are the sea. Compared as the float the base level is set as.
        [Test]
        public void SeaLevelMustBeTheErosionBaseLevel()
        {
            Assert.DoesNotThrow(() => Create(sea: 68.0, erosion: true, baseLevel: 68.0));
            Assert.Throws<ArgumentException>(() => Create(sea: 68.0, erosion: true, baseLevel: 0.0));
            Assert.Throws<ArgumentException>(() => Create(ocean: false, sea: 68.0, erosion: true, baseLevel: 0.0),
                "the check holds with the ocean off");
            Assert.Throws<ArgumentException>(() => OceanParams.CheckSeaLevel(0.0, true, 68.0));
            Assert.DoesNotThrow(() => Create(sea: 68.0, erosion: false, baseLevel: 0.0),
                "without erosion there is no base level to agree with");
            Assert.DoesNotThrow(() => Create(sea: 0.1, erosion: true, baseLevel: 0.1f),
                "a sea level of 0.1 is the base level 0.1f");
        }

        // C4: a Coast chunk holds two grids, so with an ocean the 16-bit limit falls from R 253 to
        // R 179. HeightParams cannot see the ocean, so OceanParams refuses it.
        [Test]
        public void OceanCapsTheResolution()
        {
            Assert.LessOrEqual(2 * ChunkMeshLayout.VertexCount(179), 65536, "two R = 179 grids fit 16-bit indices");
            Assert.Greater(2 * ChunkMeshLayout.VertexCount(181), 65536,     "two R = 181 grids do not");
            Assert.AreEqual(179, ChunkMeshLayout.MaxResolutionWithOcean);

            Assert.DoesNotThrow(() => Create(resolution: ChunkMeshLayout.MaxResolutionWithOcean));
            Assert.Throws<ArgumentOutOfRangeException>(() => Create(resolution: 181));
            Assert.DoesNotThrow(() => Create(ocean: false, resolution: 181),
                "without an ocean the terrain's own limit, 253, is HeightParams' to enforce");

            // The index buffers agree on where the limit is: a doubled buffer at 179, with its last
            // vertex still addressable, and none at 181, while a single one still reaches 253.
            int n179 = ChunkMeshLayout.VertexCount(179);
            var doubled = ChunkMeshLayout.BuildIndices(179, false, true);
            int top = 0;
            foreach (ushort v in doubled) top = Math.Max(top, v);
            Assert.AreEqual(2 * n179 - 1, top, "the doubled buffer at R = 179 reaches its last water vertex");
            Assert.Throws<ArgumentOutOfRangeException>(() => ChunkMeshLayout.BuildIndices(181, false, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => ChunkMeshLayout.BuildIndices(181, true, true));
            Assert.DoesNotThrow(() => ChunkMeshLayout.BuildIndices(181, false, false));
            Assert.DoesNotThrow(() => ChunkMeshLayout.BuildIndices(ChunkMeshLayout.MaxResolutionFor16BitIndices, false, false),
                "a scene without an ocean keeps R 253");
        }

        // A1: the kind is decided by the drawn range alone. Land above the sea plus the waves, Ocean
        // under the sea minus the overlap, Coast between and on both ties; the overlap moves only
        // the Ocean threshold and the waves only the Land one. Anything undecidable is Coast, which
        // draws everything.
        [Test]
        public void KindIsDecidedByTheDrawnRange()
        {
            const float sea = 68f;
            ChunkKind K(float lo, float hi, float overlap = 0f, float wave = 0f) =>
                ChunkKinds.Classify(lo, hi, sea, overlap, wave);

            Assert.AreEqual(0, (int)ChunkKind.Land);
            Assert.AreEqual(1, (int)ChunkKind.Coast);
            Assert.AreEqual(2, (int)ChunkKind.Ocean);

            Assert.AreEqual(ChunkKind.Land,  K(68.5f, 900f));
            Assert.AreEqual(ChunkKind.Coast, K(sea,   900f), "a range touching the sea from above is Coast");
            Assert.AreEqual(ChunkKind.Ocean, K(-900f, 67.5f));
            Assert.AreEqual(ChunkKind.Coast, K(-900f, sea),  "a range touching the sea from below is Coast");
            Assert.AreEqual(ChunkKind.Coast, K(-900f, 900f));
            Assert.AreEqual(ChunkKind.Land,  K(70f, 70f),    "a flat range above the sea");
            Assert.AreEqual(ChunkKind.Ocean, K(60f, 60f),    "a flat range under it");
            Assert.AreEqual(ChunkKind.Coast, K(sea, sea),    "a flat range at it");

            // The overlap keeps terrain drawn under shallow water.
            Assert.AreEqual(ChunkKind.Ocean, K(-900f, 62.5f, overlap: 5f));
            Assert.AreEqual(ChunkKind.Coast, K(-900f, 63f,   overlap: 5f), "the overlap's tie");
            Assert.AreEqual(ChunkKind.Coast, K(-900f, 67.5f, overlap: 5f));
            Assert.AreEqual(ChunkKind.Land,  K(68.5f, 900f,  overlap: 5f), "the overlap does not move Land");

            // The waves keep water drawn over a low shore.
            Assert.AreEqual(ChunkKind.Land,  K(70.5f, 900f, wave: 2f));
            Assert.AreEqual(ChunkKind.Coast, K(70f,   900f, wave: 2f), "the waves' tie");
            Assert.AreEqual(ChunkKind.Coast, K(68.5f, 900f, wave: 2f));
            Assert.AreEqual(ChunkKind.Ocean, K(-900f, 67.5f, wave: 2f), "the waves do not move Ocean");

            // Not a number at either end, or for the sea, and a range that is empty, lo above hi.
            Assert.AreEqual(ChunkKind.Coast, K(float.NaN, 900f));
            Assert.AreEqual(ChunkKind.Coast, K(70f, float.NaN));
            Assert.AreEqual(ChunkKind.Coast, K(float.NaN, 60f));
            Assert.AreEqual(ChunkKind.Coast, K(-900f, float.NaN));
            Assert.AreEqual(ChunkKind.Coast, ChunkKinds.Classify(70f, 900f, float.NaN, 0f, 0f));
            Assert.AreEqual(ChunkKind.Coast, ChunkKinds.Classify(-900f, 60f, float.NaN, 0f, 0f));
            Assert.AreEqual(ChunkKind.Coast, K(float.MaxValue, float.MinValue), "the empty range a min-max scan starts from");
            Assert.AreEqual(ChunkKind.Coast, K(900f, 70f));
            Assert.AreEqual(ChunkKind.Coast, K(60f, -900f));
        }

        // A7: per kind, the counts, the water's first vertex, and the doubled buffer, which is the
        // single one and then the single one offset by n. The water winds as the terrain does on
        // every topology, by GridTrianglesFaceOutwardOnEveryTopology's check, over every grid
        // triangle of both halves.
        [Test]
        public void OceanIndexLayout()
        {
            foreach (int r in new[] { 5, 33, 65 })
            {
                int n = ChunkMeshLayout.VertexCount(r), I = ChunkMeshLayout.IndexCount(r);
                Assert.AreEqual(n,     ChunkMeshLayout.VertexCount(r, ChunkKind.Land),  $"R={r} Land vertices");
                Assert.AreEqual(2 * n, ChunkMeshLayout.VertexCount(r, ChunkKind.Coast), $"R={r} Coast vertices");
                Assert.AreEqual(n,     ChunkMeshLayout.VertexCount(r, ChunkKind.Ocean), $"R={r} Ocean vertices");
                Assert.AreEqual(I,     ChunkMeshLayout.IndexCount(r, ChunkKind.Land),   $"R={r} Land indices");
                Assert.AreEqual(2 * I, ChunkMeshLayout.IndexCount(r, ChunkKind.Coast),  $"R={r} Coast indices");
                Assert.AreEqual(I,     ChunkMeshLayout.IndexCount(r, ChunkKind.Ocean),  $"R={r} Ocean indices");
                Assert.AreEqual(n, ChunkMeshLayout.WaterBase(r, ChunkKind.Coast), $"R={r}: Coast water follows the terrain");
                Assert.AreEqual(0, ChunkMeshLayout.WaterBase(r, ChunkKind.Ocean), $"R={r}: Ocean water starts at 0");
                Assert.Throws<ArgumentOutOfRangeException>(() => ChunkMeshLayout.WaterBase(r, ChunkKind.Land));

                foreach (bool flip in new[] { false, true })
                {
                    var single  = ChunkMeshLayout.BuildIndices(r, flip);
                    var doubled = ChunkMeshLayout.BuildIndices(r, flip, true);
                    CollectionAssert.AreEqual(single, ChunkMeshLayout.BuildIndices(r, flip, false),
                        $"R={r} flip={flip}: the single buffer is today's");
                    Assert.AreEqual(ChunkMeshLayout.IndexCount(r, ChunkKind.Coast), doubled.Length, $"R={r} flip={flip}");

                    var water = new bool[n];
                    for (int k = 0; k < I; k++)
                    {
                        Assert.AreEqual(single[k],     doubled[k],     $"R={r} flip={flip}: the terrain half at {k}");
                        Assert.AreEqual(single[k] + n, doubled[I + k], $"R={r} flip={flip}: the water half at {k}");
                        water[doubled[I + k] - n] = true;
                    }
                    Assert.IsTrue(Array.TrueForAll(water, x => x), $"R={r} flip={flip}: a water vertex no triangle uses");
                }
            }

            void Check(SurfaceDef s, string name, double sea)
            {
                const int r = 5;
                int  n     = ChunkMeshLayout.VertexCount(r), I = ChunkMeshLayout.IndexCount(r);
                bool flip  = SurfaceMath.UvFrameIsRightHanded(s);
                var  idx   = ChunkMeshLayout.BuildIndices(r, flip, true);
                var  root  = SurfaceMath.BuildRoots(s)[0];

                // A grid vertex of either half, and its normal: the water's is offset along the
                // normal by the sea, as ChunkMeshJob will place it.
                double3 P(int v, out float3 nrm)
                {
                    int g = v >= n ? v - n : v;
                    SurfaceMath.SurfaceFrame(s, root, (g % r) / (r - 1.0), (g / r) / (r - 1.0), out double3 p, out nrm);
                    return v >= n ? p + (double3)nrm * sea : p;
                }

                // Unity's convention: the face normal of (a, b, c) is cross(b - a, c - a).
                int gridIndices = (r - 1) * (r - 1) * 6;
                foreach (int half in new[] { 0, I })
                for (int t = half; t < half + gridIndices; t += 3)
                {
                    double3 a = P(idx[t], out float3 nrm), b = P(idx[t + 1], out _), c = P(idx[t + 2], out _);
                    Assert.Greater(math.dot(math.cross(b - a, c - a), (double3)nrm), 0.0,
                        $"{name}: {(half == 0 ? "terrain" : "water")} triangle {(t - half) / 3} faces inward");
                }
            }

            Check(SurfaceDef.CubeSphere(1000.0),          "CubeSphere", 30.0);
            Check(SurfaceDef.PlaneGrid(100.0, 2, 2),      "PlaneGrid",  3.0);
            Check(SurfaceDef.Cylinder(1000.0, 4000.0, 8), "Cylinder",   30.0);
        }

        // The water is a smooth surface at the sea level, R + SeaLevel (R - SeaLevel inside a
        // cylinder, where the normal points at the axis), so its whole transient gap is the
        // coarse side's chords cutting under the curve, and its skirt, with no relief term, has
        // to cover that with the floor and the chord sag alone. Every stress scene at every depth,
        // every depth gap to SkirtDepthGap, morph 0, 1/2 and 1 on each side, through the rule the
        // scheduler will call. The parent plan's test 7 without waves; ocean-b adds them.
        [Test]
        public void WaterSkirtCoversTheChordSag()
        {
            var scenes = new[]
            {
                (name: "EarthStress", surface: EarthScaleTests.Earth, rough: EarthScaleTests.RoughParams,
                 depth: EarthScaleTests.MaxDepth, fraction: EarthScaleTests.SkirtFraction),
                (name: StressSceneTests.SmallPlanet.Name, surface: StressSceneTests.SmallPlanet.Surface,
                 rough: StressSceneTests.SmallPlanet.RoughParams, depth: StressSceneTests.SmallPlanet.MaxDepth,
                 fraction: StressSceneTests.SmallPlanet.SkirtFraction),
                (name: StressSceneTests.Ringworld.Name, surface: StressSceneTests.Ringworld.Surface,
                 rough: StressSceneTests.Ringworld.RoughParams, depth: StressSceneTests.Ringworld.MaxDepth,
                 fraction: StressSceneTests.Ringworld.SkirtFraction),
                (name: StressSceneTests.NarrowRing.Name, surface: StressSceneTests.NarrowRing.Surface,
                 rough: StressSceneTests.NarrowRing.RoughParams, depth: StressSceneTests.NarrowRing.MaxDepth,
                 fraction: StressSceneTests.NarrowRing.SkirtFraction),
                (name: StressSceneTests.Terrarium.Name, surface: StressSceneTests.Terrarium.Surface,
                 rough: StressSceneTests.Terrarium.RoughParams, depth: StressSceneTests.Terrarium.MaxDepth,
                 fraction: StressSceneTests.Terrarium.SkirtFraction),
            };
            foreach (var sc in scenes)
            {
                // The sea level is the erosion's base level (A3), which StressSceneAssetTests holds
                // each asset to: 0, and 68 m in the terrarium.
                float sea = MacroTests.For(sc.name).Erosion.Value.BaseLevel;
                var w     = StressSceneTests.SceneHeights.Water(sc.name, sc.surface, sc.rough, sea);
                w.ReliefStride = 8;
                var edges = StressSceneTests.TransientEdges(w, sc.depth, 1, StressSceneTests.SkirtSeed);

                double worst = 0.0;
                int cross = 0;
                foreach (var e in edges)
                {
                    Assert.AreEqual(0.0, e.ReliefFine,   $"{sc.name}: the water has relief");
                    Assert.AreEqual(0.0, e.ReliefCoarse, $"{sc.name}: the water has relief");
                    double fine   = Drop(w, e.Depth, sc.fraction);
                    double coarse = Drop(w, e.Depth - e.Gap, sc.fraction);
                    Assert.LessOrEqual(e.GapFine, fine,
                        $"{sc.name} water, depth {e.Depth} beside {e.Depth - e.Gap}: the fine side stands {e.GapFine:G4} m " +
                        $"above, and its skirt drops {fine:G4} m");
                    Assert.LessOrEqual(e.GapCoarse, coarse,
                        $"{sc.name} water, depth {e.Depth} beside {e.Depth - e.Gap}: the coarse side stands {e.GapCoarse:G4} m " +
                        $"above, and its skirt drops {coarse:G4} m");
                    worst = Math.Max(worst, Math.Max(e.GapFine, e.GapCoarse));
                    if (e.CrossQuad) cross++;
                }
                int expected = 0;
                for (int d = 1; d <= sc.depth; d++) expected += Math.Min(d, ChunkMeshLayout.SkirtDepthGap);
                Assert.AreEqual(expected, edges.Count, $"{sc.name}: every depth and gap has an edge");
                Assert.Greater(cross, 0, $"{sc.name}: no edge crosses a root quad");
                TestContext.WriteLine($"{sc.name}: worst water gap {worst:G4} m");

                // Only where the curve opens a gap past the floor does this test the sag. Measured
                // (2026-10-29): Earth 6,403 m, the small planet 1,601, the ring 30.0, the terrarium
                // 2.99, and the narrow ring 0.90, whose roots are too narrow to open one, as in
                // ChordSagCoversASmoothSurface; there the floor covers it alone.
                if (sc.name != StressSceneTests.NarrowRing.Name)
                    Assert.Greater(worst, StressSceneTests.SkirtDepth,
                        $"{sc.name}: the floor alone covers every gap; the chord sag is not tested");
            }
        }

        // The water's drop at this depth, as the scheduler will compute it: the chord sag the
        // terrain gets, and WaterSkirtDrop.
        static double Drop(StressSceneTests.SceneHeights w, int depth, float fraction)
            => ChunkMeshLayout.WaterSkirtDrop(StressSceneTests.SkirtDepth, fraction,
                   ChunkMeshLayout.SkirtChordSag(w.Surface, fraction, LodMath.NominalSize(w.S0, depth), StressSceneTests.Resolution));
    }
}
