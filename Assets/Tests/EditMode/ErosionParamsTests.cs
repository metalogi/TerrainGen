using System;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // M5c-1, task 1 (Docs/M5_MacroLayer_Plan.md, E3): ErosionParams.Create resolves the configured
    // erosion once. IncisionDepth turns into K dt so that a channel draining a quad's area travels
    // IncisionDepth over the run, the diffusion is scaled by the top level's texel, and anything the
    // pass cannot use is refused at setup.
    public class ErosionParamsTests
    {
        static readonly SurfaceDef[] Surfaces =
        {
            SurfaceDef.CubeSphere(6371000.0),
            SurfaceDef.Cylinder(1592750.0, 1250000.0, 8, 1),
            SurfaceDef.PlaneGrid(1000.0, 2, 3),
        };

        static ErosionConfig Erosion(float depth = 500f, float m = 0.45f, float diffusion = 0.1f) => new ErosionConfig
        {
            Iterations = 30, IncisionDepth = depth, AreaExponent = m, Diffusion = diffusion, BaseLevel = 0f,
        };

        static MacroConfig Macro(ErosionConfig? e) => new MacroConfig
        {
            LevelCount = 3, Wavelength0 = 0.0, Persistence = 0.5f, BaseHeightScale = 900f,
            EquatorTemperature = 30f, PoleTemperature = -30f, LapseRate = 6.5f, TemperatureNoise = 5f,
            MoistureNoise = 1.5f, RuggednessNoise = 1.5f, Seed = 7u, Erosion = e,
        };

        static ErosionParams Create(in SurfaceDef s, in MacroConfig c)
        {
            var hp = HeightParams.Create(s, 9, 0.0, 12, 100f, 0.5f, 2f, 42u);
            return ErosionParams.Create(s, hp, MacroParams.Create(s, hp, c), c);
        }

        [Test]
        public void NoErosionIsNone()
        {
            foreach (var s in Surfaces)
                Assert.IsFalse(Create(s, Macro(null)).Enabled, s.Type.ToString());
        }

        [Test]
        public void IncisionDepthIsTheRunsTravelForAQuadsArea()
        {
            foreach (var s in Surfaces)
            foreach (float m in new[] { 0.4f, 0.45f, 0.5f })
            {
                var p = Create(s, Macro(Erosion(depth: 750f, m: m)));
                Assert.IsTrue(p.Enabled);
                Assert.AreEqual(ContinentParams.SurfaceArea(s), p.QuadArea * s.QuadCount,
                                1e-12 * ContinentParams.SurfaceArea(s), $"{s.Type}: a quad's area");
                Assert.AreEqual((double)m, p.AreaExponent, $"{s.Type}: m");
                double travel = p.Iterations * p.KDt * math.pow(p.QuadArea, p.AreaExponent);
                Assert.AreEqual(750.0, travel, 1e-9, $"{s.Type}, m {m}: the run's travel");
            }
        }

        [Test]
        public void SmallerRiversCutLessByTheAreaExponent()
        {
            // The same depth means the same thing on any world: a channel draining a quarter of a
            // quad's area travels a quarter to the m as far.
            var p = Create(Surfaces[0], Macro(Erosion(depth: 800f, m: 0.5f)));
            double quarter = p.Iterations * p.KDt * math.pow(p.QuadArea / 4.0, p.AreaExponent);
            Assert.AreEqual(400.0, quarter, 1e-9);
        }

        [Test]
        public void DiffusionIsInTopLevelTexelsSquared()
        {
            foreach (var s in Surfaces)
            {
                var    hp    = HeightParams.Create(s, 9, 0.0, 12, 100f, 0.5f, 2f, 42u);
                var    c     = Macro(Erosion(diffusion: 0.2f));
                var    mp    = MacroParams.Create(s, hp, c);
                var    p     = ErosionParams.Create(s, hp, mp, c);
                double texel = ContinentParams.TopTexelSpacing(hp.S0, mp.Layout);
                Assert.AreEqual(0.2, p.DiffusionKDt / (texel * texel), 1e-7, s.Type.ToString());
            }
        }

        [Test]
        public void ZeroIncisionCutsNothing()
        {
            var p = Create(Surfaces[1], Macro(Erosion(depth: 0f, diffusion: 0f)));
            Assert.IsTrue(p.Enabled, "routing still runs");
            Assert.AreEqual(0.0, p.KDt);
            Assert.AreEqual(0.0, p.DiffusionKDt);
        }

        // Configuration is validated once, here.
        [Test]
        public void ErosionParamsRefusesWhatThePassCannotUse()
        {
            var s = Surfaces[0];
            void Refuses(ErosionConfig e, string what) =>
                Assert.Throws<ArgumentOutOfRangeException>(() => Create(s, Macro(e)), what);

            var e0 = Erosion();
            var e  = e0; e.Iterations    = 0;              Refuses(e, "no iterations");
            e = e0;      e.IncisionDepth = -1f;            Refuses(e, "negative incision");
            e = e0;      e.Diffusion     = -0.1f;          Refuses(e, "negative diffusion");
            e = e0;      e.AreaExponent  = 0f;             Refuses(e, "m of 0");
            e = e0;      e.AreaExponent  = 1f;             Refuses(e, "m of 1");
            e = e0;      e.IncisionDepth = float.NaN;      Refuses(e, "NaN incision");
            e = e0;      e.BaseLevel     = float.PositiveInfinity; Refuses(e, "infinite base level");
            e = e0;      e.Diffusion     = float.NaN;      Refuses(e, "NaN diffusion");
            Assert.DoesNotThrow(() => Create(s, Macro(ErosionConfig.Default)), "the default");

            var hp = HeightParams.Create(s, 9, 0.0, 12, 100f, 0.5f, 2f, 42u);
            Assert.Throws<ArgumentException>(
                () => ErosionParams.Create(s, hp, MacroParams.Disabled, Macro(e0)), "no macro layer");
        }

        [Test]
        public void ErosionLeavesTheMacroParametersAlone()
        {
            // Erosion is resolved apart from the generator's parameters, so a configuration with it
            // builds the same MacroParams as one without: until the pass runs, the maps are the bits.
            foreach (var s in Surfaces)
            {
                var hp = HeightParams.Create(s, 9, 0.0, 12, 100f, 0.5f, 2f, 42u);
                Assert.AreEqual(MacroParams.Create(s, hp, Macro(null)),
                                MacroParams.Create(s, hp, Macro(Erosion())), s.Type.ToString());
            }
        }
    }
}
