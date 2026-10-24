using System;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // One scalar per texel in MacroLayout's index space, read and written: a channel MacroPyramid
    // derives. A generic constraint rather than a delegate or an interface call, as IMacroTexels
    // is, so Burst specialises it for the struct it is given.
    public interface IMacroScalars
    {
        float Get(int index);
        void  Set(int index, float value);
    }

    // A list of int2s by position: the (destination, source) pairs of a fill, or the per-level
    // ranges into them.
    public interface IMacroPairs
    {
        int2 Get(int k);
    }

    // Every fill MacroPyramid applies on one surface and layout, built once: the halo pairs and
    // the edge copies of every level, each level's run contiguous, and Ranges[level] the
    // (halo, copy) start of that level's run. Ranges has LevelCount + 1 entries, so a level's
    // run ends where the next one starts.
    public sealed class MacroPyramidPlan
    {
        public readonly int2[] Halo;
        public readonly int2[] Copies;
        public readonly int2[] Ranges;

        MacroPyramidPlan(int2[] halo, int2[] copies, int2[] ranges)
        {
            Halo = halo; Copies = copies; Ranges = ranges;
        }

        public static MacroPyramidPlan Create(in SurfaceDef s, in MacroLayout layout)
        {
            var halo   = new System.Collections.Generic.List<int2>();
            var copies = new System.Collections.Generic.List<int2>();
            var ranges = new int2[layout.LevelCount + 1];
            for (int l = 0; l < layout.LevelCount; l++)
            {
                ranges[l] = new int2(halo.Count, copies.Count);
                halo.AddRange(layout.HaloSources(s, l));
                copies.AddRange(layout.EdgeCopies(s, l));
            }
            ranges[layout.LevelCount] = new int2(halo.Count, copies.Count);
            return new MacroPyramidPlan(halo.ToArray(), copies.ToArray(), ranges);
        }
    }

    // Derives every level of a macro channel from its top level: a Gaussian pyramid over the
    // root quads' maps, with the borders filled across the quads' edges. For a channel no pure
    // function can fill level by level: the continent height (Docs/Continents_Plan.md, K5), and
    // M5c's erosion correction and wetness (Docs/M5_MacroLayer_Plan.md, E4).
    //
    // The sequence, with the top level already filled border and all:
    //
    //   1. Optionally filter the top level's [0, n] once, through a scratch copy.
    //   2. Finish it: the halo, then the edge copies.
    //   3. For each level below, from the top down: decimate the level above into it -- its
    //      (i, j) is the level above filtered at (2i, 2j) -- then finish it.
    //
    // The filter is the separable binomial [1 4 6 4 1] / 16, a 5 x 5 stencil that reaches two
    // texels past the edge, inside the three-texel border. It passes about a quarter of the
    // amplitude at a level's Nyquist and three quarters at four texels per wavelength, so it is a
    // filter, not C1's hard truncation by octave. Its weights are integers summed in double and
    // divided once by 256, a power of two, so a constant passes exactly; and because they are
    // positive and rounding is monotone, the output never leaves the input's range, which keeps
    // an analytic height bound valid over a derived channel.
    //
    // The halo and the edge copies are gathers whose sources are texels neither writes
    // (MacroLayout.HaloSource), so after each level the shared edge rows are the owner's bits on
    // both quads, as C2 requires of every macro channel.
    //
    // Pure and UnityEngine-free. MacroPyramidJob runs the same functions in Burst, so the two
    // builds do the same arithmetic in the same order; MacroMapsTests holds them to the bit.
    public static class MacroPyramid
    {
        // The binomial weights 1, 4, 6, 4, 1 at offsets -2 .. 2. A switch rather than a static
        // array, which Burst cannot read from a job.
        static double Weight(int offset) => offset switch
        {
            0          => 6.0,
            1 or -1    => 4.0,
            _          => 1.0,
        };

        // The 5 x 5 binomial at (i, j) of this level, rows first, then the column. i and j must
        // be within [-1, n + 1] so the stencil stays inside the stored border.
        public static float FilterAt<T>(ref T ch, in MacroLayout layout, int quad, int level, int i, int j)
            where T : struct, IMacroScalars
        {
            int    w   = layout.Width(level);
            int    at  = layout.Index(quad, level, i, j);
            double sum = 0.0;
            for (int b = -2; b <= 2; b++)
            {
                int    r   = at + b * w;
                double row = 0.0;
                for (int a = -2; a <= 2; a++) row += Weight(a) * ch.Get(r + a);
                sum += Weight(b) * row;
            }
            return (float)(sum / 256.0);
        }

        // [0, n] of this level, filtered from src into dst.
        public static void FilterLevel<T>(ref T src, ref T dst, in MacroLayout layout, int level)
            where T : struct, IMacroScalars
        {
            int n = layout.Cells(level);
            for (int q = 0; q < layout.QuadCount; q++)
            for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
                dst.Set(layout.Index(q, level, i, j), FilterAt(ref src, layout, q, level, i, j));
        }

        // [0, n] of this level, copied from src into dst.
        public static void CopyLevel<T>(ref T src, ref T dst, in MacroLayout layout, int level)
            where T : struct, IMacroScalars
        {
            int n = layout.Cells(level);
            for (int q = 0; q < layout.QuadCount; q++)
            for (int j = 0; j <= n; j++)
            {
                int at = layout.Index(q, level, 0, j);
                for (int i = 0; i <= n; i++) dst.Set(at + i, src.Get(at + i));
            }
        }

        // [0, n] of this level, from the level above filtered at the coincident texels. The two
        // levels are disjoint ranges of the one array, so this is safe in place.
        public static void Decimate<T>(ref T ch, in MacroLayout layout, int level)
            where T : struct, IMacroScalars
        {
            int n = layout.Cells(level);
            for (int q = 0; q < layout.QuadCount; q++)
            for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
                ch.Set(layout.Index(q, level, i, j), FilterAt(ref ch, layout, q, level + 1, 2 * i, 2 * j));
        }

        // ch[dst] = ch[src] for pairs start .. end - 1.
        public static void Gather<T, P>(ref T ch, P pairs, int start, int end)
            where T : struct, IMacroScalars
            where P : struct, IMacroPairs
        {
            for (int k = start; k < end; k++)
            {
                int2 p = pairs.Get(k);
                ch.Set(p.x, ch.Get(p.y));
            }
        }

        // A level's border from its neighbours, then its shared edge rows from their owners.
        public static void FinishLevel<T, P>(ref T ch, P halo, P copies, P ranges, int level)
            where T : struct, IMacroScalars
            where P : struct, IMacroPairs
        {
            int2 from = ranges.Get(level), to = ranges.Get(level + 1);
            Gather(ref ch, halo,   from.x, to.x);
            Gather(ref ch, copies, from.y, to.y);
        }

        // The whole sequence. ch holds the top level, border included; scratch is any channel
        // of the same layout, used only when filterTop is set.
        public static void Run<T, P>(ref T ch, ref T scratch, in MacroLayout layout,
                                     P halo, P copies, P ranges, bool filterTop)
            where T : struct, IMacroScalars
            where P : struct, IMacroPairs
        {
            int top = layout.LevelCount - 1;
            if (filterTop)
            {
                FilterLevel(ref ch, ref scratch, layout, top);
                CopyLevel(ref scratch, ref ch, layout, top);
            }
            FinishLevel(ref ch, halo, copies, ranges, top);

            for (int level = top - 1; level >= 0; level--)
            {
                Decimate(ref ch, layout, level);
                FinishLevel(ref ch, halo, copies, ranges, level);
            }
        }

        // The managed reference: the same sequence over a managed array, as MacroGenerator.Build
        // is the reference for MacroBuildJob.
        public static void Run(float[] channel, in MacroLayout layout, MacroPyramidPlan plan, bool filterTop)
        {
            if (channel == null || channel.Length != layout.TexelCount)
                throw new ArgumentException("MacroPyramid: the channel must have one value per texel of the layout.",
                                            nameof(channel));
            var ch      = new ArrayScalars(channel);
            var scratch = new ArrayScalars(filterTop ? new float[channel.Length] : channel);
            Run(ref ch, ref scratch, layout, new ArrayPairs(plan.Halo), new ArrayPairs(plan.Copies),
                new ArrayPairs(plan.Ranges), filterTop);
        }

        public readonly struct ArrayScalars : IMacroScalars
        {
            readonly float[] _a;
            public ArrayScalars(float[] a) { _a = a; }
            public float Get(int index)              => _a[index];
            public void  Set(int index, float value) => _a[index] = value;
        }

        public readonly struct ArrayPairs : IMacroPairs
        {
            readonly int2[] _a;
            public ArrayPairs(int2[] a) { _a = a; }
            public int2 Get(int k) => _a[k];
        }
    }
}
