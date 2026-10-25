using System;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // Macro erosion as configured (Docs/M5_MacroLayer_Plan.md, M5c, E3): what ErosionSettings holds,
    // as a plain struct so pure code and the tests can build one without a ScriptableObject.
    public struct ErosionConfig
    {
        public int   Iterations;       // implicit stream-power steps
        public float IncisionDepth;    // metres; see ErosionParams.KDt
        public float AreaExponent;     // m, the drainage area's exponent
        public float Diffusion;        // hillslope kappa dt per step, in top-level texel spacings squared
        public float BaseLevel;        // metres; water bodies below it of at least MinOutletArea are outlets
        public float MinOutletArea;    // in root quads' area; 0 makes every water body an outlet

        public static ErosionConfig Default => new ErosionConfig
        {
            Iterations = 30, IncisionDepth = 0f, AreaExponent = 0.45f, Diffusion = 0f, BaseLevel = 0f,
            MinOutletArea = 0f,
        };

        // Everything the erosion pass assumes of its configuration, checked once.
        public void Validate()
        {
            if (Iterations < 1)
                throw new ArgumentOutOfRangeException(nameof(Iterations),
                    "ErosionConfig: there must be at least one iteration.");
            if (!IsFinite(IncisionDepth) || !IsFinite(AreaExponent) || !IsFinite(Diffusion) ||
                !IsFinite(BaseLevel) || !IsFinite(MinOutletArea))
                throw new ArgumentOutOfRangeException(nameof(IncisionDepth),
                    "ErosionConfig: every setting must be finite.");
            if (IncisionDepth < 0f || Diffusion < 0f || MinOutletArea < 0f)
                throw new ArgumentOutOfRangeException(nameof(IncisionDepth),
                    "ErosionConfig: the incision depth, the diffusion and the minimum outlet area must not be negative.");
            // At 0 every channel incises alike whatever drains into it, and at 1 or above the
            // largest rivers cut so much faster than the rest that one step does all the work.
            if (!(AreaExponent > 0f) || !(AreaExponent < 1f))
                throw new ArgumentOutOfRangeException(nameof(AreaExponent),
                    "ErosionConfig: the area exponent must be in (0, 1); 0.4 to 0.5 is usual.");
        }

        static bool IsFinite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    }

    // Everything the erosion pass needs, resolved once at setup and copied into its jobs by value.
    // Blittable. Lengths are metres and areas square metres.
    public struct ErosionParams
    {
        public bool   Enabled;
        public int    Iterations;
        public int    AreaExponentBits; // m in 1024ths; see AreaPower
        public double AreaExponent;    // m, as AreaExponentBits gives it
        public double BaseLevel;       // metres
        public double QuadArea;        // one root quad's area: the area IncisionDepth and wetness refer to

        // Square metres. A water body -- nodes at or below BaseLevel, connected -- is an outlet only
        // if its area is at least this; a smaller one is a hollow the flood fills and routes over, so
        // rivers run through it to the sea rather than ending in it. 0 makes every body an outlet.
        public double MinOutletArea;

        // K dt, per step, in m^(1 - 2m). The incision step is h_i <- (h_i + F h_r) / (1 + F), with
        // F = KDt A^m / length (E3), which with n = 1 lowers a node at the explicit rate
        // KDt A^m S per step. So KDt A^m is a length per step: how far a knickpoint climbs a
        // channel draining A in one step, and how deep the channel is cut at unit slope.
        // IncisionDepth fixes that length over the whole run for a channel draining a quad's area,
        //   IncisionDepth = Iterations x KDt x QuadArea^m,
        // so it is in metres and means the same on any world. A channel at slope S is cut about
        // IncisionDepth x S, less where it reaches its receiver's height first.
        public double KDt;

        // Hillslope kappa dt per step, in square metres: Diffusion times the top level's nominal
        // texel spacing squared. Whether a Jacobi sweep is stable depends on the graph's shortest
        // links, so the erosion job checks that against the graph it builds; this only scales it.
        public double DiffusionKDt;

        public static ErosionParams None => default;

        // m's resolution: the exponent is taken to the nearest 1/1024.
        public const int ExponentBits = 10;

        // a^m, from square roots alone. The incision step takes A^m at every node of every
        // iteration inside a Burst job, and Burst's pow is not managed code's to the bit, which is
        // the continent jobs' trigonometry rule again. A square root is correctly rounded on both,
        // so m is kept to ExponentBits binary places, m = sum of b_k 2^-k, and a^m is the product of
        // a^(2^-k) over the bits that are set: the same operations in the same order in both builds.
        public static double AreaPower(double a, int mBits)
        {
            double r = 1.0, root = a;
            for (int k = 1; k <= ExponentBits; k++)
            {
                root = math.sqrt(root);
                if (((mBits >> (ExponentBits - k)) & 1) != 0) r *= root;
            }
            return r;
        }

        public double AreaPower(double a) => AreaPower(a, AreaExponentBits);

        // Resolves the configuration's erosion for this surface, or None if it has none. Erosion
        // needs a macro layer to write into, so the macro parameters must be enabled and made from
        // the same configuration.
        public static ErosionParams Create(in SurfaceDef s, in HeightParams hp, in MacroParams mp, in MacroConfig c)
        {
            if (!c.Erosion.HasValue)
            {
                if (mp.HasErosion)
                    throw new ArgumentException("ErosionParams: the macro parameters have erosion and the configuration none.");
                return None;
            }
            if (!mp.Enabled)
                throw new ArgumentException("ErosionParams: erosion needs a macro layer to write into.");
            if (!mp.HasErosion)
                throw new ArgumentException("ErosionParams: the macro parameters were made without erosion.");

            var e = c.Erosion.Value;
            e.Validate();

            double quadArea = ContinentParams.SurfaceArea(s) / s.QuadCount;
            double texel    = ContinentParams.TopTexelSpacing(hp.S0, mp.Layout);
            int    bits     = math.clamp((int)math.round(e.AreaExponent * (1 << ExponentBits)), 1, (1 << ExponentBits) - 1);

            return new ErosionParams
            {
                Enabled          = true,
                Iterations       = e.Iterations,
                AreaExponentBits = bits,
                AreaExponent     = bits / (double)(1 << ExponentBits),
                BaseLevel        = e.BaseLevel,
                QuadArea         = quadArea,
                MinOutletArea    = e.MinOutletArea * quadArea,
                KDt              = e.IncisionDepth / (e.Iterations * AreaPower(quadArea, bits)),
                DiffusionKDt     = e.Diffusion * texel * texel,
            };
        }
    }
}
