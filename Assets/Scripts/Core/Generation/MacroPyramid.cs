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

    // What MacroPyramid does with the top level before it derives the levels below.
    public enum MacroPyramidTop
    {
        // Filter the top level's [0, n] once, reading the caller's border, then replace the border
        // with the neighbours' filtered texels: an unfiltered border beside a filtered interior
        // would put a step in every read near an edge. The continent height (Docs/Continents_Plan.md,
        // K5), whose border Stage B computes at the continued points.
        FilterAndFill,

        // No filter; fill the border from the neighbours. For a channel the caller wrote only in
        // [0, n], such as M5c's erosion correction, computed on the surface graph.
        Fill,

        // No filter, and the caller's border kept: for a border the caller computed itself and
        // trusts, such as a function continued past the edge, which across a cube edge is closer
        // to the truth than the halo. Only the edge copies are applied.
        Keep,
    }

    // Every fill MacroPyramid applies on one surface and layout, built once: the halo pairs and
    // the edge copies of every level, each level's run contiguous, and Ranges[level] the
    // (halo, copy) start of that level's run. Ranges has LevelCount + 1 entries, so a level's
    // run ends where the next one starts.
    public sealed class MacroPyramidPlan
    {
        public readonly MacroLayout Layout;
        public readonly int2[]      Halo;
        public readonly int2[]      Copies;
        public readonly int2[]      Ranges;

        MacroPyramidPlan(in MacroLayout layout, int2[] halo, int2[] copies, int2[] ranges)
        {
            Layout = layout; Halo = halo; Copies = copies; Ranges = ranges;
        }

        public static MacroPyramidPlan Create(in SurfaceDef s, in MacroLayout layout)
        {
            if (s.QuadCount != layout.QuadCount)
                throw new ArgumentException("MacroPyramidPlan: the layout was made for a surface with a different number of root quads.",
                                            nameof(layout));

            var halo   = new int2[layout.LevelCount][];
            var copies = new int2[layout.LevelCount][];
            var ranges = new int2[layout.LevelCount + 1];
            for (int l = 0; l < layout.LevelCount; l++)
            {
                halo[l]       = layout.HaloSources(s, l);
                copies[l]     = layout.EdgeCopies(s, l);
                ranges[l + 1] = ranges[l] + new int2(halo[l].Length, copies[l].Length);
            }
            return new MacroPyramidPlan(layout, Concat(halo, ranges[layout.LevelCount].x),
                                        Concat(copies, ranges[layout.LevelCount].y), ranges);
        }

        static int2[] Concat(int2[][] parts, int total)
        {
            var all = new int2[total];
            int at  = 0;
            foreach (int2[] p in parts)
            {
                Array.Copy(p, 0, all, at, p.Length);
                at += p.Length;
            }
            return all;
        }
    }

    // Derives every level of a macro channel from its top level: a Gaussian pyramid over the
    // root quads' maps, with the borders filled across the quads' edges. For a channel no pure
    // function can fill level by level: the continent height (Docs/Continents_Plan.md, K5), and
    // M5c's erosion correction and wetness (Docs/M5_MacroLayer_Plan.md, E4).
    //
    // The sequence, with the top level already filled (MacroPyramidTop says how its border is
    // treated):
    //
    //   1. The top level: filtered once if asked, then finished.
    //   2. For each level below, from the top down: decimate the level above into it -- its
    //      (i, j) is the level above filtered at (2i, 2j) -- then finish it.
    //
    // Finishing a level is its halo, then its edge copies, in that order: the copies read the
    // owners' extension texels, which are border texels the halo fills (MacroLayout.HaloSource).
    // After it the shared edge rows are the owner's bits on both quads, as C2 requires of every
    // macro channel.
    //
    // The filter is the separable binomial [1 4 6 4 1] / 16, a 5 x 5 stencil that reaches two
    // texels past the edge, inside the three-texel border. It passes about a quarter of the
    // amplitude at a level's Nyquist and three quarters at four texels per wavelength, so it is a
    // filter, not C1's hard truncation by octave. Its weights are integers summed in double and
    // divided once by 256, a power of two, so a constant passes exactly; and because they are
    // positive and rounding is monotone, the output never leaves the input's range, which keeps
    // an analytic height bound valid over a derived channel.
    //
    // Pure and UnityEngine-free. MacroPyramidJob runs the same functions in Burst, so the two
    // builds do the same arithmetic in the same order; MacroMapsTests holds them to the bit.
    public static class MacroPyramid
    {
        // The binomial weights 1, 4, 6, 4, 1 at offsets -2 .. 2. A switch rather than a static
        // array, which Burst cannot read from a job.
        static double Weight(int offset) => offset switch
        {
            0       => 6.0,
            1 or -1 => 4.0,
            _       => 1.0,
        };

        // The 5 x 5 binomial centred on flat index at, in a level whose rows are w texels wide:
        // rows first, then the column. The centre must be at least two texels inside the stored
        // border on both axes.
        static float Filter<T>(ref T ch, int at, int w) where T : struct, IMacroScalars
        {
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

        // The flat index of texel (0, 0) of this quad and level, and the level's row width. The
        // texel loops step from it by one along a row and by the width down a column, rather than
        // calling MacroLayout.Index, whose level offset is a loop over the levels below.
        static int Origin(in MacroLayout layout, int levelOffset, int quad, int w)
            => quad * layout.QuadStride + levelOffset + MacroLayout.Border * (w + 1);

        // [0, n] of this level, filtered from src into dst.
        public static void FilterLevel<T>(ref T src, ref T dst, in MacroLayout layout, int level)
            where T : struct, IMacroScalars
        {
            int n = layout.Cells(level), w = layout.Width(level), offset = layout.LevelOffset(level);
            for (int q = 0; q < layout.QuadCount; q++)
            {
                int origin = Origin(layout, offset, q, w);
                for (int j = 0; j <= n; j++)
                {
                    int row = origin + j * w;
                    for (int i = 0; i <= n; i++) dst.Set(row + i, Filter(ref src, row + i, w));
                }
            }
        }

        // [0, n] of this level, copied from src into dst.
        public static void CopyLevel<T>(ref T src, ref T dst, in MacroLayout layout, int level)
            where T : struct, IMacroScalars
        {
            int n = layout.Cells(level), w = layout.Width(level), offset = layout.LevelOffset(level);
            for (int q = 0; q < layout.QuadCount; q++)
            {
                int origin = Origin(layout, offset, q, w);
                for (int j = 0; j <= n; j++)
                {
                    int row = origin + j * w;
                    for (int i = 0; i <= n; i++) dst.Set(row + i, src.Get(row + i));
                }
            }
        }

        // [0, n] of this level, from the level above filtered at the coincident texels. The two
        // levels are disjoint ranges of the one array, so this is safe in place.
        public static void Decimate<T>(ref T ch, in MacroLayout layout, int level)
            where T : struct, IMacroScalars
        {
            int n  = layout.Cells(level);
            int w  = layout.Width(level),     offset  = layout.LevelOffset(level);
            int wa = layout.Width(level + 1), offsetA = layout.LevelOffset(level + 1);
            for (int q = 0; q < layout.QuadCount; q++)
            {
                int origin = Origin(layout, offset, q, w), originA = Origin(layout, offsetA, q, wa);
                for (int j = 0; j <= n; j++)
                {
                    int row = origin + j * w, rowA = originA + 2 * j * wa;
                    for (int i = 0; i <= n; i++) ch.Set(row + i, Filter(ref ch, rowA + 2 * i, wa));
                }
            }
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

        // A level's border from its neighbours, unless it is kept, then its shared edge rows from
        // their owners. The copies come second: they read owners' extension texels, which the
        // halo fills.
        public static void FinishLevel<T, P>(ref T ch, P halo, P copies, P ranges, int level, bool fillHalo)
            where T : struct, IMacroScalars
            where P : struct, IMacroPairs
        {
            int2 from = ranges.Get(level), to = ranges.Get(level + 1);
            if (fillHalo) Gather(ref ch, halo, from.x, to.x);
            Gather(ref ch, copies, from.y, to.y);
        }

        // The whole sequence. ch holds the top level; scratch is any channel of the same layout,
        // used only by FilterAndFill.
        public static void Run<T, P>(ref T ch, ref T scratch, in MacroLayout layout,
                                     P halo, P copies, P ranges, MacroPyramidTop topMode)
            where T : struct, IMacroScalars
            where P : struct, IMacroPairs
        {
            int top = layout.LevelCount - 1;
            if (topMode == MacroPyramidTop.FilterAndFill)
            {
                FilterLevel(ref ch, ref scratch, layout, top);
                CopyLevel(ref scratch, ref ch, layout, top);
            }
            FinishLevel(ref ch, halo, copies, ranges, top, fillHalo: topMode != MacroPyramidTop.Keep);

            for (int level = top - 1; level >= 0; level--)
            {
                Decimate(ref ch, layout, level);
                FinishLevel(ref ch, halo, copies, ranges, level, fillHalo: true);
            }
        }

        // What both overloads of Run check before touching an array: a mismatch would index out of
        // range, which is an exception in the Editor and a read or write outside the array in a
        // build. The plan must be for this layout, and the channel one value per texel of it.
        public static void Validate(int channelLength, in MacroLayout layout, MacroPyramidPlan plan)
        {
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));
            MacroLayout pl = plan.Layout;
            if (pl.Cells0 != layout.Cells0 || pl.LevelCount != layout.LevelCount || pl.QuadCount != layout.QuadCount)
                throw new ArgumentException("MacroPyramid: the plan was made for a different layout.", nameof(plan));
            if (plan.Ranges == null || plan.Ranges.Length != layout.LevelCount + 1 ||
                plan.Halo == null || plan.Copies == null ||
                plan.Ranges[layout.LevelCount].x != plan.Halo.Length ||
                plan.Ranges[layout.LevelCount].y != plan.Copies.Length)
                throw new ArgumentException("MacroPyramid: the plan's level ranges do not match its lists.", nameof(plan));
            if (channelLength != layout.TexelCount)
                throw new ArgumentException("MacroPyramid: the channel must have one value per texel of the layout.");
        }

        // The managed reference: the same sequence over a managed array, as MacroGenerator.Build
        // is the reference for MacroBuildJob.
        public static void Run(float[] channel, in MacroLayout layout, MacroPyramidPlan plan, MacroPyramidTop topMode)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            Validate(channel.Length, layout, plan);
            var ch      = new ArrayScalars(channel);
            var scratch = new ArrayScalars(topMode == MacroPyramidTop.FilterAndFill ? new float[channel.Length] : channel);
            Run(ref ch, ref scratch, layout, new ArrayPairs(plan.Halo), new ArrayPairs(plan.Copies),
                new ArrayPairs(plan.Ranges), topMode);
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
