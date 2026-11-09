using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // What the measuring tools (ReliefMeasurements and two in ContinentTests) share beyond their
    // arithmetic: the switches that run them, the scene they measure, progress and cancelling, and
    // the faster scans the Editor plugs in. Pure, like the tools, so they still run out of the
    // Editor, where every hook below stays null and the managed reference runs.
    //
    // The switches are environment variables, read in-process, because the Editor's menu item
    // (Tools > Sonoma > Measure Open Scene, Assets/Editor/MeasureOpenScene.cs) cannot reference the
    // test assembly and sets them with Environment.SetEnvironmentVariable before running the tools:
    //
    //   SONOMA_MEASURE=1           run the tools at all (ContinentTests.MeasuringToolsOnly)
    //   SONOMA_SCENES=A,B          only these StressMacros entries
    //   SONOMA_CONTINENT_ASSET=p   the open scene's ContinentSettings asset, by its path. The entry's
    //                              own asset is what the tests read anyway (ContinentAssets); another,
    //                              a sandbox's, is measured in place of it, as the scene it copies
    //   SONOMA_SURFACE=f=v;...     with SONOMA_CONTINENT_ASSET, the open scene's surface, which must
    //                              be the entry's
    //   SONOMA_MEASURE_CANCELLED=1 set here when the progress bar's Cancel is pressed; every tool
    //                              still to run then skips
    internal static class Measuring
    {
        internal const string MeasureVar        = "SONOMA_MEASURE";
        internal const string ScenesVar         = "SONOMA_SCENES";
        internal const string ContinentAssetVar = "SONOMA_CONTINENT_ASSET";
        internal const string SurfaceVar        = "SONOMA_SURFACE";
        internal const string CancelledVar      = "SONOMA_MEASURE_CANCELLED";

        // ── The scene measured ──
        //
        // With SONOMA_CONTINENT_ASSET naming another asset than the entry's, the one scene
        // SONOMA_SCENES names has its continents and LandTarget replaced by that asset's for as long
        // as the tools run, and its cached builds dropped, since MacroTests caches them by scene name.
        // Restore puts the entry's own back; each measuring fixture calls it from its
        // OneTimeTearDown, so no other fixture sees the swap. It keeps the swapped builds aside, so
        // the next measuring fixture of the same run swaps them back in rather than building the
        // maps again. A cancelled run need not reach a teardown, and the Editor keeps the test
        // assembly loaded between runs, so ContinentAssetsAfresh also undoes any swap, and drops
        // what was kept, at the start and the end of every run (BeginRun, EndRun).

        sealed class Swap
        {
            public MacroTests.SceneMacro Entry;
            public ContinentConfig?      Continents;
            public double                LandTarget;
            public string                Key;
        }

        sealed class Kept
        {
            public MacroTests.SceneMacro Entry;
            public string                Key;
            public MacroTests.Builds     Builds;
        }

        static Swap _swap;
        static Kept _kept;

        internal static void BeginRun() { Finish?.Invoke(); Unswap(keep: false); }
        internal static void EndRun()   => BeginRun();

        // Called from MeasuringToolsOnly, after the gate, by every tool: applies the swap once, and
        // says what it measures in the tool's own output.
        internal static void Apply()
        {
            string asset = Environment.GetEnvironmentVariable(ContinentAssetVar);
            if (string.IsNullOrWhiteSpace(asset)) { Unswap(keep: false); return; }

            string scenes = Environment.GetEnvironmentVariable(ScenesVar);
            if (string.IsNullOrWhiteSpace(scenes) || scenes.Contains(","))
                Assert.Fail($"{ContinentAssetVar} names one scene's continents: name exactly one in {ScenesVar}.");
            var m = MacroTests.For(scenes.Trim());
            Assert.IsTrue(m.Continents.HasValue, $"{m.Scene} has no continents to measure");

            string surface = Environment.GetEnvironmentVariable(SurfaceVar);
            if (!string.IsNullOrWhiteSpace(surface))
            {
                string differ = Differences(m.Surface, Parse<SurfaceDef>(surface));
                Assert.IsEmpty(differ, $"the open scene's surface is not {m.Scene}'s ({differ}); the measuring tools " +
                                       "can only measure a scene with StressSceneTests' copy of its terrain");
            }

            if (asset == m.ContinentAsset)
            {
                Unswap(keep: false);
                TestContext.WriteLine($"Measuring {m.Scene} with its own {asset}.");
                return;
            }

            string key = asset + "|" + ContinentAssets.Stamp(asset).Ticks;
            if (_swap == null || _swap.Key != key || _swap.Entry != m)
            {
                bool reuse = _kept != null && _kept.Key == key && _kept.Entry == m;
                var  kept  = reuse ? _kept.Builds : default;
                Unswap(keep: false);
                var config = ContinentAssets.Read(asset, out float target);
                _swap = new Swap { Entry = m, Continents = m.Continents, LandTarget = m.LandTarget, Key = key };
                m.Continents = config;
                m.LandTarget = target;
                MacroTests.ForgetBuilds(m.Scene);
                if (reuse) MacroTests.PutBuilds(m.Scene, kept);
                if (reuse && (kept.Erosion != null || kept.Continents != null))
                    TestContext.WriteLine($"Reusing the {m.Scene} builds the previous measuring fixture made from {asset}.");
            }
            TestContext.WriteLine($"Measuring {m.Scene} with {asset} in place of {m.ContinentAsset}, TargetLand {m.LandTarget:0.###}.");
        }

        internal static void Restore()
        {
            Finish?.Invoke();
            Unswap(keep: true);
        }

        // Puts the entry's own continents back. keep sets the swapped builds aside for the next
        // fixture's Apply; otherwise they, and anything kept before, are dropped.
        static void Unswap(bool keep)
        {
            if (!keep) _kept = null;
            if (_swap == null) return;
            var builds = MacroTests.TakeBuilds(_swap.Entry.Scene);
            if (keep) _kept = new Kept { Entry = _swap.Entry, Key = _swap.Key, Builds = builds };
            _swap.Entry.Continents = _swap.Continents;
            _swap.Entry.LandTarget = _swap.LandTarget;
            _swap = null;
        }

        // ── Progress and cancelling ──
        //
        // The Editor sets Report to its cancelable progress bar; it returns true when Cancel is
        // pressed. Call it from the main thread only: the tools call it between depths and between
        // slices of nodes, never from Parallel.For's workers.
        internal static Func<string, string, float, bool> Report;
        // Clears it, when a measuring fixture finishes.
        internal static Action Finish;

        static string _title = "", _info = "";
        static float  _from, _to = 1f;

        // A tool's step, covering [from, to] of the tool's progress.
        internal static void Step(string info, float from, float to)
        {
            _info = info; _from = from; _to = to;
            Tick(0f);
        }

        internal static void Tick(float within)
        {
            if (Report == null) return;
            if (TestContext.CurrentContext?.Test != null) _title = TestContext.CurrentContext.Test.Name;
            if (Report(_title, _info, _from + (_to - _from) * Math.Max(0f, Math.Min(1f, within))))
            {
                Environment.SetEnvironmentVariable(CancelledVar, "1");
                Assert.Ignore("Cancelled from the progress bar.");
            }
        }

        internal static bool Cancelled => Environment.GetEnvironmentVariable(CancelledVar) == "1";

        // ── Faster scans ──
        //
        // The Editor's Burst versions of the slowest scans (MeasuringScans.cs, Editor-only). Each
        // returns what its managed reference in ReliefMeasurements returns, node for node:
        // MeasuringScanTests holds them to it.
        internal interface IFastScans
        {
            // MacroTests.ErodedBuild through MacroMaps.Build, which is the managed builder to the bit
            // (MacroMapsTests.ErodedMapsMatchTheManagedBuild).
            MacroTests.SceneErosion ErodedBuild(MacroTests.SceneMacro m, in HeightParams hp);

            // Each drawn node's C_m on the eroded maps (ReliefMeasurements.MacroReliefNode).
            double[] MacroRelief(MacroTests.SceneMacro m, MacroTests.SceneErosion built, int depth, int count, uint seed);

            // Each drawn node's half-relief over its size, detail and eroded base together, through
            // the real HeightSampleJob (ReliefMeasurements.CombinedReliefNode).
            double[] CombinedRelief(MacroTests.SceneMacro m, MacroTests.SceneErosion built, in HeightParams hp,
                                    int depth, int count, uint seed);
        }

        internal static IFastScans Fast;

        // ── The pinned figures ──
        //
        // One line per constant a retune re-pins, which the menu item gathers into its summary:
        //   PIN|<scene>|<constant>|<kind>|<value to pin>|<pinned now>|<budget>|<note>
        // The kind says how to compare them: "exact" is pinned to the bit, or exactly for a count;
        // "bound" is a margin over a measured worst case, so a larger value must be pinned and a
        // smaller one may be; "count" is a working set, which its tests hold to 1%. A budget is
        // "<chunks>" when the tests ask only that the set fits under it, and "<chunks>:<lo>-<hi>"
        // when they also hold it between lo and hi times the set (StressSceneTests: 1.1-1.2).
        internal static void Pin(string scene, string constant, string kind, string value, string current,
                                 string budget = "", string note = "")
            => TestContext.WriteLine($"PIN|{scene}|{constant}|{kind}|{value}|{current}|{budget}|{note}");

        // ── Struct text ──
        //
        // A struct's public fields as "Name=value;...", round-trip exact ("R"). The menu item writes
        // the open scene's SurfaceDef this way, with its own copy of the writer, since the Editor
        // assembly cannot reference this one; Parse reads it back.
        static string FormatValue(object v) => v switch
        {
            float  x => x.ToString("R", CultureInfo.InvariantCulture),
            double x => x.ToString("R", CultureInfo.InvariantCulture),
            IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString(),
        };

        internal static T Parse<T>(string text) where T : struct
        {
            object boxed = default(T);
            var    seen  = new HashSet<string>();
            foreach (string part in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                Assert.Greater(eq, 0, $"'{part}' is not Name=value");
                string name = part.Substring(0, eq).Trim(), value = part.Substring(eq + 1).Trim();
                var f = typeof(T).GetField(name, BindingFlags.Public | BindingFlags.Instance);
                Assert.IsNotNull(f, $"{typeof(T).Name} has no field {name}");
                object parsed = f.FieldType.IsEnum
                    ? Enum.Parse(f.FieldType, value)
                    : Convert.ChangeType(value, f.FieldType, CultureInfo.InvariantCulture);
                f.SetValue(boxed, parsed);
                seen.Add(name);
            }
            foreach (var f in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
                Assert.IsTrue(seen.Contains(f.Name), $"{typeof(T).Name}.{f.Name} is missing from '{text}'");
            return (T)boxed;
        }

        // The fields two values of a struct differ in, with both values, or "".
        internal static string Differences<T>(T copy, T asset) where T : struct
        {
            var parts = new List<string>();
            foreach (var f in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object a = f.GetValue(copy), b = f.GetValue(asset);
                if (!Equals(a, b)) parts.Add($"{f.Name} (copy {FormatValue(a)}, asset {FormatValue(b)})");
            }
            return string.Join(", ", parts);
        }
    }
}
