using System;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // M5c-1, task 6 (Docs/M5_MacroLayer_Plan.md, E1 and E5): erosion from configuration to eroded
    // maps, through the managed builder MacroMaps.Build is held to in MacroMapsTests. Nothing to
    // erode leaves the maps as they were; erosion only lowers the base height and leaves the
    // climate alone; the top level then holds T' (base height and the eroded octaves together);
    // and the measured height bound holds over the eroded terrain.
    public class ErosionBuildTests
    {
        internal sealed class Case
        {
            public string           Name;
            public SurfaceDef       Surface;
            public RootQuad[]       Roots;
            public HeightParams     Height;
            public MacroConfig      Config;
            public MacroParams      Macro;
            public ContinentParams  Continents;
            public ErosionParams    Erosion;
        }

        internal static ErosionConfig Incising(float depth = 2e6f, float diffusion = 0f) => new ErosionConfig
        {
            Iterations = 10, IncisionDepth = depth, AreaExponent = 0.45f, Diffusion = diffusion, BaseLevel = 0f,
        };

        // general puts the height function on its general Fbm path, as every stress scene is: a
        // two-slope spectrum and band edges. The bare parameters take the original path, and on those
        // alone the Burst build matched the managed one while the narrow ring's T, on the general
        // path, came out an ULP apart at 13.6% of its nodes.
        internal static Case Make(string name, in SurfaceDef s, ErosionConfig? e, ContinentConfig? k = null, bool general = false)
        {
            var hp = HeightParams.Create(s, 9, 0.0, 12, 2000f, 0.5f, 2f, 42u);
            if (general) hp = hp.WithBands(150000.0, 5000.0).WithRoughness(0.6f, 5000.0);
            var c  = new MacroConfig
            {
                LevelCount = 3, Wavelength0 = 0.0, Persistence = 0.5f, BaseHeightScale = 900f,
                EquatorTemperature = 30f, PoleTemperature = -30f, LapseRate = 6.5f, TemperatureNoise = 5f,
                MoistureNoise = 1.5f, RuggednessNoise = 1.5f, Seed = 7u, Continents = k, Erosion = e,
            };
            var p = MacroParams.Create(s, hp, c);
            return new Case
            {
                Name = name, Surface = s, Roots = SurfaceMath.BuildRoots(s), Height = hp, Config = c, Macro = p,
                Continents = ContinentParams.Create(s, p, c), Erosion = ErosionParams.Create(s, hp, p, c),
            };
        }

        internal static Case[] Cases(ErosionConfig? e) => new[]
        {
            Make("sphere",     SurfaceDef.CubeSphere(6371000.0),               e),
            Make("ring",       SurfaceDef.Cylinder(1592750.0, 1250000.0, 8, 1), e),
            Make("plane",      SurfaceDef.PlaneGrid(100000.0, 2, 2),            e),
            Make("continents", SurfaceDef.CubeSphere(6371000.0),               e, ContinentMapTests.SmallConfig(8, 60, 2)),
            Make("general path", SurfaceDef.CubeSphere(6371000.0),             e, ContinentMapTests.SmallConfig(8, 60, 2), general: true),
        };

        internal static float4[] Build(Case c, out float2[] drainage, out ErosionBuild.Managed erosion, out ErosionStats stats)
            => MacroGenerator.Build(c.Surface, c.Roots, c.Macro, c.Continents, c.Height, c.Erosion,
                                    out _, out drainage, out erosion, out stats);

        // The same maps without erosion: the configuration without it, everything else equal.
        static float4[] Plain(Case c)
        {
            var config = c.Config;
            config.Erosion = null;
            var p = MacroParams.Create(c.Surface, c.Height, config);
            return MacroGenerator.Build(c.Surface, c.Roots, p, ContinentParams.Create(c.Surface, p, config), out _);
        }

        [Test]
        public void NothingToErodeLeavesTheMaps()
        {
            foreach (var c in Cases(Incising(depth: 0f)))
            {
                var eroded = Build(c, out float2[] drainage, out _, out ErosionStats stats);
                var plain  = Plain(c);
                for (int t = 0; t < plain.Length; t++)
                {
                    Assert.IsTrue(math.all(eroded[t] == plain[t]), $"{c.Name}: texel {t}");
                    Assert.AreEqual(0f, drainage[t].y, $"{c.Name}: D at {t}");
                    Assert.That(drainage[t].x, Is.InRange(0f, 1f), $"{c.Name}: wetness at {t}");
                }
                Assert.AreEqual(0.0, stats.MaxCut, c.Name);
            }
        }

        [Test]
        public void ErosionLowersTheBaseHeightAndLeavesTheClimate()
        {
            foreach (var c in Cases(Incising()))
            {
                var eroded = Build(c, out float2[] drainage, out _, out ErosionStats stats);
                var plain  = Plain(c);
                int lowered = 0;
                for (int t = 0; t < plain.Length; t++)
                {
                    Assert.LessOrEqual(eroded[t].x, plain[t].x, $"{c.Name}: texel {t} rose");
                    Assert.AreEqual(math.asuint(plain[t].yzw), math.asuint(eroded[t].yzw), $"{c.Name}: climate at {t}");
                    if (eroded[t].x < plain[t].x) lowered++;
                }
                Assert.Greater(lowered, plain.Length / 20, $"{c.Name}: almost nothing was cut");
                Assert.Greater(stats.MaxCut, 0.0, c.Name);

                double max = 0.0;
                foreach (float4 x in eroded) max = math.max(max, math.abs((double)x.x));
                Assert.AreEqual(max, stats.MaxAbsBaseHeight, $"{c.Name}: the recorded maximum");
            }
        }

        // E1: at every top-level node the stored base height plus the eroded octaves is T', the
        // pass's eroded height, to the float storage's rounding.
        [Test]
        public void TheTopLevelHoldsTheErodedHeight()
        {
            foreach (var c in Cases(Incising()))
            {
                var texels = Build(c, out float2[] drainage, out ErosionBuild.Managed e, out _);
                var reader = new ArrayMacroTexels(texels, drainage);
                var g      = e.Graph;
                for (int node = 0; node < g.NodeCount; node++)
                {
                    int texel = g.NodeTexel[node];
                    ErosionGraph.Coords(g.Cells, texel, out int q, out int i, out int j);
                    double got = ErosionChannels.UnerodedHeight(c.Height, c.Macro, reader, g.Points[texel], q, i, j);
                    Assert.AreEqual(e.Result.Heights[node], got, 0.02, $"{c.Name}: node {node}");
                }
            }
        }

        // E5: the measured bound holds over the eroded terrain at full detail, and without erosion it
        // is the analytic bound to the bit.
        [Test]
        public void TheMeasuredBoundHoldsOverErodedTerrain()
        {
            foreach (var c in Cases(Incising()))
            {
                var    texels = Build(c, out float2[] drainage, out _, out ErosionStats stats);
                var    reader = new ArrayMacroTexels(texels, drainage);
                double bound  = TerrainHeightFunction.MaxAbsHeight(c.Height, c.Macro, stats.MaxAbsBaseHeight);
                var    rng    = new Unity.Mathematics.Random(0xB0B0u);
                for (int k = 0; k < 2000; k++)
                {
                    int     q = rng.NextInt(0, c.Surface.QuadCount);
                    double3 p = SurfaceMath.SurfacePoint(c.Surface, c.Roots[q], rng.NextDouble(), rng.NextDouble());
                    float   h = TerrainHeightFunction.HeightAtPoint(c.Surface, p, 8, c.Height, c.Macro, reader);
                    Assert.LessOrEqual(math.abs((double)h), bound, $"{c.Name}: point {k}");
                }

                // The analytic bound refuses eroded maps, which it does not bound.
                Assert.Throws<ArgumentException>(() => TerrainHeightFunction.MaxAbsHeight(c.Height, c.Macro),
                                                 $"{c.Name}: the analytic bound accepted eroded maps");

                var plain = c.Macro;
                plain.HasErosion = false;
                Assert.AreEqual(math.asulong(TerrainHeightFunction.MaxAbsHeight(c.Height, plain)),
                                math.asulong(TerrainHeightFunction.MaxAbsHeight(c.Height, plain, 12345.0)),
                                $"{c.Name}: without erosion the bound is the analytic one");
            }
        }

        [Test]
        public void TheBuildersRefuseMismatchedErosion()
        {
            var c = Cases(Incising())[0];
            Assert.Throws<ArgumentException>(() => MacroGenerator.Build(c.Surface, c.Roots, c.Macro, c.Continents, out _),
                                             "the overload without erosion, given eroded parameters");
            Assert.Throws<ArgumentException>(() => MacroGenerator.Build(c.Surface, c.Roots, c.Macro, c.Continents, c.Height,
                                                                        ErosionParams.None, out _, out _, out _, out _),
                                             "erosion in the macro parameters only");
        }
    }
}
