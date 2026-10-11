using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // How rough SampleScene's terrain really is, against how rough LodMath lets it be.
    //
    // LodSelector widens a node's bounding sphere by half its relief, and that pad spends the
    // same LOD boundary budget as node size spread (LodMath.MaxHalfRelief). The demand is
    // measured here as C = halfRelief / (NominalSize(d) * HeightScale / S0): scale-free, so a
    // 300 m sphere and an Earth-radius one give the same C, but NOT flat across depth. At
    // persistence 0.5 every octave coarser than the node contributes the same slope, so the
    // relief of a node grows with the number of octaves it carries -- the mean goes from 1.03
    // at depth 3 to 1.53 at depth 8 -- and the tail of a population of 4^d nodes per face is
    // wider again.
    //
    // The figures pinned below come from a full scan on 2026-10-10: every node at every depth
    // 0..8 on all six faces, 33 x 33 samples each through the same path HeightSampleJob takes,
    // min and max over the main grid as ChunkMeshJob takes them. The worst node at depth 8 is
    // C = 5.888, which at SampleScene's HeightScale/S0 of 0.102 is 0.601 of the node size.
    //
    // M3 recorded 3.15 instead, from 49 nodes per depth on one face, sampled 13 x 13. That is
    // the 98.8th percentile of the depth-7 population it came from, and 2.2% of depth-8 nodes
    // are rougher. SplitFactor 4 was chosen against it, with "23% to spare", and the runtime
    // warning fired on the first play session. The test at the bottom reproduces that number,
    // so the way it went wrong is checked rather than described.
    //
    // UnityEngine-free, so it runs in the out-of-Editor harness.
    public class TerrainReliefTests
    {
        // SampleScene: TerrainRoot.Radius and Assets/Settings/TerrainSettings.asset. Copies,
        // so this file stays UnityEngine-free; MorphStart is the asset's value, not
        // TerrainSettings' C# default. WorkingSetTests reads these too, and its
        // ConstantsMatchSampleScene fails if they drift from the asset or the scene.
        internal const double Radius      = 300.0;
        internal const int    Resolution  = 33;
        internal const int    OctaveCount = 20;
        internal const float  HeightScale = 50f;
        internal const float  Persistence = 0.5f;
        internal const float  Lacunarity  = 2f;
        internal const uint   Seed        = 42u;
        internal const float  SplitFactor = 4f;
        internal const float  MorphStart  = 0.09f;
        internal const float  Hysteresis  = 1.1f;
        internal const float  Preload     = 1.15f;
        internal const int    MaxDepth    = 8;
        internal const int    Budget      = 4000;   // MaxResidentChunks

        // The worst node at each depth over all six faces, and its C, indexed by depth. The
        // depth-8 figure is the one LOD configuration has to clear: there is no deeper level in
        // SampleScene.
        public const double WorstReliefFactor = 5.88755;

        static readonly (NodeId node, double c)[] WorstPerDepth =
        {
            (new NodeId(5, 0,   0,  0), 0.50080),
            (new NodeId(5, 1,   0,  1), 0.94413),
            (new NodeId(5, 2,   1,  2), 1.57156),
            (new NodeId(1, 3,   7,  0), 2.15915),
            (new NodeId(4, 4,  12,  9), 3.04403),
            (new NodeId(1, 5,  28,  4), 3.99718),
            (new NodeId(1, 6,  58,  8), 4.63329),
            (new NodeId(1, 7, 116, 17), 5.31029),
            (new NodeId(1, 8, 233, 33), WorstReliefFactor),
        };

        SurfaceDef   _surface;
        RootQuad[]   _roots;
        HeightParams _params;

        void Setup()
        {
            _surface = SurfaceDef.CubeSphere(Radius);
            _roots   = SurfaceMath.BuildRoots(_surface);
            _params  = HeightParams.Create(_surface, Resolution, 0.0, OctaveCount, HeightScale,
                                           Persistence, Lacunarity, Seed);
        }

        LodMath Lod(float morphStart) =>
            LodMath.Create(_surface, _params, SplitFactor, morphStart, Hysteresis, Preload, MaxDepth);

        double Unit => _params.HeightScale / _params.S0;

        // A chunk's height range exactly as the generation path produces it: ChunkGrid.VertexUV,
        // then SurfaceFrame, then the height function at the node's own band limit, min and max
        // in float over the R x R main grid -- no border ring, no skirts.
        void Relief(NodeId node, out float min, out float max)
        {
            var root = _roots[node.Quad];
            int oct  = _params.MaxOctave(node.Depth);
            min = float.MaxValue;
            max = float.MinValue;
            for (int j = 0; j < Resolution; j++)
            for (int i = 0; i < Resolution; i++)
            {
                ChunkGrid.VertexUV(node, i, j, Resolution, out double u, out double v);
                SurfaceMath.SurfaceFrame(_surface, root, u, v, out double3 p, out float3 _);
                float h = TerrainHeightFunction.Height(p, oct, MacroSample.Zero, _params);
                min = math.min(min, h);
                max = math.max(max, h);
            }
        }

        double HalfRelief(NodeId node)
        {
            Relief(node, out float min, out float max);
            return 0.5 * (max - min);
        }

        // The recorded worst nodes still have the recorded relief. If the height function, the
        // noise or the sampling path changes, this fails -- and the scan behind every figure in
        // this file, in CLAUDE.md and in the M4 plan has to be run again, not patched. The scan
        // is Relief over every node of every face at every depth, which is what the next test
        // does for one face at one depth; out of Editor, on four cores, it takes about 200 s.
        [Test]
        public void WorstNodesHaveTheRecordedRelief()
        {
            Setup();
            var lod = Lod(MorphStart);

            foreach (var (node, c) in WorstPerDepth)
                Assert.AreEqual(c, HalfRelief(node) / lod.NominalSize(node.Depth) / Unit, 1e-3,
                    $"relief of the recorded worst node at depth {node.Depth} has changed; " +
                    "re-run the full scan rather than editing the number");
        }

        // The figures are maxima over every node, not over a sample -- the distinction that
        // cost M3 its number. A whole face at depth 8 is too slow for a unit test, so this
        // re-derives one face at depth 4 in full and checks the recorded node is its maximum.
        [Test]
        public void RecordedWorstIsTheMaximumOverItsFace()
        {
            Setup();
            var (recorded, _) = WorstPerDepth[4];

            NodeId worst = default;
            double most  = -1.0;
            int    span  = 1 << recorded.Depth;
            for (int y = 0; y < span; y++)
            for (int x = 0; x < span; x++)
            {
                var    node = new NodeId(recorded.Quad, recorded.Depth, x, y);
                double half = HalfRelief(node);
                if (half > most) { most = half; worst = node; }
            }

            Assert.AreEqual(recorded, worst,
                $"the roughest node on face {recorded.Quad} at depth {recorded.Depth} is not the recorded one");
        }

        // Depths 0..2 are 126 nodes in all, so the claim "worst over all six faces" is checked
        // in full there rather than on one face. These are the depths with the most absolute
        // relief, and the ones a coarse layer added later -- M5's macro maps -- would hit first.
        [Test]
        public void ShallowRecordsAreTheMaximumOverAllSixFaces()
        {
            Setup();

            for (int d = 0; d <= 2; d++)
            {
                NodeId worst = default;
                double most  = -1.0;
                int    span  = 1 << d;
                for (int q = 0; q < _roots.Length; q++)
                for (int y = 0; y < span; y++)
                for (int x = 0; x < span; x++)
                {
                    var    node = new NodeId(q, d, x, y);
                    double half = HalfRelief(node);
                    if (half > most) { most = half; worst = node; }
                }

                Assert.AreEqual(WorstPerDepth[d].node, worst,
                    $"the roughest node at depth {d} over all six faces is not the recorded one");
            }
        }

        // SampleScene's configuration clears the worst node at every depth, and the configuration
        // it replaced did not. Both directions, so neither half can rot into a comment.
        //
        // MorphStartFraction went from 0.15 to 0.09 rather than HeightScale coming down: it
        // feeds only MorphRange and MaxHalfRelief, never selection, so the terrain and the
        // working set are untouched. What it costs is a morph over 9% of each range instead of
        // 15%.
        [Test]
        public void SampleSceneClearsItsWorstRelief()
        {
            Setup();
            var shipped  = Lod(MorphStart);
            var previous = Lod(0.15f);
            var sf3      = LodMath.Create(_surface, _params, 3f, 0.15f, Hysteresis, Preload, MaxDepth);

            foreach (var (node, _) in WorstPerDepth)
            {
                int    d    = node.Depth;
                double half = HalfRelief(node);

                Assert.Less(half, shipped.MaxHalfRelief(d),
                    $"depth {d}: {half:F3} m of half-relief against {shipped.MaxHalfRelief(d):F3} m " +
                    "allowed; LodSelector will warn and LOD boundaries in the roughest terrain may not close");

                // Depths 0..5 cleared at 0.15 as well; 6..8 did not, by up to 45%.
                Assert.AreEqual(d >= 6, half > previous.MaxHalfRelief(d),
                    $"depth {d}: the MorphStartFraction 0.15 configuration should " +
                    (d >= 6 ? "overrun" : "clear") + " this node");

                // And SplitFactor 3, which M3 shipped before 4, overran at seven of the nine
                // depths -- 2..8 -- by up to 5.2x. CLAUDE.md quotes this; it is pinned here.
                Assert.AreEqual(d >= 2, half > sf3.MaxHalfRelief(d),
                    $"depth {d}: the SplitFactor 3 configuration should " +
                    (d >= 2 ? "overrun" : "clear") + " this node");
            }

            double allowance = shipped.MaxHalfRelief(MaxDepth) / shipped.NominalSize(MaxDepth);
            Assert.Greater(allowance, 1.05 * WorstReliefFactor * Unit,
                "less than 5% headroom over the worst node at the deepest level");
        }

        // The number M3 shipped, reproduced: one face, a 7 x 7 stride of nodes per depth (8 x 8
        // at depths 3 and 4), 13 x 13 samples per node. Its per-depth maxima were noisy enough
        // to look flat, and its worst sat at the 98.8th percentile of the real depth-7
        // population. Recorded here so the method is checked, not just described.
        [Test]
        public void TheOriginalSampleMissedTheTail()
        {
            Setup();
            var    lod   = Lod(MorphStart);
            double worst = 0.0;

            for (int d = 3; d <= MaxDepth; d++)
            {
                int    span   = 1 << d, stride = math.max(1, span / 6);
                int    oct    = _params.MaxOctave(d);
                double most   = 0.0;
                for (int y = 0; y < span; y += stride)
                for (int x = 0; x < span; x += stride)
                {
                    var    node = new NodeId(0, d, x, y);
                    double lo = double.MaxValue, hi = double.MinValue;
                    for (int j = 0; j <= 12; j++)
                    for (int i = 0; i <= 12; i++)
                    {
                        double3 p = SurfaceMath.SurfacePoint(_surface, _roots[0],
                                        math.lerp(node.UMin, node.UMax, i / 12.0),
                                        math.lerp(node.VMin, node.VMax, j / 12.0));
                        float h = TerrainHeightFunction.Height(p, oct, MacroSample.Zero, _params);
                        lo = math.min(lo, h);
                        hi = math.max(hi, h);
                    }
                    most = math.max(most, 0.5 * (hi - lo));
                }
                worst = math.max(worst, most / lod.NominalSize(d) / Unit);
            }

            Assert.AreEqual(3.146, worst, 1e-3, "the M3 sample no longer reproduces its 3.15");
            Assert.Greater(WorstReliefFactor / worst, 1.8,
                "the full scan should find the tail nearly twice as rough as the sample did");
        }
    }
}
