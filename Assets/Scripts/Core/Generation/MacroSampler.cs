using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // Read access to the stored macro texels, by flat MacroLayout index. MacroMaps implements it
    // over its NativeArray for the jobs; the tests implement it over a managed array. A generic
    // constraint rather than a delegate or a virtual call, so Burst specialises the read for the
    // struct it is given.
    public interface IMacroTexels
    {
        float4 Texel(int index);

        // The drainage maps' texel at the same index (M5c, E4): x wetness, y the erosion
        // correction D. Read only when MacroParams.HasErosion, so a reader of maps without erosion
        // may return anything here.
        float2 Drainage(int index);
    }

    // The bicubic read of the macro maps: Catmull-Rom on each axis, separable, rows first.
    //
    // Two properties make a sample on a shared root-quad edge bit-identical from both quads,
    // given the edge rule that stores identical texels there (MacroLayout.EdgeSource):
    //
    //   * On the edge the perpendicular coordinate is exactly 0 or n, so its weights are exactly
    //     (0, 1, 0, 0) and only the edge row contributes.
    //   * Along the edge a reversed link sees t' = 1 - t, exact for the short dyadic t a chunk
    //     vertex has, and the texels in reverse order. The weights are written
    //     (g(1-t), h(1-t), h(t), g(t)), so reversal swaps them exactly, and summed in symmetric
    //     pairs, (w0 p0 + w3 p3) + (w1 p1 + w2 p2), which IEEE addition's commutativity makes the
    //     same bits either way round.
    //
    // The textbook weights, w0 = (-t^3 + 2t^2 - t)/2 and so on, summed left to right, are
    // algebraically equal and lose both properties: MacroTests.MacroEdgesAgreeAcrossEveryLink
    // runs them as its mutation check.
    //
    // Double from the texel coordinate to the sum (C3). A macro texel at Earth radius is 40 to
    // 325 km across, and a float fraction of one quantises the read to millimetres along each
    // texel axis -- the axis-aligned staircase of M4's noise, aligned with the face grid instead.
    // The texels themselves are float; the sum narrows to MacroSample's fields once.
    public static class MacroSampler
    {
        // The most a read can exceed the texels it reads, as a factor on the largest |texel|.
        // Catmull-Rom's weights sum to one but two of them are negative, so their absolute sum is
        // 1 + t(1 - t) on each axis, 1.25 at t = 0.5, and the separable read can reach 1.25^2 of
        // the largest texel against an adversarial pattern. Smooth, band-limited maps overshoot
        // far less; TerrainHeightFunction.MaxAbsHeight needs a bound, not the typical case.
        public const double MaxOvershoot = 1.5625;

        // Catmull-Rom's two weight shapes. w3 = g(t), w2 = h(t), w1 = h(1-t), w0 = g(1-t).
        // g(0) = g(1) = h(0) = 0 and h(1) = 1 exactly, so a read on a texel takes that texel
        // with weight one and its neighbours with weight zero.
        static double G(double x) => x * x * (x - 1.0) * 0.5;
        static double H(double x) => ((-3.0 * x + 4.0) * x + 1.0) * x * 0.5;

        // The cell and weights on one axis, at texel coordinate x (u or v times Cells(level)).
        // The stencil is texels cell - 1 .. cell + 2.
        //
        // The cell is clamped to -2 .. n + 1, which keeps the stencil inside the stored
        // -Border .. n + Border for any x from -2 to n + 2 -- as far as any sample job reads, the
        // coarse job at depth 0 (MacroLayout) -- and lands every integer x in that range on its
        // own texel with weights (0, 1, 0, 0) or, at n + 2 alone, (0, 0, 1, 0). It also puts x = n
        // in cell n at t = 0 rather than cell n - 1 at t = 1, so a read exactly on the far edge has
        // the same weights as one on the near edge, which the reversal argument needs.
        public static void Weights(double x, int n, out int cell, out double4 w)
        {
            cell = math.clamp((int)math.floor(x), 1 - MacroLayout.Border, n + MacroLayout.Border - 2);
            double t = x - cell;
            double s = 1.0 - t;
            w = new double4(G(s), H(s), H(t), G(t));
        }

        // One axis of the read, in symmetric pairs. See the class comment before reordering.
        public static double4 Combine(double4 w, double4 p0, double4 p1, double4 p2, double4 p3)
            => (w.x * p0 + w.w * p3) + (w.y * p1 + w.z * p2);

        public static double2 Combine(double4 w, double2 p0, double2 p1, double2 p2, double2 p3)
            => (w.x * p0 + w.w * p3) + (w.y * p1 + w.z * p2);

        // The read every caller makes: MacroSample.Zero, reading nothing, when there is no macro
        // layer, and wetness only where there is erosion. The sample jobs, MacroMaps.Sample and
        // TerrainHeightFunction.HeightAtPoint all come through here, so none can forget a check or
        // apply it differently.
        public static MacroSample Read<T>(T texels, in MacroParams p, int quad, int level, double u, double v)
            where T : struct, IMacroTexels
        {
            if (!p.Enabled) return MacroSample.Zero;
            var m = Sample(texels, p.Layout, quad, level, u, v);
            // Saturated, because the bicubic read overshoots between texels, and wetness means
            // nothing outside 0..1.
            if (p.HasErosion) m.Wetness = math.saturate((float)SampleDrainage(texels, p.Layout, quad, level, u, v).x);
            return m;
        }

        // The macro sample at (u, v) on this quad and level.
        public static MacroSample Sample<T>(T texels, in MacroLayout layout, int quad, int level,
                                            double u, double v)
            where T : struct, IMacroTexels
        {
            int n = layout.Cells(level);
            Weights(u * n, n, out int ci, out double4 wu);
            Weights(v * n, n, out int cj, out double4 wv);

            int w   = layout.Width(level);
            int at  = layout.Index(quad, level, ci - 1, cj - 1);

            double4 r0 = Row(texels, at,         wu);
            double4 r1 = Row(texels, at + w,     wu);
            double4 r2 = Row(texels, at + 2 * w, wu);
            double4 r3 = Row(texels, at + 3 * w, wu);
            double4 m  = Combine(wv, r0, r1, r2, r3);

            return new MacroSample
            {
                BaseHeight  = m.x,
                Temperature = (float)m.y,
                Moisture    = (float)m.z,
                Ruggedness  = (float)m.w,
            };
        }

        static double4 Row<T>(T texels, int at, double4 w) where T : struct, IMacroTexels
            => Combine(w, texels.Texel(at), texels.Texel(at + 1), texels.Texel(at + 2), texels.Texel(at + 3));

        // The drainage maps at (u, v): the same cells, weights and order as Sample, so a read is
        // exact on texels and bit-identical across a shared edge, as the main maps' is.
        public static double2 SampleDrainage<T>(T texels, in MacroLayout layout, int quad, int level,
                                                double u, double v)
            where T : struct, IMacroTexels
        {
            int n = layout.Cells(level);
            Weights(u * n, n, out int ci, out double4 wu);
            Weights(v * n, n, out int cj, out double4 wv);

            int w  = layout.Width(level);
            int at = layout.Index(quad, level, ci - 1, cj - 1);

            double2 r0 = DrainageRow(texels, at,         wu);
            double2 r1 = DrainageRow(texels, at + w,     wu);
            double2 r2 = DrainageRow(texels, at + 2 * w, wu);
            double2 r3 = DrainageRow(texels, at + 3 * w, wu);
            return Combine(wv, r0, r1, r2, r3);
        }

        static double2 DrainageRow<T>(T texels, int at, double4 w) where T : struct, IMacroTexels
            => Combine(w, texels.Drainage(at), texels.Drainage(at + 1), texels.Drainage(at + 2), texels.Drainage(at + 3));
    }
}
