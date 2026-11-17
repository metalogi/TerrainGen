using System;
using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // The sea's waves (ocean-b, Docs/Ocean_B_Plan.md): a sum of Gerstner waves on the water grid,
    // built once from a handful of parameters (B4) and read by the ocean shader. UnityEngine-free,
    // so the out-of-Editor harness runs it, and so the tests price what the shader computes.
    //
    // A wave's phase at an absolute point X and time t, in cycles, is
    //
    //     phase(X, t) = K · X − F t + Phase0
    //                 = K · p  +  frac(K · O − F t + Phase0)          p = X − O, the origin O
    //
    // and the shader takes the first term in float from the vertex's render-space position, which
    // is metres from the camera wherever waves are drawn, while the second, PhaseOffset, is computed
    // here in double and pushed once a frame and again at every rebase (B1). K is rounded to float
    // when the table is built, so the shader's K and this one are the same number: the phase at a
    // fixed point then moves across a rebase by nothing but float rounding of K · p.
    public struct OceanWave
    {
        public double3 K;           // wave vector, cycles a metre, each component a float's value
        public double  Wavelength;  // metres, 1 / |K|
        public double  Frequency;   // cycles a second: deep water, sqrt(g / (2 pi lambda))
        public double  Amplitude;   // metres, along the up
        public double  Choppiness;  // Q: the horizontal displacement is Q x Amplitude, along K's tangent part
        public double  Phase0;      // cycles, from the seed, so the waves do not all crest at the origin
        public double  ShoalDepth;  // metres: ShoalFactor x Wavelength, the floor depth the wave is damped under
    }

    // The resolved parameters of OceanWaveSettings, validated by OceanWaves.Build.
    public struct OceanWaveConfig
    {
        public int     Count;
        public double  ShortestWavelength;  // metres
        public double  LongestWavelength;   // metres
        public double  Steepness;           // 2 pi a / lambda of the shortest wave
        public double  SpectrumExponent;    // steepness falls as (lambda / shortest)^-exponent
        public double  Choppiness;          // Q, the same for every wave
        public double3 WindDirection;       // world; any length but zero
        public double  SpreadDegrees;       // the half-angle of the cone the directions are drawn in
        public double  ShoalFactor;         // a wave is damped where the water is shallower than this x lambda
        public uint    Seed;
    }

    public static class OceanWaves
    {
        // The shader's array length, SONOMA_MAX_WAVES in SonomaWaves.hlsl.
        public const int MaxWaves = 16;

        public const double Gravity = 9.81;

        // A wave displaces where its effective depth draws at least this many vertices a wavelength,
        // and fully from FadeFull: Catmull-Rom's four texels a wavelength for the macro maps, the
        // same rule for the same reason (B2).
        public const double FadeStart = 4.0;
        public const double FadeFull  = 8.0;

        // The table. Wavelengths are geometric from shortest to longest, directions are drawn by the
        // seed in a cone about the wind, and each K is rounded to float so the shader holds it
        // exactly. Refuses what would not draw: more waves than the shader holds, a wavelength that is
        // not positive, a choppiness that loops the surface over itself (NoLoopSum above 1).
        public static OceanWave[] Build(in OceanWaveConfig c)
        {
            if (c.Count < 1 || c.Count > MaxWaves)
                throw new ArgumentOutOfRangeException(nameof(c),
                    "OceanWaves: the wave count must be from 1 to MaxWaves, the shader's array length.");
            if (!(c.ShortestWavelength > 0.0) || !(c.LongestWavelength >= c.ShortestWavelength) ||
                double.IsInfinity(c.LongestWavelength))
                throw new ArgumentOutOfRangeException(nameof(c),
                    "OceanWaves: wavelengths must be positive and finite, the longest no shorter than the shortest.");
            if (!(c.Steepness >= 0.0) || double.IsInfinity(c.Steepness) || double.IsNaN(c.SpectrumExponent) ||
                !(c.Choppiness >= 0.0) || double.IsInfinity(c.Choppiness) || !(c.ShoalFactor >= 0.0) ||
                !(c.SpreadDegrees >= 0.0) || c.SpreadDegrees > 180.0)
                throw new ArgumentOutOfRangeException(nameof(c),
                    "OceanWaves: steepness, choppiness and the shoal factor must be finite and not negative, " +
                    "and the spread from 0 to 180 degrees.");
            double windLength = math.length(c.WindDirection);
            if (!(windLength > 0.0) || double.IsInfinity(windLength))
                throw new ArgumentOutOfRangeException(nameof(c), "OceanWaves: the wind direction must be a finite, nonzero vector.");

            double3 wind = c.WindDirection / windLength;
            // Any vector perpendicular to the wind, to turn it about.
            double3 side = math.normalize(math.abs(wind.x) < 0.9 ? math.cross(wind, new double3(1, 0, 0))
                                                                  : math.cross(wind, new double3(0, 1, 0)));
            var rng    = new Unity.Mathematics.Random(c.Seed == 0u ? 0x9E3779B9u : c.Seed);
            double cone = math.radians(c.SpreadDegrees);

            var waves = new OceanWave[c.Count];
            for (int i = 0; i < c.Count; i++)
            {
                double t      = c.Count == 1 ? 0.0 : i / (double)(c.Count - 1);
                double lambda = c.ShortestWavelength * math.pow(c.LongestWavelength / c.ShortestWavelength, t);

                // A direction in the cone: the wind turned by up to the spread, about a random axis
                // perpendicular to it.
                double  about = rng.NextDouble(0.0, 2.0 * math.PI_DBL);
                double  tilt  = cone * rng.NextDouble(-1.0, 1.0);
                double3 axis  = Rotate(side, wind, about);
                double3 dir   = Rotate(wind, axis, tilt);

                double3 k = (double3)(float3)(dir / lambda);
                double  kLength = math.length(k);
                double  wl = 1.0 / kLength;
                double  steep = c.Steepness * math.pow(wl / c.ShortestWavelength, -c.SpectrumExponent);

                waves[i] = new OceanWave
                {
                    K          = k,
                    Wavelength = wl,
                    Frequency  = math.sqrt(Gravity / (2.0 * math.PI_DBL * wl)),
                    Amplitude  = steep * wl / (2.0 * math.PI_DBL),
                    Choppiness = c.Choppiness,
                    Phase0     = rng.NextDouble(),
                    ShoalDepth = c.ShoalFactor * wl,
                };
            }

            if (NoLoopSum(waves) > 1.0)
                throw new ArgumentOutOfRangeException(nameof(c),
                    "OceanWaves: the waves are too steep or too choppy, and the surface would loop over itself: " +
                    "lower Steepness or Choppiness until the sum of Q x 2 pi a / lambda is at most 1.");
            return waves;
        }

        // Rodrigues' rotation of v about the unit axis by angle radians.
        static double3 Rotate(double3 v, double3 axis, double angle)
        {
            math.sincos(angle, out double s, out double c);
            return v * c + math.cross(axis, v) * s + axis * math.dot(axis, v) * (1.0 - c);
        }

        // The largest vertical displacement: every wave cresting at once. What the kinds (Classify's
        // wave argument), the overlap, the water skirt and the camera's floor are sized by (B6).
        public static double MaxAmplitude(OceanWave[] waves)
        {
            double sum = 0.0;
            if (waves != null) foreach (var w in waves) sum += w.Amplitude;
            return sum;
        }

        // The largest displacement along any one axis: a wave moves a vertex by its amplitude along
        // the up and by Q times it along its tangent direction, a quarter cycle apart, so by at most
        // a x max(1, Q). What the water's culling box is padded by (B6).
        public static double MaxDisplacement(OceanWave[] waves)
        {
            double sum = 0.0;
            if (waves != null) foreach (var w in waves) sum += w.Amplitude * math.max(1.0, w.Choppiness);
            return sum;
        }

        // A Gerstner sum folds the surface over itself where its horizontal motion outruns the
        // spacing, which a sum of Q x 2 pi a / lambda at most 1 rules out everywhere.
        public static double NoLoopSum(OceanWave[] waves)
        {
            double sum = 0.0;
            if (waves != null)
                foreach (var w in waves) sum += w.Choppiness * 2.0 * math.PI_DBL * w.Amplitude / w.Wavelength;
            return sum;
        }

        // The double part of the phase at origin O and time t, in cycles, in [0, 1) (B1). Pushed as
        // _SonomaWavePhase; the shader adds K · p. Recomputed whole each frame, never accumulated, so
        // nothing drifts over a session.
        public static double PhaseOffset(in OceanWave w, double3 origin, double time)
            => Frac(math.dot(w.K, origin) - w.Frequency * time + w.Phase0);

        // The true phase at an absolute point, in double: what the shader's split sum approximates.
        public static double Phase(in OceanWave w, double3 point, double time)
            => Frac(math.dot(w.K, point) - w.Frequency * time + w.Phase0);

        // The phase as the shader computes it: K · p in float, p the render-space position as a
        // float, plus the pushed offset narrowed to a float, wrapped. For the tests.
        public static float ShaderPhase(in OceanWave w, float3 renderPosition, double offset)
        {
            float kp = math.dot((float3)w.K, renderPosition);
            float x  = kp + (float)offset;
            return x - math.floor(x);
        }

        public static double Frac(double x) => x - math.floor(x);

        // The vertex spacing at effective depth e (B2), from the depth-0 spacing,
        // NominalSize(0) / (R − 1). Continuous in e, so a chunk morphing from depth d to d − 1
        // passes through every spacing between, and two chunks agreeing on e agree on it.
        public static double Spacing(double depthZeroSpacing, double effectiveDepth)
            => depthZeroSpacing * math.exp2(-effectiveDepth);

        // How much of a wave the geometry draws at a spacing: 0 below FadeStart vertices a
        // wavelength, 1 from FadeFull, smooth between (B2). A function of the effective depth through
        // the spacing, never of distance or depth alone.
        public static double Fade(double wavelength, double spacing)
        {
            double t = math.saturate((wavelength / spacing - FadeStart) / (FadeFull - FadeStart));
            return t * t * (3.0 - 2.0 * t);
        }
    }
}
