using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // M5c-1, task 5 (Docs/M5_MacroLayer_Plan.md, E4): the drainage maps' derived levels and the
    // fold of D into the base height. The pyramid is MacroPyramid's, which MacroPyramidTests pins
    // (halo, filter, edges, ringing); these hold the float2 component view to the scalar pyramid,
    // and check what is new: every texel written, wetness and D kept in range, edges agreeing
    // after the fold, and the fold adding exactly D. The Burst job is held to it in MacroMapsTests.
    public class ErosionPyramidTests
    {
        internal sealed class Case
        {
            public string           Name;
            public SurfaceDef       Surface;
            public MacroLayout      Layout;
            public MacroPyramidPlan Plan;
            public float4[]         Texels;     // as generated
            public float2[]         Drainage;   // the top level inside the quads; NaN elsewhere
        }

        internal static Case Make(string name, in SurfaceDef s, int cells0, int levels, uint seed = 0x2F6Bu)
        {
            var layout = new MacroLayout(cells0, levels, s.QuadCount);
            var c = new Case
            {
                Name = name, Surface = s, Layout = layout, Plan = MacroPyramidPlan.Create(s, layout),
                Texels = new float4[layout.TexelCount], Drainage = new float2[layout.TexelCount],
            };
            var rng = new Unity.Mathematics.Random(seed);
            for (int t = 0; t < c.Texels.Length; t++)
            {
                c.Texels[t]   = rng.NextFloat4(-3000f, 3000f);
                c.Drainage[t] = new float2(float.NaN, float.NaN);
            }
            // The generated maps after MacroMaps.Build's copies: every edge row its owner's.
            foreach (int2 copy in layout.EdgeCopies(s)) c.Texels[copy.x] = c.Texels[copy.y];

            // What WriteTop leaves: owner values inside every quad, the copies equal to their owners.
            int top = levels - 1, n = layout.Cells(top);
            for (int q = 0; q < s.QuadCount; q++)
            for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
                c.Drainage[layout.Index(q, top, i, j)] = new float2(rng.NextFloat(), rng.NextFloat(-200f, 0f));
            foreach (int2 copy in layout.EdgeCopies(s, top))
            {
                layout.Decode(copy.x, out _, out _, out int ci, out int cj);
                if (ci >= 0 && ci <= n && cj >= 0 && cj <= n) c.Drainage[copy.x] = c.Drainage[copy.y];
            }
            return c;
        }

        internal static Case[] Cases() => new[]
        {
            Make("sphere",    SurfaceDef.CubeSphere(6371000.0),               8, 3),
            Make("ring",      SurfaceDef.Cylinder(1592750.0, 1250000.0, 8, 1), 4, 3),
            Make("terrarium", SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4),     4, 3),
            Make("plane",     SurfaceDef.PlaneGrid(1000.0, 2, 3),              4, 3),
        };

        static (float4[] texels, float2[] drainage) Run(Case c)
        {
            var texels   = (float4[])c.Texels.Clone();
            var drainage = (float2[])c.Drainage.Clone();
            ErosionPyramid.Run(texels, drainage, c.Layout, c.Plan);
            return (texels, drainage);
        }

        [Test]
        public void EachChannelIsTheScalarPyramid()
        {
            foreach (var c in Cases())
            {
                var (_, drainage) = Run(c);
                for (int comp = 0; comp < 2; comp++)
                {
                    var scalar = new float[c.Drainage.Length];
                    for (int t = 0; t < scalar.Length; t++) scalar[t] = c.Drainage[t][comp];
                    MacroPyramid.Run(scalar, c.Layout, c.Plan, MacroPyramidTop.Fill);
                    for (int t = 0; t < scalar.Length; t++)
                        Assert.AreEqual(math.asuint(scalar[t]), math.asuint(drainage[t][comp]),
                                        $"{c.Name}: component {comp}, texel {t}");
                }
            }
        }

        [Test]
        public void EveryTexelIsWrittenAndKeepsItsRange()
        {
            foreach (var c in Cases())
            {
                var (_, drainage) = Run(c);
                float lowest = 0f;
                foreach (float2 d in c.Drainage) if (!float.IsNaN(d.y)) lowest = math.min(lowest, d.y);
                for (int t = 0; t < drainage.Length; t++)
                {
                    Assert.IsFalse(math.any(math.isnan(drainage[t])), $"{c.Name}: texel {t} never written");
                    Assert.That(drainage[t].x, Is.InRange(0f, 1f), $"{c.Name}: wetness at {t}");
                    Assert.That(drainage[t].y, Is.InRange(lowest, 0f), $"{c.Name}: D at {t}");
                }
            }
        }

        // After the fold, both quads at every shared edge store the same bits in all three
        // channels, at every level: the base height reads the same from either side.
        [Test]
        public void EdgesAgreeAfterTheFold()
        {
            foreach (var c in Cases())
            {
                var (texels, drainage) = Run(c);
                foreach (int2 copy in c.Layout.EdgeCopies(c.Surface))
                {
                    Assert.AreEqual(math.asuint(drainage[copy.y]), math.asuint(drainage[copy.x]), $"{c.Name}: drainage at {copy.x}");
                    Assert.AreEqual(math.asuint(texels[copy.y]),   math.asuint(texels[copy.x]),   $"{c.Name}: texel at {copy.x}");
                }
            }
        }

        [Test]
        public void TheFoldAddsExactlyD()
        {
            foreach (var c in Cases())
            {
                var (texels, drainage) = Run(c);
                for (int t = 0; t < texels.Length; t++)
                {
                    float4 was = c.Texels[t], now = texels[t];
                    Assert.AreEqual(was.x + drainage[t].y, now.x, $"{c.Name}: base height at {t}");
                    Assert.AreEqual(math.asuint(was.yzw), math.asuint(now.yzw), $"{c.Name}: climate at {t}");
                }
            }
        }
    }
}
