using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // M5c-1, task 4 (Docs/M5_MacroLayer_Plan.md, E4): the drainage maps. Wetness runs from 0 on a
    // ridge to 1 on a river draining a quad; the pass's nodes land on every top-level texel that
    // stores them, edge copies included; and the read takes wetness only where there is erosion,
    // exactly on texels and within 0..1 between them.
    public class ErosionChannelsTests
    {
        [Test]
        public void WetnessRunsFromARidgeToAQuadsRiver()
        {
            const double a = 1.6e9, q = 8.5e13;
            Assert.AreEqual(0f, ErosionChannels.Wetness(a, a, q), "a ridge drains only itself");
            Assert.AreEqual(1f, ErosionChannels.Wetness(q, a, q), "a river drains a quad");
            Assert.AreEqual(1f, ErosionChannels.Wetness(10 * q, a, q), "saturated above a quad");
            Assert.AreEqual(0.5f, ErosionChannels.Wetness(math.sqrt(a * q), a, q), 1e-6f, "logarithmic");

            float last = -1f;
            for (double d = a; d <= q; d *= 3.0)
            {
                float w = ErosionChannels.Wetness(d, a, q);
                Assert.GreaterOrEqual(w, last, $"wetness fell at {d}");
                last = w;
            }
            Assert.AreEqual(0f, ErosionChannels.Wetness(a, q, q), "a node as big as a quad: a ridge");
            Assert.AreEqual(1f, ErosionChannels.Wetness(2 * q, q, q), "and a river");
        }

        // Every texel in [0, n] of the top level holds its node's wetness and D, so the edge copies
        // have nothing left to change; the border is left for the pyramid's halo.
        [Test]
        public void WriteTopFillsEveryQuadAndAgreesOnEveryEdge()
        {
            foreach (var c in ErosionGraphTests.Cases())
            {
                var g = c.Graph;
                var h = ErosionPassTests.PittedHeights(c);
                var p = ErosionPassTests.Params(iterations: 5, kdt: 0.05);
                p.QuadArea = ContinentParams.SurfaceArea(c.Surface) / c.Surface.QuadCount;
                var r = ErosionPass.Run(p, g, h);

                var channel = new float2[c.Layout.TexelCount];
                var unset   = new float2(float.NaN, float.NaN);
                for (int t = 0; t < channel.Length; t++) channel[t] = unset;
                ErosionChannels.WriteTop(c.Layout, g, p, h, r, channel);

                int top = c.Layout.LevelCount - 1, n = g.Cells;
                for (int q = 0; q < c.Surface.QuadCount; q++)
                for (int j = -MacroLayout.Border; j <= n + MacroLayout.Border; j++)
                for (int i = -MacroLayout.Border; i <= n + MacroLayout.Border; i++)
                {
                    float2 got    = channel[c.Layout.Index(q, top, i, j)];
                    bool   inside = i >= 0 && i <= n && j >= 0 && j <= n;
                    if (!inside)
                    {
                        Assert.IsTrue(float.IsNaN(got.x), $"{c.Name}: border texel ({q}, {i}, {j}) written");
                        continue;
                    }
                    int node = g.NodeOf[ErosionGraph.Texel(n, q, i, j)];
                    Assert.AreEqual(ErosionChannels.Wetness(r.Drainage[node], g.Areas[node], p.QuadArea), got.x,
                                    $"{c.Name}: wetness at ({q}, {i}, {j})");
                    Assert.AreEqual((float)(r.Heights[node] - h[node]), got.y, $"{c.Name}: D at ({q}, {i}, {j})");
                    Assert.LessOrEqual(got.y, 0f, $"{c.Name}: D above zero with diffusion off");
                    Assert.That(got.x, Is.InRange(0f, 1f), c.Name);
                }

                foreach (int2 copy in c.Layout.EdgeCopies(c.Surface, top))
                {
                    c.Layout.Decode(copy.x, out _, out _, out int di, out int dj);
                    if (di < 0 || di > n || dj < 0 || dj > n) continue;   // an extension texel: the halo's
                    Assert.AreEqual(channel[copy.y], channel[copy.x], $"{c.Name}: an edge copy would change texel {copy.x}");
                }
            }
        }

        // Without erosion the read never touches the drainage maps; with it, wetness is the stored
        // texel exactly on one, and saturated to 0..1 between them, where the bicubic read overshoots.
        [Test]
        public void ReadTakesWetnessOnlyWithErosion()
        {
            var s  = SurfaceDef.CubeSphere(6371000.0);
            var hp = HeightParams.Create(s, 9, 0.0, 12, 100f, 0.5f, 2f, 42u);
            var p  = MacroParams.Create(s, hp, new MacroConfig
            {
                LevelCount = 3, Persistence = 0.5f, BaseHeightScale = 900f, LapseRate = 6.5f, Seed = 7u,
            });
            var texels   = new float4[p.Layout.TexelCount];
            var drainage = new float2[p.Layout.TexelCount];
            var rng      = new Unity.Mathematics.Random(0xC0FFEEu);
            for (int t = 0; t < drainage.Length; t++) drainage[t] = new float2(rng.NextFloat(), rng.NextFloat(-50f, 0f));
            var maps = new MacroTests.ArrayTexels(texels, drainage);

            Assert.AreEqual(0f, MacroSample.Zero.Wetness, "a default sample has no erosion data");
            Assert.IsFalse(p.HasErosion);
            Assert.AreEqual(0f, MacroSampler.Read(maps, p, 2, 1, 0.3, 0.6).Wetness, "read without erosion");

            p.HasErosion = true;
            for (int level = 0; level < p.Layout.LevelCount; level++)
            {
                int n = p.Layout.Cells(level);
                for (int q = 0; q < 6; q++)
                for (int k = 0; k < 40; k++)
                {
                    int i = rng.NextInt(0, n + 1), j = rng.NextInt(0, n + 1);
                    float got = MacroSampler.Read(maps, p, q, level, i / (double)n, j / (double)n).Wetness;
                    Assert.AreEqual(drainage[p.Layout.Index(q, level, i, j)].x, got, $"on texel ({q}, {level}, {i}, {j})");

                    float between = MacroSampler.Read(maps, p, q, level, rng.NextDouble(), rng.NextDouble()).Wetness;
                    Assert.That(between, Is.InRange(0f, 1f), "between texels");
                }
            }

            p.Enabled = false;
            Assert.AreEqual(0f, MacroSampler.Read(maps, p, 0, 0, 0.5, 0.5).Wetness, "no macro layer at all");
        }
    }
}
