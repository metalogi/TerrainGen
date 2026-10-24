using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // ContinentMap's tables as NativeArrays, for the jobs. The same reads as ArrayContinentTables,
    // so the jobs and the managed reference run one code path.
    public struct NativeContinentTables : IContinentTables, IDisposable
    {
        ContinentGrid _grid;
        double        _blend2;

        [ReadOnly] NativeArray<double3> _macro;
        [ReadOnly] NativeArray<double>  _elevation;
        [ReadOnly] NativeArray<double3> _micro;
        [ReadOnly] NativeArray<byte>    _land;
        [ReadOnly] NativeArray<int>     _starts;
        [ReadOnly] NativeArray<int>     _items;

        // Copies of the map's tables as they stand: before Finish the elevations and land flags
        // are all zero, which the scan does not read.
        public static NativeContinentTables From(ContinentMap m, Allocator allocator)
        {
            var t = new NativeContinentTables { _grid = m.Grid, _blend2 = m.BlendRadius2 };
            try
            {
                t._macro     = new NativeArray<double3>(m.MacroSeeds,     allocator);
                t._elevation = new NativeArray<double>(m.MacroElevation,  allocator);
                t._micro     = new NativeArray<double3>(m.MicroSeeds,     allocator);
                t._land      = new NativeArray<byte>(m.MicroLand,         allocator);
                t._starts    = new NativeArray<int>(m.CellStart,          allocator);
                t._items     = new NativeArray<int>(m.Items,              allocator);
            }
            catch
            {
                t.Dispose();
                throw;
            }
            return t;
        }

        public ContinentGrid Grid         => _grid;
        public int           MacroCount   => _macro.Length;
        public double        BlendRadius2 => _blend2;
        public double3 MacroSeed(int i)      => _macro[i];
        public double  MacroElevation(int i) => _elevation[i];
        public double3 MicroSeed(int i)      => _micro[i];
        public bool    MicroLand(int i)      => _land[i] != 0;
        public int     CellStart(int cell)   => _starts[cell];
        public int     Item(int k)           => _items[k];

        public void Dispose()
        {
            if (_macro.IsCreated)     _macro.Dispose();
            if (_elevation.IsCreated) _elevation.Dispose();
            if (_micro.IsCreated)     _micro.Dispose();
            if (_land.IsCreated)      _land.Dispose();
            if (_starts.IsCreated)    _starts.Dispose();
            if (_items.IsCreated)     _items.Dispose();
        }
    }

    public readonly struct NativeContinentScan : IContinentScan
    {
        [ReadOnly] readonly NativeArray<ContinentTexel> _a;
        public NativeContinentScan(NativeArray<ContinentTexel> a) { _a = a; }
        public ContinentTexel Get(int k) => _a[k];
    }

    // Stage A's texel scan (ContinentMap.ScanTexel) over every stored texel of the top level, in
    // ContinentMap.ScanIndex order, at the points ContinentMap.TopPoints computed in managed code.
    // Independent per texel, so a plain parallel-for.
    [BurstCompile(CompileSynchronously = true)]
    public struct ContinentTexelScanJob : IJobParallelFor
    {
        public NativeContinentTables Tables;

        [ReadOnly]  public NativeArray<double3>        Points;
        [WriteOnly] public NativeArray<ContinentTexel> Out;

        public void Execute(int k) => Out[k] = ContinentMap.ScanTexel(Points[k], Tables);
    }

    // Stage B (ContinentField) over every stored texel of the top level, at the same points,
    // written to its place in MacroLayout's index space; and each texel's PlateauRatio, in scan
    // order, for the check.
    [BurstCompile(CompileSynchronously = true)]
    public struct ContinentFieldJob : IJobParallelFor
    {
        public ContinentParams       Params;
        public NativeContinentTables Tables;
        public int                   Cells, Width, QuadStride, TopOffset;

        [ReadOnly] public NativeArray<double3> Points;

        // Each index writes one texel of the top level, a different one from its own index.
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<float> Continent;
        [WriteOnly] public NativeArray<double> Plateau;

        // Each texel's s, in scan order, when KeepCoast; otherwise one element, never written. At
        // the job's own index only, so the safety system's parallel-for check stays on for it.
        public bool KeepCoast;
        [WriteOnly] public NativeArray<float> Coast;

        public void Execute(int k)
        {
            ContinentMap.ScanCoords(k, Width, out int q, out int i, out int j);
            ContinentSample o = ContinentField.Sample(Points[k], Tables, Params);
            Continent[q * QuadStride + TopOffset + (j + MacroLayout.Border) * Width + i + MacroLayout.Border] = (float)o.Height;
            Plateau[k] = ContinentField.PlateauRatio(o, Cells, i, j, Params);
            if (KeepCoast) Coast[k] = (float)o.S;
        }
    }

    // As BurstWitnessJob, for the continent code: Compiled[0] is 1 exactly when Burst compiled
    // this job, and it calls the leaf code the continent jobs call, so an un-Burstable construct
    // anywhere in it is a compile error here rather than a silent managed fallback. The equality
    // tests assert it, since a managed fallback would pass them trivially.
    [BurstCompile(CompileSynchronously = true)]
    public struct ContinentWitnessJob : IJob
    {
        public double3               Point;
        public ContinentParams       Params;
        public NativeContinentTables Tables;

        [WriteOnly] public NativeArray<int>    Compiled;
        [WriteOnly] public NativeArray<double> Sample;

        public void Execute()
        {
            bool burst = true;
            ClearIfManaged(ref burst);
            Compiled[0] = burst ? 1 : 0;

            ContinentTexel  t = ContinentMap.ScanTexel(Point, Tables);
            ContinentSample o = ContinentField.Sample(Point, Tables, Params);
            Sample[0] = t.MacroD2 + t.MicroD2 + o.Height;
        }

        [BurstDiscard]
        static void ClearIfManaged(ref bool burst) => burst = false;
    }

    // The continent stages on workers, for MacroMaps.Build; ContinentMap and ContinentField hold
    // the managed references they are tested against. Both stages take the top level's points
    // (ContinentMap.TopPoints), computed once in managed code; the overloads without them compute
    // them, for callers running one stage alone.
    public static class ContinentJobs
    {
        // The top level's points as a NativeArray, for both stages' jobs.
        public static NativeArray<double3> TopPoints(in SurfaceDef s, RootQuad[] roots, in MacroLayout layout, Allocator allocator)
            => new NativeArray<double3>(ContinentMap.TopPoints(s, roots, layout), allocator);

        public static ContinentMap BuildMap(in SurfaceDef s, RootQuad[] roots, in MacroLayout layout, in ContinentParams cp,
                                            bool keepTopTexels = false)
        {
            var points = TopPoints(s, roots, layout, Allocator.TempJob);
            try { return BuildMap(s, roots, layout, cp, points, keepTopTexels); }
            finally { points.Dispose(); }
        }

        // Stage A with the texel scan in Burst: placement, the scan job, then the reductions,
        // BFS and threshold over its output. keepTopTexels keeps each texel's nearest micro seed
        // (ContinentMap.TopMicro).
        public static ContinentMap BuildMap(in SurfaceDef s, RootQuad[] roots, in MacroLayout layout, in ContinentParams cp,
                                            NativeArray<double3> points, bool keepTopTexels = false)
        {
            int top = layout.LevelCount - 1, w = layout.Width(top), count = layout.QuadCount * w * w;
            if (!points.IsCreated || points.Length != count)
                throw new ArgumentException("ContinentJobs: one point per stored texel of the top level, from TopPoints.", nameof(points));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var map   = ContinentMap.Place(s, roots, cp);

            var tables = NativeContinentTables.From(map, Allocator.TempJob);
            var scan   = default(NativeArray<ContinentTexel>);
            try
            {
                scan = new NativeArray<ContinentTexel>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                new ContinentTexelScanJob { Tables = tables, Points = points, Out = scan }.Schedule(count, 256).Complete();

                map.Finish(s, roots, layout, cp, new NativeContinentScan(scan));
                if (keepTopTexels)
                {
                    map.TopMicro = new int[count];
                    for (int k = 0; k < count; k++) map.TopMicro[k] = scan[k].Micro;
                }
            }
            finally
            {
                tables.Dispose();
                if (scan.IsCreated) scan.Dispose();
            }

            map.Stats.StageAMilliseconds = clock.Elapsed.TotalMilliseconds;
            return map;
        }

        public static double FillTop(NativeArray<float> channel, in SurfaceDef s, RootQuad[] roots, in MacroLayout layout,
                                     in ContinentParams cp, ContinentMap map, bool keepTopTexels = false)
        {
            var points = TopPoints(s, roots, layout, Allocator.TempJob);
            try { return FillTop(channel, points, layout, cp, map, keepTopTexels); }
            finally { points.Dispose(); }
        }

        // Stage B: the field job over the top level of channel, which must hold one value per
        // texel of the layout. Throws, through ContinentField.CheckPlateau, if a warped point inside
        // a region is too far from its own class for the plateau; returns the worst ratio.
        // keepTopTexels keeps each texel's signed coast distance (ContinentMap.TopCoast).
        public static double FillTop(NativeArray<float> channel, NativeArray<double3> points, in MacroLayout layout,
                                     in ContinentParams cp, ContinentMap map, bool keepTopTexels = false)
        {
            int top = layout.LevelCount - 1, w = layout.Width(top), count = layout.QuadCount * w * w;
            if (!channel.IsCreated || channel.Length != layout.TexelCount)
                throw new ArgumentException("ContinentJobs: the channel must have one value per texel of the layout.", nameof(channel));
            if (!points.IsCreated || points.Length != count)
                throw new ArgumentException("ContinentJobs: one point per stored texel of the top level, from TopPoints.", nameof(points));

            var tables  = NativeContinentTables.From(map, Allocator.TempJob);
            var plateau = default(NativeArray<double>);
            var coast   = default(NativeArray<float>);
            double worst = 0.0;
            try
            {
                plateau = new NativeArray<double>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                coast   = new NativeArray<float>(keepTopTexels ? count : 1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                new ContinentFieldJob
                {
                    Params = cp, Tables = tables, Cells = layout.Cells(top), Width = w,
                    QuadStride = layout.QuadStride, TopOffset = layout.LevelOffset(top),
                    Points = points, Continent = channel, Plateau = plateau,
                    KeepCoast = keepTopTexels, Coast = coast,
                }.Schedule(count, 256).Complete();
                for (int k = 0; k < count; k++) worst = math.max(worst, plateau[k]);
                if (keepTopTexels) map.TopCoast = coast.ToArray();
            }
            finally
            {
                tables.Dispose();
                if (plateau.IsCreated) plateau.Dispose();
                if (coast.IsCreated)   coast.Dispose();
            }
            ContinentField.CheckPlateau(worst, cp);
            return worst;
        }

        // Whether ContinentWitnessJob ran Burst-compiled: the continent jobs' equality tests mean
        // nothing if it did not.
        public static bool WitnessCompiled(ContinentMap map, in SurfaceDef s, in RootQuad root, in ContinentParams cp)
        {
            var tables   = NativeContinentTables.From(map, Allocator.TempJob);
            var compiled = new NativeArray<int>(1, Allocator.TempJob);
            var sample   = new NativeArray<double>(1, Allocator.TempJob);
            try
            {
                new ContinentWitnessJob
                {
                    Point = SurfaceMath.SurfacePoint(s, root, 0.375, 0.5), Params = cp, Tables = tables,
                    Compiled = compiled, Sample = sample,
                }.Schedule().Complete();
                return compiled[0] == 1 && !double.IsNaN(sample[0]);
            }
            finally
            {
                tables.Dispose();
                compiled.Dispose();
                sample.Dispose();
            }
        }
    }
}
