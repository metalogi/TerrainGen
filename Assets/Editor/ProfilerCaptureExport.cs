using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

// Exports the frames the Profiler window holds -- a live capture or a loaded .data file -- to
// three files in Logs/, and logs a summary against the 2 ms terrain budget.
//
// The Profiler window shows one frame at a time, so the questions the profiler pass asks -- how
// often TerrainRoot.Update runs over budget, what the worst rebase costs, whether anything
// allocates -- need every frame at once.
//
//   ProfilerCapture.csv  one row a frame: the main thread's CPU frame time; the terrain's own
//                        main-thread time and the GC it allocates (TerrainRoot.Update, inclusive);
//                        WorldOriginSystem.LateUpdate and the ShiftOrigin marker inside it; all
//                        main-thread GC; and the generation jobs' time summed over the workers.
//   ProfilerMarkers.csv  every main-thread marker over the capture: frames it ran in, total, mean
//                        and worst time a frame, sorted by total.
//   ProfilerGc.csv       every main-thread allocation site, by its chain of enclosing markers.
//
// Names are matched by substring. The Profiler's name for a script callback varies by version
// and build -- "TerrainRoot.Update()", "TerrainRoot.Update() [Invoke]" -- and the first capture
// this read matched neither the exact form nor the prefix; the marker table is there so the
// names actually recorded can be read off rather than guessed.
static class ProfilerCaptureExport
{
    const string TerrainUpdate    = "TerrainRoot.Update";
    const string OriginLateUpdate = "WorldOriginSystem.LateUpdate";
    const string ShiftOrigin      = "WorldOriginSystem.ShiftOrigin";
    const string GcAlloc          = "GC.Alloc";
    const double BudgetMs         = 2.0;

    // HeightSampleJob covers CoarseHeightSampleJob too.
    static readonly string[] JobNames = { "HeightSampleJob", "ChunkMeshJob" };

    enum Kind { None, Terrain, OriginLate, Shift, Gc, Job }

    struct Marker { public Kind Kind; public string Name; }

    struct Frame
    {
        public int    Index;
        public double FrameMs, TerrainMs, OriginLateMs, ShiftMs, JobMs;
        public long   TerrainGc, MainGc;
    }

    sealed class MarkerStats { public int Frames; public long Calls; public double TotalMs, MaxFrameMs; }
    sealed class GcStats     { public int Frames, LastFrame = -1; public long Count, Bytes; }

    // Everything one export accumulates; Walk adds to it sample by sample.
    sealed class Capture
    {
        public readonly Dictionary<int, Marker>         Markers    = new();   // per view
        public readonly List<string>                    Path       = new();   // enclosing markers
        public readonly Dictionary<string, double>      FrameMs    = new();   // per frame
        public readonly Dictionary<string, int>         FrameCalls = new();   // per frame
        public readonly Dictionary<string, MarkerStats> Table      = new();
        public readonly Dictionary<string, GcStats>     Gc         = new();
    }

