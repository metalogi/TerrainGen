using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // Gradient lattice noise that takes double coordinates.
    //
    // This exists because the prototype's height sampler (HeightmapGenerator, deleted in M2)
    // cast the world position to float once per octave. Float has 24 mantissa bits, so at
    // coordinate magnitude 2^k the
    // resolution is 2^(k-23): on an Earth-radius sphere the eighth octave lands two
    // samples per lattice cell and every finer octave is worse than noise. See
    // SonomaRevisedPlan.md section 2.3.
    //
    // The integer cell is taken with a double floor, and everything after it -- the
    // fractional part, the fade, the corner dots, the interpolation -- stays in double too.
    //
    // M2 narrowed the fractional part to float on the reasoning that it is always in [0,1),
    // so its 24 bits are "independent of how far from the origin the sample is". True, and not
    // enough: they are 24 bits of a CELL, and a cell is as wide as its octave's wavelength. At
    // Earth radius octave 0's cell is 10,400 km, so 2^-24 of it is 0.62 m -- octave 0 became a
    // staircase with 0.62 m treads along each world axis, octave 1 one with 0.31 m treads, and
    // so on down to about octave 16. The risers are around a millimetre, which would not matter
    // on rough ground; Earth's at metre scale has slopes of a few thousandths, so the shading
    // showed them as long straight lines along the x, y and z lattice planes, visible flying low
    // when looking along one. SampleScene's 490 m cells resolve to 0.03 mm and never showed it.
    // HeightFunctionTests.EarthScaleHeightHasNoAxisAlignedSteps is the regression test.
    //
    // The result is double as well, and Fbm sums in double: narrowing each octave's value to
    // float re-quantises octave 0 at 9000 m x 2^-24 = 0.5 mm, which terraces along contours
    // instead of axes but is the same species of error. TerrainHeightFunction narrows once,
    // at the end. Measured in managed code, the double path costs no more than the float one.
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
        public static double Gradient3D(double3 p, uint seed)
        {
            double3 fl = math.floor(p);
            int3    c  = (int3)fl;

            // In double from here to the return; see the class comment for why a float
            // fraction is not enough. Writing this as (float3)p - (float3)fl reintroduces the
            // section 2.3 bug as well, and is what
            // LatticeNoiseTests.PrecisionDoesNotCollapseAtLargeCoordinates mutation-checks.
            double3 f = p - fl;

            double3 t = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);   // quintic fade

            double n000 = Corner(c, 0, 0, 0, f, seed);
            double n100 = Corner(c, 1, 0, 0, f, seed);
            double n010 = Corner(c, 0, 1, 0, f, seed);
            double n110 = Corner(c, 1, 1, 0, f, seed);
            double n001 = Corner(c, 0, 0, 1, f, seed);
            double n101 = Corner(c, 1, 0, 1, f, seed);
            double n011 = Corner(c, 0, 1, 1, f, seed);
            double n111 = Corner(c, 1, 1, 1, f, seed);

            double x00 = math.lerp(n000, n100, t.x);
            double x10 = math.lerp(n010, n110, t.x);
            double x01 = math.lerp(n001, n101, t.x);
            double x11 = math.lerp(n011, n111, t.x);

            return math.lerp(math.lerp(x00, x10, t.y),
                             math.lerp(x01, x11, t.y), t.z);
        }

        static double Corner(int3 c, int i, int j, int k, double3 f, uint seed)
        {
            double3 g = Gradient(Hash(new int3(c.x + i, c.y + j, c.z + k), seed));
            return math.dot(g, new double3(f.x - i, f.y - j, f.z - k));
        }

        // Gradient3D and its analytic derivative with respect to p, for the gully term's slope
        // damping (M5c, E6). The value is computed by exactly Gradient3D's operations, in its
        // order, so it is Gradient3D's bits: a sum may take an octave's value from either and stay
        // the same sum, which is what lets the last octave of a damped sum skip the derivative.
        // LatticeNoiseTests holds both halves -- the value to the bit and the derivative against
        // central differences.
        //
        // The derivative is the product rule over the trilinear blend: the corner gradients blended
        // by the faded weights, plus each axis's fade derivative 30 f^2 (f - 1)^2 times the blend's
        // own difference along that axis. Only + - * appear, which IEEE rounds the same in Burst and
        // managed code, so the derivative is as reproducible as the value.
        public static double Gradient3DD(double3 p, uint seed, out double3 derivative)
        {
            double3 fl = math.floor(p);
            int3    c  = (int3)fl;
            double3 f  = p - fl;

            double3 t  = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);   // quintic fade
            double3 dt = 30.0 * f * f * (f * (f - 2.0) + 1.0);        // its derivative

            double3 g000 = CornerGradient(c, 0, 0, 0, seed);
            double3 g100 = CornerGradient(c, 1, 0, 0, seed);
            double3 g010 = CornerGradient(c, 0, 1, 0, seed);
            double3 g110 = CornerGradient(c, 1, 1, 0, seed);
            double3 g001 = CornerGradient(c, 0, 0, 1, seed);
            double3 g101 = CornerGradient(c, 1, 0, 1, seed);
            double3 g011 = CornerGradient(c, 0, 1, 1, seed);
            double3 g111 = CornerGradient(c, 1, 1, 1, seed);

            // Corner's dot, written out so the value is Gradient3D's to the bit.
            double n000 = math.dot(g000, new double3(f.x,       f.y,       f.z));
            double n100 = math.dot(g100, new double3(f.x - 1.0, f.y,       f.z));
            double n010 = math.dot(g010, new double3(f.x,       f.y - 1.0, f.z));
            double n110 = math.dot(g110, new double3(f.x - 1.0, f.y - 1.0, f.z));
            double n001 = math.dot(g001, new double3(f.x,       f.y,       f.z - 1.0));
            double n101 = math.dot(g101, new double3(f.x - 1.0, f.y,       f.z - 1.0));
            double n011 = math.dot(g011, new double3(f.x,       f.y - 1.0, f.z - 1.0));
            double n111 = math.dot(g111, new double3(f.x - 1.0, f.y - 1.0, f.z - 1.0));

            double x00 = math.lerp(n000, n100, t.x);
            double x10 = math.lerp(n010, n110, t.x);
            double x01 = math.lerp(n001, n101, t.x);
            double x11 = math.lerp(n011, n111, t.x);
            double y0  = math.lerp(x00, x10, t.y);
            double y1  = math.lerp(x01, x11, t.y);

            // The blend's differences along each axis, at the faded weights of the other two.
            double ddx = math.lerp(math.lerp(n100 - n000, n110 - n010, t.y),
                                   math.lerp(n101 - n001, n111 - n011, t.y), t.z);
            double ddy = math.lerp(x10 - x00, x11 - x01, t.z);
            double ddz = y1 - y0;

            double3 blend = math.lerp(math.lerp(math.lerp(g000, g100, t.x), math.lerp(g010, g110, t.x), t.y),
                                      math.lerp(math.lerp(g001, g101, t.x), math.lerp(g011, g111, t.x), t.y), t.z);

            derivative = blend + dt * new double3(ddx, ddy, ddz);
            return math.lerp(y0, y1, t.z);
        }

        static double3 CornerGradient(int3 c, int i, int j, int k, uint seed)
            => Gradient(Hash(new int3(c.x + i, c.y + j, c.z + k), seed));

        // Summed octaves. octaveCount comes from HeightParams.MaxOctave(depth) + 1.
        //
        // The sum runs from octave 0 upwards and accumulates in a fixed order, so a coarse
        // evaluation is a strict prefix of a finer one at the same point. That is what lets
        // a chunk and its parent agree on the terms they share, and it is the property M3's
        // geomorph target depends on.
        public static double Fbm(double3 p, int octaveCount, float persistence, float lacunarity, uint seed)
        {
            double lac = lacunarity;
            double3 q  = p;
            double sum = 0.0;
            double amp = 1.0;

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

        // The same sum with each octave band's amplitude scaled: octaves below lowBandEnd by
        // 1 + bandGain.x, those below midBandEnd by 1 + bandGain.y, the rest by 1 + bandGain.z
        // (Biomes.BandGain). Linear in the octaves and fixed in order, so it keeps the
        // strict-prefix property the geomorph depends on, and the gains are a function of the
        // macro sample alone, the same at a chunk's depth and its parent's.
        //
        // With zero gains it is the sum above to the bit: 1 + 0 is exactly 1, and amp * 1 is
        // exactly amp. HeightFunctionTests holds it to that, and TerrainHeightFunction still
        // calls the original when the gains are zero.
        public static double Fbm(double3 p, int octaveCount, float persistence, float lacunarity, uint seed,
                                 float3 bandGain, int lowBandEnd, int midBandEnd)
            => Fbm(p, octaveCount, persistence, lacunarity, seed, bandGain, lowBandEnd, midBandEnd,
                   persistence, octaveCount);

        // The same, with a two-slope spectrum for close-up roughness (C10): octaves 0 ..
        // crossoverOctave-1 fall off by persistence, and every octave after them by
        // finePersistence. At persistence 0.5 each octave is as steep as the last, so small
        // features are tiny -- 10 m bumps about 9 mm tall on Earth -- and a finePersistence
        // above 0.5 roughens the small scales alone, leaving the continents as tuned.
        //
        // Octave k's amplitude depends on k alone, so the sum keeps its strict-prefix property.
        // With finePersistence equal to persistence every multiply is the same one, and the sum
        // is the one above to the bit.
        public static double Fbm(double3 p, int octaveCount, float persistence, float lacunarity, uint seed,
                                 float3 bandGain, int lowBandEnd, int midBandEnd,
                                 float finePersistence, int crossoverOctave)
            => Fbm(p, octaveCount, persistence, lacunarity, seed, bandGain, lowBandEnd, midBandEnd,
                   finePersistence, crossoverOctave, 0, 0f);

        // The same, with the continental cutoff (HeightParams.WithContinentalCutoff): octaves 0 ..
        // firstOctave-1 scaled by 1 + coarseGain on top of their band gain. A gain of -1 drops them,
        // and then the octave is skipped -- no lattice evaluation, only the amplitude and the
        // coordinate advanced -- which on Earth saves four of 24 octaves at every vertex.
        //
        // The same octaves at every count, so the sum is still a strict prefix of itself. With
        // firstOctave 0 no octave takes the branch, and the sum is the one above to the bit; with
        // coarseGain 0 the extra factor is exactly 1.0, multiplied after the band gain, which is
        // also the sum above to the bit.
        public static double Fbm(double3 p, int octaveCount, float persistence, float lacunarity, uint seed,
                                 float3 bandGain, int lowBandEnd, int midBandEnd,
                                 float finePersistence, int crossoverOctave,
                                 int firstOctave, float coarseGain)
        {
            double lac = lacunarity;
            double3 q  = p;
            double sum = 0.0;
            double amp = 1.0;

            for (int k = 0; k < octaveCount; k++)
            {
                float gain = k < lowBandEnd ? bandGain.x : k < midBandEnd ? bandGain.y : bandGain.z;
                if (k >= firstOctave)
                    sum += amp * (1.0 + gain) * Gradient3D(q, seed + (uint)k);
                else if (coarseGain != -1f)
                    sum += amp * (1.0 + gain) * (1.0 + coarseGain) * Gradient3D(q, seed + (uint)k);
                amp *= k + 1 < crossoverOctave ? persistence : finePersistence;
                q   *= lac;
            }

            return sum;
        }

        // The same, with the gully term's slope damping (M5c, E6, G1): every octave from
        // gullyStartOctave on is divided by 1 + damping |g|^2, where g is the slope of the octaves
        // before it in metres per metre, projected onto the tangent plane. Steep ground loses its
        // fine detail and crests and flats keep theirs, which reads as weathered slopes.
        //
        // slopeScale turns a derivative in these noise coordinates into metres per metre:
        // HeightScale / OctaveWavelength0, since p is the surface position over the octave-0
        // wavelength. up is any vector along the surface normal at p, not necessarily unit (the
        // radial vector on a sphere or cylinder): the projection divides by its length squared
        // rather than normalising, so no square root enters and Burst rounds it as managed code does.
        //
        // g accumulates each octave's derivative as it entered the sum, damped by its own factor
        // held constant -- the factor's own variation is left out, so g is the slope of the sum to
        // first order in the damping. Octave k's factor depends only on octaves 0 .. k-1, so the sum
        // is still a strict prefix of itself and a fully morphed child is still its parent. Before
        // gullyStartOctave every term is the general sum's to the bit (Gradient3DD's value is
        // Gradient3D's), and with damping 0 TerrainHeightFunction does not call this at all.
        //
        // The factor is at most 1 for damping >= 0, so no octave exceeds its undamped amplitude and
        // TerrainHeightFunction.MaxAbsHeight still bounds the sum.
        public static double Fbm(double3 p, int octaveCount, float persistence, float lacunarity, uint seed,
                                 float3 bandGain, int lowBandEnd, int midBandEnd,
                                 float finePersistence, int crossoverOctave,
                                 int firstOctave, float coarseGain,
                                 int gullyStartOctave, double damping, double slopeScale, double3 up)
        {
            double  lac   = lacunarity;
            double3 q     = p;
            double  sum   = 0.0;
            double  amp   = 1.0;
            double  scale = 1.0;            // lac^k: d q / d p
            double3 slope = double3.zero;   // d sum / d p
            double  upLen2 = math.dot(up, up);

            for (int k = 0; k < octaveCount; k++)
            {
                float  gain = k < lowBandEnd ? bandGain.x : k < midBandEnd ? bandGain.y : bandGain.z;
                double a;
                if (k >= firstOctave)    a = amp * (1.0 + gain);
                else if (coarseGain != -1f) a = amp * (1.0 + gain) * (1.0 + coarseGain);
                else                     a = 0.0;

                if (a != 0.0)
                {
                    if (k >= gullyStartOctave)
                    {
                        double3 t  = slope - up * (math.dot(slope, up) / upLen2);
                        double  g2 = slopeScale * slopeScale * math.dot(t, t);
                        a /= 1.0 + damping * g2;
                    }

                    // The last octave's derivative would never be read.
                    if (k + 1 < octaveCount)
                    {
                        sum   += a * Gradient3DD(q, seed + (uint)k, out double3 d);
                        slope += a * scale * d;
                    }
                    else
                        sum += a * Gradient3D(q, seed + (uint)k);
                }

                amp   *= k + 1 < crossoverOctave ? persistence : finePersistence;
                q     *= lac;
                scale *= lac;
            }

            return sum;
        }
    }
}
