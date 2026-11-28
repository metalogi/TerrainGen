using System;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Rendering;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The skybox's fade (AtmosphereParams, and SonomaSkyFactor in SonomaSky.hlsl, which repeats it
    // in float): full sky inside the atmosphere, black above it, by the camera's altitude above the
    // base surface; inside a cylinder the same from the wall towards the axis, and outside one from
    // the wall itself. Pure, so it runs out of the Editor too.
    public class AtmosphereTests
    {
        const double Thickness = 15000.0, Fade = 45000.0;
        static readonly AtmosphereParams Air = AtmosphereParams.Create(Thickness, Fade, 0.35);

        static readonly SurfaceDef Earth = SurfaceDef.CubeSphere(6371000.0, true);
        static readonly SurfaceDef Ring  = SurfaceDef.Cylinder(1592750.0, 1250000.0, 8, 1);
        static readonly SurfaceDef Plane = SurfaceDef.PlaneGrid(1000.0, 4, 4);

        // A point at `altitude` above Earth's base surface, along a direction that is no axis.
        static double3 OnEarth(double altitude) =>
            math.normalize(new double3(0.3, -0.8, 0.52)) * (Earth.Radius + altitude);

        // A point `altitude` in from the ring's wall towards its axis; negative is outside the ring.
        static double3 InRing(double altitude) =>
            new double3(math.cos(2.1), math.sin(2.1), 300000.0) * new double3(Ring.Radius - altitude, Ring.Radius - altitude, 1.0);

        [Test]
        public void TheSkyIsFullAtTheSurfaceAndBlackAboveTheAtmosphere()
        {
            Assert.AreEqual(1.0, Air.SkyFactor(Earth, OnEarth(0.0)), "the fog's colour at the surface");
            Assert.AreEqual(1.0, Air.SkyFactor(Earth, OnEarth(-2000.0)), "below the base surface, in a basin");
            Assert.AreEqual(1.0, Air.SkyFactor(Earth, OnEarth(Thickness - 1.0)), "full up to the thickness");
            Assert.AreEqual(0.5, Air.SkyFactor(Earth, OnEarth(Thickness + Fade / 2)), 1e-9, "half way through the fade");
            Assert.AreEqual(0.0, Air.SkyFactor(Earth, OnEarth(Thickness + Fade)), "black at the top of the fade");
            Assert.AreEqual(0.0, Air.SkyFactor(Earth, OnEarth(36000000.0)), "black from orbit");

            // Monotone through the fade, by the smoothstep.
            double last = 1.0;
            for (double a = Thickness; a <= Thickness + Fade; a += Fade / 64)
            {
                double f = Air.SkyFactor(Earth, OnEarth(a));
                Assert.LessOrEqual(f, last, $"the sky brightens climbing through {a} m");
                Assert.AreEqual(1.0 - AtmosphereParams.Smoothstep((a - Thickness) / Fade), f, 1e-12, $"at {a} m");
                last = f;
            }
        }

        // By altitude above the base surface, never by distance from anything else: the same
        // altitude anywhere on the sphere is the same sky.
        [Test]
        public void ASphereFadesByAltitudeAlone()
        {
            double a = Thickness + 0.3 * Fade;
            double expected = Air.SkyFactor(Earth, OnEarth(a));
            Assert.Greater(expected, 0.0);
            Assert.Less(expected, 1.0);
            foreach (var d in new[] { new double3(1, 0, 0), new double3(0, -1, 0), new double3(0.6, 0.0, 0.8), new double3(-0.1, 0.7, -0.7) })
                Assert.AreEqual(expected, Air.SkyFactor(Earth, math.normalize(d) * (Earth.Radius + a)), 1e-9, $"along {d}");
        }

        // Inside a cylinder the thickness is measured from the wall towards the axis; outside, the
        // air is held in, so the sky starts to fade the moment the camera crosses the wall.
        [Test]
        public void ACylinderHoldsItsAirInside()
        {
            Assert.AreEqual(1.0, Air.SkyFactor(Ring, InRing(0.0)), "at the wall");
            Assert.AreEqual(1.0, Air.SkyFactor(Ring, InRing(Thickness - 1.0)), "inside, within the thickness");
            Assert.AreEqual(0.5, Air.SkyFactor(Ring, InRing(Thickness + Fade / 2)), 1e-9, "inside, half way through the fade");
            Assert.AreEqual(0.0, Air.SkyFactor(Ring, InRing(Thickness + Fade)), "inside, above the atmosphere");
            Assert.AreEqual(0.0, Air.SkyFactor(Ring, InRing(Ring.Radius)), "on the axis");

            // Outside: the same distance from the wall that is full sky inside is already fading.
            Assert.AreEqual(1.0, Air.SkyFactor(Ring, InRing(1000.0)), "1 km inside");
            Assert.Less(Air.SkyFactor(Ring, InRing(-1000.0)), 1.0, "1 km outside is not full sky");
            Assert.AreEqual(1.0 - AtmosphereParams.Smoothstep(1000.0 / Fade), Air.SkyFactor(Ring, InRing(-1000.0)), 1e-12,
                "outside, the fade runs from the wall");
            Assert.AreEqual(0.5, Air.SkyFactor(Ring, InRing(-Fade / 2)), 1e-9, "outside, half way");
            Assert.AreEqual(0.0, Air.SkyFactor(Ring, InRing(-Fade)), "outside, past the fade");
            Assert.Less(Air.SkyFactor(Ring, InRing(-Thickness)), Air.SkyFactor(Ring, InRing(Thickness)),
                "outside, the thickness does not count");

            // A sphere has no inside rule: below its base surface is still air.
            Assert.AreEqual(1.0, Air.SkyFactor(Earth, OnEarth(-1000.0)), "a sphere's basins are air");
        }

        [Test]
        public void APlaneFadesByHeight()
        {
            Assert.AreEqual(1.0, Air.SkyFactor(Plane, new double3(500, 0, 2500)));
            Assert.AreEqual(1.0, Air.SkyFactor(Plane, new double3(500, -100, 2500)), "below the plane");
            Assert.AreEqual(0.5, Air.SkyFactor(Plane, new double3(-9e5, Thickness + Fade / 2, 2500)), 1e-9);
            Assert.AreEqual(0.0, Air.SkyFactor(Plane, new double3(500, Thickness + Fade, 2500)));
        }

        // default(AtmosphereParams) is no atmosphere: a black sky anywhere, and a w of 0 that zeroes
        // the skybox while the sea keeps the gradient it always had.
        [Test]
        public void NoAtmosphereIsABlackSky()
        {
            var none = default(AtmosphereParams);
            Assert.IsFalse(none.Enabled);
            Assert.AreEqual(0.0, none.SkyFactor(Earth, OnEarth(0.0)));
            Assert.AreEqual(0.0, none.SkyFactor(Ring, InRing(10.0)));
            Assert.AreEqual(new float4(0f, 0f, 0.35f, 0f), none.ShaderParams);
            Assert.AreEqual(new float4(15000f, (float)(1.0 / 45000.0), 0.35f, 1f), Air.ShaderParams);
        }

        [Test]
        public void CreateRefusesWhatCannotFade()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereParams.Create(-1.0, Fade, 0.35), "a negative thickness");
            Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereParams.Create(double.NaN, Fade, 0.35), "a NaN thickness");
            Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereParams.Create(double.PositiveInfinity, Fade, 0.35), "an infinite thickness");
            Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereParams.Create(Thickness, 0.0, 0.35), "no fade");
            Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereParams.Create(Thickness, double.NaN, 0.35), "a NaN fade");
            Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereParams.Create(Thickness, Fade, 0.0), "a flat gradient");
            Assert.Throws<ArgumentOutOfRangeException>(() => AtmosphereParams.Create(Thickness, Fade, 1.5), "a gradient past the zenith");
            Assert.DoesNotThrow(() => AtmosphereParams.Create(0.0, 1.0, 1.0), "the edges are allowed");
        }
    }
}
