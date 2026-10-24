using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // Stage B of the continents (Docs/Continents_Plan.md, K2, Task 4): the continent field is
    // continuous, zero on the coast, exactly flat inside a region, and within its bound. On
    // ContinentMapTests' small maps.
    public class ContinentFieldTests
    {
        // No step anywhere: two points close together differ by at most a Lipschitz bound derived
        // from the parameters times their distance. A step of any size -- a missing cap, an even
        // profile's discontinuity at s = 0, a land factor that jumps between macro cells -- is
        // hundreds of metres over a distance where the bound allows centimetres.
        //
        //   * The profile: slope 15/8 of the larger amplitude per CoastWidth of s.
        //   * s = (dO^2 - dL^2) / 2D: each squared distance's gradient is 2d, and each is capped at
        //     (2D)^2, so |grad s| <= (2D + 2D) / D = 4.
        //   * The warp: p' = p + A w(p / lambda), each w three octaves at persistence 0.5 and
        //     lacunarity 2, so its gradient per metre is at most 3 g / lambda per component, and the
        //     stretch at most 1 + A sqrt(3) 3 g / lambda. g = 18 bounds the gradient noise's slope per
        //     cell: a corner's product is at most sqrt(2) sqrt(3), the quintic fade's slope 15/8, so
        //     each axis's derivative is at most 15/8 x 2 sqrt(6) + 1 = 10.2, and the gradient sqrt(3)
        //     times that.
        //   * E on land: every point has a macro seed within R / 1.5, whose weight is at least
        //     (1 - 1/2.25)^2 = 0.309; each weight's gradient is at most 1.54 / R; and |e - E| <= 1;
        //     so |grad E| <= M 1.54 / (0.309 R), times LandHeight x InteriorRise.
        [Test]
        public void FieldIsContinuous()
        {
            foreach (var c in ContinentMapTests.Cases())
            {
                var    map    = ContinentMapTests.Build(c);
                var    tables = new ArrayContinentTables(map);
                var    cp     = c.Continents;
                double amp    = math.max(cp.LandHeight * (1.0 + 0.5 * cp.InteriorRise), cp.OceanDepth);
                double g      = 18.0;
                double stretch = 1.0 + cp.WarpAmplitude * math.sqrt(3.0) * 3.0 * g / cp.WarpWavelength;
                double eSlope = map.MacroSeeds.Length * 1.54 / (0.309 * math.sqrt(map.BlendRadius2));
                double bound  = 15.0 / 8.0 * amp / cp.CoastWidth * 4.0 * stretch + cp.LandHeight * cp.InteriorRise * eSlope;

                var    rng   = new Unity.Mathematics.Random(0xF1E1Du);
                double worst = 0.0;
                for (int k = 0; k < 5000; k++)
                {
                    var    q  = c.Roots[rng.NextInt(c.Roots.Length)];
                    double u  = rng.NextDouble(), v = rng.NextDouble();
                    double du = rng.NextDouble(-1e-4, 1e-4), dv = rng.NextDouble(-1e-4, 1e-4);
                    double3 a = SurfaceMath.SurfacePoint(c.Surface, q, u, v);
                    double3 b = SurfaceMath.SurfacePoint(c.Surface, q, u + du, v + dv);
                    double  h = math.abs(ContinentField.Sample(a, tables, cp).Height - ContinentField.Sample(b, tables, cp).Height);
                    double  d = math.length(a - b);
                    Assert.LessOrEqual(h, bound * d * (1.0 + 1e-9) + 1e-9,
                        $"{c.Name}: a step of {h:F3} m over {d:F3} m at {a}, against {bound * d:F3} m allowed");
                    if (d > 0.0) worst = math.max(worst, h / d);
                }
                TestContext.WriteLine($"{c.Name}: steepest {worst:E3} per metre, {worst / bound:P2} of the bound {bound:E3}");
            }
        }

        // Without the warp, the field is zero midway between a land seed and an ocean seed that are
        // each other's nearest across the coast, and takes the side of whichever it moves towards.
        [Test]
        public void CoastIsZeroOnTheBisector()
        {
            foreach (var w in ContinentMapTests.Cases())
            {
                var k = w.Config.Continents.Value; k.WarpAmplitude = 0f;
                var c = ContinentMapTests.Make(w.Name, w.Surface, k);
                var map    = ContinentMapTests.Build(c);
                var tables = new ArrayContinentTables(map);
                var cp     = c.Continents;
                int pairs  = 0;
                for (int a = 0; a < map.MicroSeeds.Length && pairs < 200; a++)
                for (int b = 0; b < map.MicroSeeds.Length && pairs < 200; b++)
                {
                    if (map.MicroLand[a] == 0 || map.MicroLand[b] != 0) continue;
                    double3 m  = 0.5 * (map.MicroSeeds[a] + map.MicroSeeds[b]);
                    double  r2 = math.lengthsq(m - map.MicroSeeds[a]);
                    if (r2 > cp.Cap2) continue;
                    bool nearest = true;
                    for (int o = 0; o < map.MicroSeeds.Length && nearest; o++)
                        if (o != a && o != b && math.lengthsq(m - map.MicroSeeds[o]) <= r2 * (1.0 + 1e-9)) nearest = false;
                    if (!nearest) continue;
                    pairs++;

                    double3 step = 1e-3 * (map.MicroSeeds[a] - map.MicroSeeds[b]);
                    var on = ContinentField.Sample(m, tables, cp);
                    Assert.AreEqual(0.0, on.Height, 1e-6, $"{c.Name}: the coast between {a} and {b}");
                    Assert.Greater(ContinentField.Sample(m + step, tables, cp).Height, 0.0, $"{c.Name}: towards land seed {a}");
                    Assert.Less(ContinentField.Sample(m - step, tables, cp).Height, 0.0, $"{c.Name}: towards ocean seed {b}");
                }
                Assert.Greater(pairs, 10, $"{c.Name}: too few coastal pairs to test");
            }
        }

        // Inside a region, wherever the other class is beyond the cap, the profile is exactly on its
        // plateau: the ocean floor, or the land at its interior rise, with no cell ripple at all. At
        // every texel of the top level inside a quad, at the warped points the field evaluates; and
        // Stage B's check, which throws otherwise, agrees.
        [Test]
        public void PlateausAreExactlyFlat()
        {
            foreach (var c in ContinentMapTests.Cases())
            {
                var map    = ContinentMapTests.Build(c);
                var tables = new ArrayContinentTables(map);
                var cp     = c.Continents;
                int top = c.Layout.LevelCount - 1, n = c.Layout.Cells(top);
                int land = 0, sea = 0;
                double worst = 0.0;
                for (int q = 0; q < c.Roots.Length; q++)
                for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    var o = ContinentField.AtTexel(c.Surface, c.Roots[q], n, i, j, tables, cp);
                    worst = math.max(worst, ContinentField.PlateauRatio(o, n, i, j, cp));
                    if (o.OtherD2 < cp.Cap2) continue;
                    Assert.GreaterOrEqual(math.abs(o.S), cp.CoastWidth, $"{c.Name}: ({q}, {i}, {j}) is off its plateau");
                    if (o.S >= 0.0)
                    {
                        land++;
                        Assert.AreEqual(cp.LandHeight * (1.0 - 0.5 * cp.InteriorRise + cp.InteriorRise * o.E), o.Height,
                            $"{c.Name}: a land plateau at ({q}, {i}, {j})");
                    }
                    else
                    {
                        sea++;
                        Assert.AreEqual(-cp.OceanDepth, o.Height, $"{c.Name}: the ocean floor at ({q}, {i}, {j})");
                    }
                }
                Assert.Greater(land, 0, $"{c.Name}: no land plateau");
                Assert.Greater(sea, 0, $"{c.Name}: no ocean floor");
                Assert.DoesNotThrow(() => ContinentField.CheckPlateau(worst, cp), $"{c.Name}: Stage B's check");
                Assert.Throws<System.InvalidOperationException>(() => ContinentField.CheckPlateau(cp.PlateauLimit2 / (cp.MicroSpacing * cp.MicroSpacing) * 1.0001, cp),
                    $"{c.Name}: Stage B's check passes a ratio over the limit");
                TestContext.WriteLine($"{c.Name}: {land} land and {sea} ocean plateau texels; the worst own-class " +
                                      $"distance {math.sqrt(worst):F3} micro spacings, against {math.sqrt(cp.PlateauLimit2) / cp.MicroSpacing:F3}");
            }
        }

        // Every stored texel of the top level, borders included, lies within the profile's range,
        // which ContinentConfig.Bound and the height envelope are built from. Against the field's
        // own LandHeight (1 + IR/2), computed with the float InteriorRise, not the decimal 1.4.
        [Test]
        public void FieldStaysInItsRange()
        {
            foreach (var c in ContinentMapTests.Cases())
            {
                var map    = ContinentMapTests.Build(c);
                var cp     = c.Continents;
                var channel = new float[c.Layout.TexelCount];
                ContinentField.FillTop(channel, c.Surface, c.Roots, c.Layout, cp, map);

                int top = c.Layout.LevelCount - 1, n = c.Layout.Cells(top), b = MacroLayout.Border;
                double hi = cp.LandHeight * (1.0 + 0.5 * cp.InteriorRise);
                Assert.AreEqual(math.max(hi, cp.OceanDepth), cp.Bound, $"{c.Name}: the bound is the range's larger side");
                for (int q = 0; q < c.Roots.Length; q++)
                for (int j = -b; j <= n + b; j++)
                for (int i = -b; i <= n + b; i++)
                {
                    float h = channel[c.Layout.Index(q, top, i, j)];
                    Assert.GreaterOrEqual(h, (float)-cp.OceanDepth, $"{c.Name}: below the ocean floor at ({q}, {i}, {j})");
                    Assert.LessOrEqual(h, (float)hi, $"{c.Name}: above the highest land at ({q}, {i}, {j})");
                }
            }
        }
    }
}
