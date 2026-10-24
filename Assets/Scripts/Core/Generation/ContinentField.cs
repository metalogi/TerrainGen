using System;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // The continent field at one point, and what went into it.
    public struct ContinentSample
    {
        public double S;        // signed coast distance, metres: positive on land, zero on the coast
        public double E;        // the blended macro elevation, 0..1
        public double Height;   // metres
        public double OwnD2;    // squared distance to the nearest seed of the point's own class, capped
        public double OtherD2;  // and of the other class, capped
    }

    // Stage B of the continents (Docs/Continents_Plan.md, K2): the continent height, a pure function
    // of a surface point and Stage A's tables.
    //
    //   p'     = p + WarpAmplitude * (three 3-octave fBms of p / WarpWavelength)
    //   dL, dO = the squared distances from p' to the nearest land and ocean micro seeds, capped
    //   s      = (dO - dL) / 2D                    positive on land, exactly the bisector's distance for an adjacent pair D apart
    //   t      = min(|s| / CoastWidth, 1)
    //   f      = t (15 - 10 t^2 + 3 t^4) / 8       odd quintic: slope 15/8 at the coast, flat at t = 1
    //   E      = sum w e / sum w over macro seeds, w = (1 - r^2/R^2)^2 inside R
    //   height = s >= 0 ? LandHeight (1 - IR/2 + IR E) f : -OceanDepth f
    //
    // Squared, not linear: inside a region the capped term dominates and the profile is exactly
    // flat, where the linear form carried a cell-shaped ripple on every plateau. A polynomial
    // profile and kernel, not tanh and a Gaussian: only + - x / and sqrt, which IEEE rounds the same
    // in Burst and managed code, so ContinentFieldJob matches this to the bit. Squared distances
    // throughout, so the field takes no sqrt at all.
    //
    // The plateau is exact where a point's own class is within ContinentParams.PlateauLimit2 and
    // the other class is capped. Stage A checks the unwarped texels; the warp moves the field's
    // points up to about a micro spacing, off the surface too, so Stage B checks the points it
    // actually evaluates (PlateauRatio, CheckPlateau).
    public static class ContinentField
    {
        public static ContinentSample Sample<T>(double3 p, T t, in ContinentParams cp) where T : struct, IContinentTables
        {
            double3 x    = p / cp.WarpWavelength;
            double3 warp = new double3(LatticeNoise.Fbm(x, 3, 0.5f, 2f, cp.WarpSeed),
                                       LatticeNoise.Fbm(x, 3, 0.5f, 2f, cp.WarpSeed + ContinentParams.ChannelStride),
                                       LatticeNoise.Fbm(x, 3, 0.5f, 2f, cp.WarpSeed + 2u * ContinentParams.ChannelStride));
            double3 pw   = p + cp.WarpAmplitude * warp;

            ContinentMap.ClassDistances(t, pw, out double land2, out double ocean2);
            land2  = math.min(land2,  cp.Cap2);
            ocean2 = math.min(ocean2, cp.Cap2);

            var o = new ContinentSample();
            o.S = (ocean2 - land2) / (2.0 * cp.MicroSpacing);

            double u = math.min(math.abs(o.S) / cp.CoastWidth, 1.0);
            // This one form, fixed: the managed and Burst builds must evaluate the same expression.
            // The clamp keeps a rounding just under u = 1 from exceeding 1, so the bound holds.
            double f = math.min(u * (15.0 + u * u * (-10.0 + 3.0 * u * u)) / 8.0, 1.0);

            double sw = 0.0, se = 0.0, r2max = t.BlendRadius2;
            for (int i = 0; i < t.MacroCount; i++)
            {
                double r2 = math.lengthsq(p - t.MacroSeed(i));
                if (!(r2 < r2max)) continue;
                double q = 1.0 - r2 / r2max;
                double w = q * q;
                sw += w;
                se += w * t.MacroElevation(i);
            }
            // Stage A's coverage radius, measured over every texel this is evaluated at, puts a
            // seed within R / 1.5 of each, so sw > 0 here; the guard is for points elsewhere.
            o.E = sw > 0.0 ? se / sw : 0.0;

            bool land = o.S >= 0.0;
            o.Height  = land ? cp.LandHeight * (1.0 - 0.5 * cp.InteriorRise + cp.InteriorRise * o.E) * f
                             : -cp.OceanDepth * f;
            o.OwnD2   = land ? land2  : ocean2;
            o.OtherD2 = land ? ocean2 : land2;
            return o;
        }

        // At a stored texel of the top level, at the point MacroGenerator.Texel evaluates it.
        public static ContinentSample AtTexel<T>(in SurfaceDef s, in RootQuad q, int n, int i, int j, T t, in ContinentParams cp)
            where T : struct, IContinentTables
        {
            double cells = n;
            return Sample(SurfaceMath.SurfacePoint(s, q, i / cells, j / cells), t, cp);
        }

        // What a texel contributes to the plateau check, in squared micro spacings: its own class's
        // distance where the other class is capped, inside the quad. Zero elsewhere -- near a coast,
        // where the transition is meant to be, and on the border (see ContinentMap.Finish).
        public static double PlateauRatio(in ContinentSample o, int n, int i, int j, in ContinentParams cp)
        {
            if (i < 0 || j < 0 || i > n || j > n || o.OtherD2 < cp.Cap2) return 0.0;
            return o.OwnD2 / (cp.MicroSpacing * cp.MicroSpacing);
        }

        // Throws if the largest PlateauRatio leaves a point inside a region off its plateau.
        public static void CheckPlateau(double worstRatio, in ContinentParams cp)
        {
            double limit = cp.PlateauLimit2 / (cp.MicroSpacing * cp.MicroSpacing);
            if (worstRatio > limit)
                throw new InvalidOperationException(
                    $"ContinentField: a warped point is {math.sqrt(worstRatio):F2} micro spacings from its own class with " +
                    $"the other beyond the cap, past the {math.sqrt(limit):F2} at which the profile is flat, so the " +
                    "plateau would ripple there. Raise MicroCells, narrow CoastWidth, or lower WarpAmplitude.");
        }

        // Stage B in managed code, the reference ContinentFieldJob is held to: every stored texel of
        // the top level into channel, narrowed once. Checks the plateau as ContinentJobs.FillTop
        // does, and returns the largest PlateauRatio.
        public static double FillTop(float[] channel, in SurfaceDef s, RootQuad[] roots, in MacroLayout layout,
                                     in ContinentParams cp, ContinentMap map)
        {
            int top = layout.LevelCount - 1, n = layout.Cells(top), w = layout.Width(top);
            int origin = layout.LevelOffset(top);
            var tables = new ArrayContinentTables(map);
            double worst = 0.0;
            for (int k = 0, count = layout.QuadCount * w * w; k < count; k++)
            {
                ContinentMap.ScanCoords(k, w, out int q, out int i, out int j);
                ContinentSample o = AtTexel(s, roots[q], n, i, j, tables, cp);
                channel[q * layout.QuadStride + origin + (j + MacroLayout.Border) * w + i + MacroLayout.Border] = (float)o.Height;
                worst = math.max(worst, PlateauRatio(o, n, i, j, cp));
            }
            CheckPlateau(worst, cp);
            return worst;
        }
    }
}