    [MenuItem("Tools/Sonoma/Export Profiler Capture")]
    static void Export()
    {
        int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
        if (first < 0 || last < first)
        {
            Debug.LogWarning("[ProfilerCaptureExport] the Profiler holds no frames. Record or load a capture first.");
            return;
        }

        var frames  = new List<Frame>(last - first + 1);
        var capture = new Capture();
        try
        {
            for (int f = first; f <= last; f++)
            {
                if ((f - first) % 50 == 0 &&
                    EditorUtility.DisplayCancelableProgressBar("Export Profiler Capture",
                        $"Frame {f - first + 1} of {last - first + 1}", (f - first) / (float)(last - first + 1)))
                    return;

                var frame = new Frame { Index = f };
                capture.FrameMs.Clear();
                capture.FrameCalls.Clear();
                for (int thread = 0; ; thread++)
                {
                    using var view = ProfilerDriver.GetRawFrameDataView(f, thread);
                    if (view == null || !view.valid) break;
                    if (thread == 0) frame.FrameMs = view.frameTimeMs;

                    capture.Markers.Clear();   // marker ids are per view; resolve each name once a view
                    capture.Path.Clear();
                    int i = 0;
                    while (i < view.sampleCount)
                        Walk(view, ref i, thread == 0, false, false, capture, ref frame);
                }
                foreach (var (name, ms) in capture.FrameMs)
                {
                    if (!capture.Table.TryGetValue(name, out var stats)) capture.Table[name] = stats = new MarkerStats();
                    stats.Frames++;
                    stats.Calls     += capture.FrameCalls[name];
                    stats.TotalMs   += ms;
                    stats.MaxFrameMs = Math.Max(stats.MaxFrameMs, ms);
                }
                frames.Add(frame);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"));
        Directory.CreateDirectory(dir);

        var csv = new StringBuilder("frame,frameMs,terrainMs,terrainGcBytes,originLateUpdateMs,shiftOriginMs,mainGcBytes,workerJobMs\n");
        foreach (var fr in frames)
            csv.Append(string.Format(CultureInfo.InvariantCulture, "{0},{1:F4},{2:F4},{3},{4:F4},{5:F4},{6},{7:F4}\n",
                fr.Index, fr.FrameMs, fr.TerrainMs, fr.TerrainGc, fr.OriginLateMs, fr.ShiftMs, fr.MainGc, fr.JobMs));
        File.WriteAllText(Path.Combine(dir, "ProfilerCapture.csv"), csv.ToString());

        var table = new StringBuilder("name,frames,calls,totalMs,meanMsPerFrame,maxMsPerFrame\n");
        foreach (var (name, s) in capture.Table.OrderByDescending(kv => kv.Value.TotalMs))
            table.Append(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F3},{4:F4},{5:F4}\n",
                Quote(name), s.Frames, s.Calls, s.TotalMs, s.TotalMs / s.Frames, s.MaxFrameMs));
        File.WriteAllText(Path.Combine(dir, "ProfilerMarkers.csv"), table.ToString());

        var gc = new StringBuilder("bytes,allocations,frames,path\n");
        foreach (var (site, s) in capture.Gc.OrderByDescending(kv => kv.Value.Bytes))
            gc.Append($"{s.Bytes},{s.Count},{s.Frames},{Quote(site)}\n");
        File.WriteAllText(Path.Combine(dir, "ProfilerGc.csv"), gc.ToString());

        Debug.Log(Summary(frames, capture) + $"\nWritten to {dir}: ProfilerCapture.csv, ProfilerMarkers.csv, ProfilerGc.csv");
    }

    // One sample and, recursively, its children: samples come depth first, each followed by its
    // subtree, so the walk knows every sample's ancestors without a parent index.
    static void Walk(RawFrameDataView view, ref int i, bool mainThread, bool inTerrain, bool inJob,
                     Capture capture, ref Frame frame)
    {
        int    sample = i++;
        Marker marker = MarkerOf(view, sample, capture.Markers);
        double ms     = view.GetSampleTimeMs(sample);

        if (mainThread)
        {
            if (marker.Kind == Kind.Terrain && !inTerrain) frame.TerrainMs    += ms;
            if (marker.Kind == Kind.OriginLate)            frame.OriginLateMs += ms;
            if (marker.Kind == Kind.Shift)                 frame.ShiftMs      += ms;
            if (marker.Kind == Kind.Gc && view.GetSampleMetadataCount(sample) > 0)
            {
                long bytes = view.GetSampleMetadataAsLong(sample, 0);
                frame.MainGc += bytes;
                if (inTerrain) frame.TerrainGc += bytes;

                string site = capture.Path.Count == 0 ? "(top level)" : string.Join(" > ", capture.Path);
                if (!capture.Gc.TryGetValue(site, out var g)) capture.Gc[site] = g = new GcStats();
                g.Count++;
                g.Bytes += bytes;
                if (g.LastFrame != frame.Index) { g.Frames++; g.LastFrame = frame.Index; }
            }

            // A marker nested inside itself would be counted twice; take the outermost.
            if (!capture.Path.Contains(marker.Name))
            {
                capture.FrameMs.TryGetValue(marker.Name, out double sum);
                capture.FrameMs[marker.Name] = sum + ms;
            }
            capture.FrameCalls.TryGetValue(marker.Name, out int calls);
            capture.FrameCalls[marker.Name] = calls + 1;
        }
        else if (marker.Kind == Kind.Job && !inJob)
            frame.JobMs += ms;

        bool childInTerrain = inTerrain || marker.Kind == Kind.Terrain;
        bool childInJob     = inJob     || marker.Kind == Kind.Job;
        int  children       = view.GetSampleChildrenCount(sample);
        if (mainThread) capture.Path.Add(marker.Name);
        for (int c = 0; c < children && i < view.sampleCount; c++)
            Walk(view, ref i, mainThread, childInTerrain, childInJob, capture, ref frame);
        if (mainThread) capture.Path.RemoveAt(capture.Path.Count - 1);
    }

