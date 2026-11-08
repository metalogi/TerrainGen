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
    //   SONOMA_CONTINENTS=f=v;...  measure with this ContinentConfig in place of the entry's copy:
    //                              the open scene's asset, so a regenerated asset is measured
    //                              before its copy is updated, and a sandbox as the scene it copies
    //   SONOMA_LAND_TARGET=0.3     with SONOMA_CONTINENTS, the asset's TargetLand
    //   SONOMA_SURFACE=f=v;...     with SONOMA_CONTINENTS, the open scene's surface, which must be
    //                              the entry's
    //   SONOMA_MEASURE_CANCELLED=1 set here when the progress bar's Cancel is pressed; every tool
    //                              still to run then skips
    internal static class Measuring
    {
        internal const string MeasureVar     = "SONOMA_MEASURE";
        internal const string ScenesVar      = "SONOMA_SCENES";
        internal const string ContinentsVar  = "SONOMA_CONTINENTS";
        internal const string LandTargetVar  = "SONOMA_LAND_TARGET";
        internal const string SurfaceVar     = "SONOMA_SURFACE";
        internal const string CancelledVar   = "SONOMA_MEASURE_CANCELLED";

        // ── The scene measured ──
        //
        // With SONOMA_CONTINENTS set, the one scene SONOMA_SCENES names has its continent copy, and
        // LandTarget, replaced for as long as the tools run, and its cached builds dropped, since
        // MacroTests caches them by scene name. Restore puts the copy back; each measuring fixture
        // calls it from its OneTimeTearDown, so the suite never sees the swap.

        sealed class Swap
        {
            public MacroTests.SceneMacro Entry;
            public ContinentConfig?      Continents;
            public double                LandTarget;
            public string                Key;
        }

        static Swap _swap;

        // Called from MeasuringToolsOnly, after the gate, by every tool: applies the override once,
        // and says what it measures in the tool's own output.
        internal static void Apply()
        {
            string text = Environment.GetEnvironmentVariable(ContinentsVar);
            if (string.IsNullOrWhiteSpace(text)) { Unswap(); return; }

            string scenes = Environment.GetEnvironmentVariable(ScenesVar);
            if (string.IsNullOrWhiteSpace(scenes) || scenes.Contains(","))
                Assert.Fail($"{ContinentsVar} replaces one scene's continents: name exactly one in {ScenesVar}.");
            var    m   = MacroTests.For(scenes.Trim());
            string key = text + "|" + Environment.GetEnvironmentVariable(LandTargetVar);
            if (_swap != null && _swap.Key == key && _swap.Entry == m) { Describe(m); return; }
            Unswap();

            Assert.IsTrue(m.Continents.HasValue, $"{m.Scene} has no continents to replace");
            string surface = Environment.GetEnvironmentVariable(SurfaceVar);
            if (!string.IsNullOrWhiteSpace(surface))
            {
                var open = Parse<SurfaceDef>(surface);
                string differ = Differences(m.Surface, open);
                Assert.IsEmpty(differ, $"the open scene's surface is not {m.Scene}'s ({differ}); the measuring tools " +
                                       "can only measure a scene with StressSceneTests' copy of its terrain");
            }

            var config = Parse<ContinentConfig>(text);
            _swap = new Swap { Entry = m, Continents = m.Continents, LandTarget = m.LandTarget, Key = key };
            _copyDiffers = Differences(m.Continents.Value, config);
            m.Continents = config;
            string target = Environment.GetEnvironmentVariable(LandTargetVar);
            if (!string.IsNullOrWhiteSpace(target))
            {
                double t = double.Parse(target, CultureInfo.InvariantCulture);
                _landTargetDiffers = t != m.LandTarget ? $"LandTarget (copy {m.LandTarget:R}, asset {t:R})" : "";
                m.LandTarget = t;
            }
            else _landTargetDiffers = "";
            MacroTests.ForgetBuilds(m.Scene);
            Describe(m);
        }

        static string _copyDiffers = "", _landTargetDiffers = "";

        static void Describe(MacroTests.SceneMacro m)
        {
            string differs = string.Join(", ", new[] { _copyDiffers, _landTargetDiffers }.Where(s => s.Length > 0));
            TestContext.WriteLine($"Measuring {m.Scene} with the open scene's continent asset; " +
                                  (differs.Length == 0 ? "MacroTests.StressMacros' copy matches it."
                                                       : $"MacroTests.StressMacros' copy differs in {differs}: update it before re-pinning."));
        }

        internal static void Restore()
        {
            Finish?.Invoke();
            Unswap();
        }

        static void Unswap()
        {
            if (_swap == null) return;
            _swap.Entry.Continents = _swap.Continents;
            _swap.Entry.LandTarget = _swap.LandTarget;
            MacroTests.ForgetBuilds(_swap.Entry.Scene);
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
        // The kind says how to compare them: "exact" is pinned to the bit; "bound" is a margin over
        // a measured worst case, so a larger value must be pinned and a smaller one may be; "count"
        // is a working set, which its tests hold to 1% and its budget to 10-20% above.
        internal static void Pin(string scene, string constant, string kind, string value, string current,
                                 string budget = "", string note = "")
            => TestContext.WriteLine($"PIN|{scene}|{constant}|{kind}|{value}|{current}|{budget}|{note}");

        // ── Struct text ──
        //
        // A struct's public fields as "Name=value;...", round-trip exact ("R"), and back. The menu
        // item writes the open scene's ContinentConfig and SurfaceDef this way.
        internal static string Format<T>(T value) where T : struct
        {
            var parts = new List<string>();
            foreach (var f in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
                parts.Add(f.Name + "=" + FormatValue(f.GetValue(value)));
            return string.Join(";", parts);
        }

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
