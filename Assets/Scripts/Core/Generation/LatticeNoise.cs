using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // Gradient lattice noise that takes double coordinates.
    //
    // This exists because HeightmapGenerator.SampleAt casts the world position to float
    // once per octave. Float has 24 mantissa bits, so at coordinate magnitude 2^k the
    // resolution is 2^(k-23): on an Earth-radius sphere the eighth octave lands two
    // samples per lattice cell and every finer octave is worse than noise. See
    // SonomaRevisedPlan.md section 2.3.
    //
    // The fix is structural rather than a wider float: the integer cell is taken with a
    // double floor, and only the fractional part -- always in [0,1) -- becomes a float.
    // Precision is then independent of how far from the origin the sample is. The cost is
    // one double floor and one subtract per octave; the gradient evaluation stays float.
    public static class LatticeNoise
    {
        // Ken Perlin's improved-noise gradient set: the 12 cube-edge directions, with 4
        // repeated so the index is a mask rather than a modulo.
        //
        // A switch, not a static readonly float3[], because Burst forbids reading managed
        // static arrays from a job -- the same reason SurfaceMath spells out the cube face
        // basis as switch expressions. A managed array here compiles fine and then silently
        // drops the calling job to the managed fallback path.
        static float3 Gradient(uint h) => (h & 15u) switch
        {
            0u  => new float3( 1,  1,  0),
            1u  => new float3(-1,  1,  0),
            2u  => new float3( 1, -1,  0),
            3u  => new float3(-1, -1,  0),
            4u  => new float3( 1,  0,  1),
            5u  => new float3(-1,  0,  1),
            6u  => new float3( 1,  0, -1),
            7u  => new float3(-1,  0, -1),
            8u  => new float3( 0,  1,  1),
            9u  => new float3( 0, -1,  1),
            10u => new float3( 0,  1, -1),
            11u => new float3( 0, -1, -1),
            12u => new float3( 1,  1,  0),
            13u => new float3( 0, -1,  1),
            14u => new float3(-1,  1,  0),
            _   => new float3( 0, -1, -1),
        };

        // Chris Wellons' lowbias32 finaliser over a three-way multiplicative mix.
        // Pure integer arithmetic, so it is identical on every platform and in every Burst
        // target -- which is what makes the height function reproducible bit for bit.
        //
        // The casts on negative coordinates wrap, which is defined in C# and deliberate.
        // Folding them with abs() would mirror the terrain about the origin.
        static uint Hash(int3 c, uint seed)
        {
            uint h = (uint)c.x * 0x8DA6B343u
                   ^ (uint)c.y * 0xD8163841u
                   ^ (uint)c.z * 0xCB1AB31Fu
                   ^ seed      * 0x9E3779B9u;

            h ^= h >> 16; h *= 0x7FEB352Du;
            h ^= h >> 15; h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }

        // Value at a point, in [-1, 1]. The bound is attained -- see LatticeNoiseTests.
        // Exactly zero at every integer lattice point, mean zero over many cells.
        //
        // Do not normalise the result. The textbook bound of sqrt(3)/2 ~ 0.866 quoted for
        // 3D Perlin does not hold for this 16-entry gradient table (hill-climbing reaches
        // 1.000000), so scaling by 2/sqrt(3) would clip. Amplitude belongs to HeightScale.
        public static float Gradient3D(double3 p, uint seed)
        {
            double3 fl = math.floor(p);
            int3    c  = (int3)fl;

            // The one and only narrowing. p - fl is evaluated in double and is in [0,1),
            // so the float carries its full 24 bits no matter how large |p| is. Writing
            // this as (float3)p - (float3)fl reintroduces the section 2.3 bug and is what
            // LatticeNoiseTests.PrecisionDoesNotCollapseAtLargeCoordinates mutation-checks.
            float3 f = (float3)(p - fl);

            float3 t = f * f * f * (f * (f * 6f - 15f) + 10f);   // quintic fade

            float n000 = Corner(c, 0, 0, 0, f, seed);
            float n100 = Corner(c, 1, 0, 0, f, seed);
            float n010 = Corner(c, 0, 1, 0, f, seed);
            float n110 = Corner(c, 1, 1, 0, f, seed);
            float n001 = Corner(c, 0, 0, 1, f, seed);
            float n101 = Corner(c, 1, 0, 1, f, seed);
            float n011 = Corner(c, 0, 1, 1, f, seed);
            float n111 = Corner(c, 1, 1, 1, f, seed);

            float x00 = math.lerp(n000, n100, t.x);
            float x10 = math.lerp(n010, n110, t.x);
            float x01 = math.lerp(n001, n101, t.x);
            float x11 = math.lerp(n011, n111, t.x);

            return math.lerp(math.lerp(x00, x10, t.y),
                             math.lerp(x01, x11, t.y), t.z);
        }

        static float Corner(int3 c, int i, int j, int k, float3 f, uint seed)
        {
            float3 g = Gradient(Hash(new int3(c.x + i, c.y + j, c.z + k), seed));
            return math.dot(g, new float3(f.x - i, f.y - j, f.z - k));
        }

        // Summed octaves. octaveCount comes from HeightParams.MaxOctave(depth) + 1.
        //
        // The sum runs from octave 0 upwards and accumulates in a fixed order, so a coarse
        // evaluation is a strict prefix of a finer one at the same point. That is what lets
        // a chunk and its parent agree on the terms they share, and it is the property M3's
        // geomorph target depends on.
        public static float Fbm(double3 p, int octaveCount, float persistence, float lacunarity, uint seed)
        {
            double lac = lacunarity;
            double3 q  = p;
            float sum  = 0f;
            float amp  = 1f;

            for (int k = 0; k < octaveCount; k++)
            {
                // Each octave gets its own seed so they are independent rather than the
                // same field at different scales.
                sum += amp * Gradient3D(q, seed + (uint)k);
                amp *= persistence;
                q   *= lac;
            }

            return sum;
        }
    }
}