    static Marker MarkerOf(RawFrameDataView view, int sample, Dictionary<int, Marker> markers)
    {
        int id = view.GetSampleMarkerId(sample);
        if (markers.TryGetValue(id, out var marker)) return marker;

        // Some samples have no name -- the exporter met them on the first capture it read.
        string name = view.GetSampleName(sample);
        if (string.IsNullOrEmpty(name)) name = "(unnamed)";
        marker = new Marker
        {
            Name = name,
            Kind = name.Contains(TerrainUpdate)                     ? Kind.Terrain
                 : name.Contains(OriginLateUpdate)                  ? Kind.OriginLate
                 : name == ShiftOrigin                              ? Kind.Shift
                 : name == GcAlloc                                  ? Kind.Gc
                 : Array.Exists(JobNames, j => name.Contains(j))    ? Kind.Job
                 : Kind.None,
        };
        markers[id] = marker;
        return marker;
    }

    static string Summary(List<Frame> frames, Capture capture)
    {
        var terrain = new List<double>(frames.Count);
        var frameMs = new List<double>(frames.Count);
        var jobs    = new List<double>(frames.Count);
        int overBudget = 0, allocating = 0, shifts = 0, allocatingAtAll = 0;
        long terrainGc = 0, mainGc = 0;
        double worstShift = 0, worstShiftFrame = 0;
        foreach (var fr in frames)
        {
            frameMs.Add(fr.FrameMs);
            jobs.Add(fr.JobMs);
            if (fr.TerrainMs > 0) terrain.Add(fr.TerrainMs);
            if (fr.TerrainMs > BudgetMs) overBudget++;
            if (fr.TerrainGc > 0) { allocating++; terrainGc += fr.TerrainGc; }
            if (fr.MainGc > 0)    { allocatingAtAll++; mainGc += fr.MainGc; }
            if (fr.ShiftMs > 0)
            {
                shifts++;
                if (fr.ShiftMs > worstShift) { worstShift = fr.ShiftMs; worstShiftFrame = fr.TerrainMs + fr.OriginLateMs; }
            }
        }

        var sb = new StringBuilder();
        sb.Append(string.Format(CultureInfo.InvariantCulture,
            "[ProfilerCaptureExport] {0} frames. Main-thread frame: median {1:F2} ms, p99 {2:F2}. " +
            "Generation jobs across workers: median {3:F3} ms a frame, max {4:F3}. {5} rebases, the slowest {6:F3} ms. " +
            "Main thread allocated in {7} frames, {8} bytes in all.",
            frames.Count, Percentile(frameMs, 0.5), Percentile(frameMs, 0.99), Percentile(jobs, 0.5), Percentile(jobs, 1.0),
            shifts, worstShift, allocatingAtAll, mainGc));

        if (terrain.Count == 0)
            sb.Append($" No marker containing \"{TerrainUpdate}\": read ProfilerMarkers.csv for what the capture calls it.");
        else
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                " TerrainRoot.Update: median {0:F3} ms, p95 {1:F3}, p99 {2:F3}, max {3:F3}; {4} frames over the {5} ms budget; " +
                "it allocated in {6} frames ({7} bytes). The slowest rebase's frame spent {8:F3} ms in terrain and origin work.",
                Percentile(terrain, 0.5), Percentile(terrain, 0.95), Percentile(terrain, 0.99), Percentile(terrain, 1.0),
                overBudget, BudgetMs, allocating, terrainGc, worstShiftFrame));

        foreach (var (site, g) in capture.Gc.OrderByDescending(kv => kv.Value.Bytes).Take(3))
            sb.Append($"\n  GC {g.Bytes} bytes in {g.Frames} frames: {site}");
        return sb.ToString();
    }

    static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = new List<double>(values);
        sorted.Sort();
        int at = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(at, 0, sorted.Count - 1)];
    }
}
