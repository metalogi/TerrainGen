using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using Sonoma.Core.Quadtree;
using Sonoma.Systems.Configuration;
using Debug = UnityEngine.Debug;

// Tools > Sonoma > Measure Open Scene: re-measures the figures a continent scene pins, from inside
// the Editor, so a scene regenerated in the continent designer can be re-pinned without the
// out-of-Editor harness (Docs/Continents_Plan.md, the designer amendment, "Retuning a pinned scene").
//
// The figures come from the explicit measuring tools in the test assembly (ReliefMeasurements),
// which this cannot reference, so it talks to them the way the harness does, through environment
// variables set in this process (Sonoma.Tests.Measuring lists them): SONOMA_MEASURE=1, the scene,
// and the open scene's ContinentSettings as text, so what is measured is the asset as it is now,
// saved or not, whether or not MacroTests.StressMacros' copy has caught up. It runs the tools
// through TestRunnerApi, writes everything they print to Logs/Measurements/<scene>.txt, and logs
// each pinned constant's new value beside the one the tests pin now.
//
// The tools measure a scene through its entry in MacroTests.StressMacros and StressSceneTests, so
// the open scene must be one of the four pinned continent scenes, or a copy of one whose terrain
// and macro settings are the same apart from its continent asset: ContinentSandbox is EarthStress
// with its own SandboxContinents, and is measured as EarthStress with that asset.
//
// The default runs the tools that produce pinned figures, with the eroded scans in Burst
// (MeasuringScans). "Every Tool" adds the rest: the detail C, the uneroded C_m and skirt shares,
// the land calibration Regenerate superseded, and the two in ContinentTests.
[InitializeOnLoad]
static class MeasureOpenScene
{
    // The variables Sonoma.Tests.Measuring reads.
    const string MeasureVar    = "SONOMA_MEASURE";
    const string ScenesVar     = "SONOMA_SCENES";
    const string ContinentsVar = "SONOMA_CONTINENTS";
    const string LandTargetVar = "SONOMA_LAND_TARGET";
    const string SurfaceVar    = "SONOMA_SURFACE";
    const string CancelledVar  = "SONOMA_MEASURE_CANCELLED";
    static readonly string[] Variables = { MeasureVar, ScenesVar, ContinentsVar, LandTargetVar, SurfaceVar, CancelledVar };

    const string RunningKey = "Sonoma.MeasureOpenScene.Running";

    // The pinned continent scenes and the terrain settings each runs.
    static readonly (string Scene, string Settings)[] Pinned =
    {
        ("EarthStress",       "Assets/Settings/EarthTerrainSettings.asset"),
        ("SmallPlanetStress", "Assets/Settings/SmallPlanetTerrainSettings.asset"),
        ("RingworldStress",   "Assets/Settings/RingworldTerrainSettings.asset"),
        ("NarrowRingStress",  "Assets/Settings/NarrowRingTerrainSettings.asset"),
    };

    const string Relief = "Sonoma.Tests.ReliefMeasurements.";
    static readonly string[] PinnedTools =
    {
        Relief + "ErodedMaps", Relief + "ErodedMacroReliefFactor", Relief + "ErodedCombinedRelief",
        Relief + "ErodedSkirtShares", Relief + "WorkingSets",
    };
    static readonly string[] OtherTools =
    {
        Relief + "DetailReliefFactor", Relief + "MacroReliefFactor", Relief + "SkirtShares", Relief + "CalibrateLandFraction",
        "Sonoma.Tests.ContinentTests.ContinentsSurviveTheDetailNoiseAtFullSample", "Sonoma.Tests.ContinentTests.ClimateOverLand",
    };

    // A domain reload ends any run's bookkeeping here; leave nothing set that would make the next
    // Run All take the measuring tools for real.
    static MeasureOpenScene()
    {
        if (SessionState.GetBool(RunningKey, false))
        {
            Debug.LogWarning("[Measure] A domain reload interrupted the measurement; its output so far is in the Test Runner window.");
            SessionState.SetBool(RunningKey, false);
        }
        ClearVariables();
    }

    [MenuItem("Tools/Sonoma/Measure Open Scene")]
    static void MeasurePinned() => Run(PinnedTools);

    [MenuItem("Tools/Sonoma/Measure Open Scene (Every Tool)")]
    static void MeasureEverything()
    {
        var all = new List<string>(PinnedTools);
        all.AddRange(OtherTools);
        Run(all.ToArray());
    }

    [MenuItem("Tools/Sonoma/Measure Open Scene", true)]
    [MenuItem("Tools/Sonoma/Measure Open Scene (Every Tool)", true)]
    static bool CanMeasure() => !EditorApplication.isPlayingOrWillChangePlaymode && _run == null;

    static Collector _run;

    static void Run(string[] tools)
    {
        string scene = SceneManager.GetActiveScene().name;
        var    root  = UnityEngine.Object.FindAnyObjectByType<TerrainRoot>();
        var    continents = root != null && root.Settings != null && root.Settings.Macro != null ? root.Settings.Macro.Continents : null;
        if (continents == null)
        {
            EditorUtility.DisplayDialog("Measure Open Scene", $"{scene} has no TerrainRoot with a continent asset to measure.", "OK");
            return;
        }
        if (!Resolve(scene, root.Settings, out string measuredAs, out string why))
        {
            EditorUtility.DisplayDialog("Measure Open Scene", why, "OK");
            return;
        }

        ClearVariables();
        Environment.SetEnvironmentVariable(MeasureVar,    "1");
        Environment.SetEnvironmentVariable(ScenesVar,     measuredAs);
        Environment.SetEnvironmentVariable(ContinentsVar, Format(continents.ToConfig()));
        Environment.SetEnvironmentVariable(LandTargetVar, continents.TargetLand.ToString("R", CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(SurfaceVar,    Format(root.BuildSurface()));
        SessionState.SetBool(RunningKey, true);

        string asset = AssetDatabase.GetAssetPath(continents);
        _run = new Collector(scene, measuredAs, asset, tools.Length);
        var api = ScriptableObject.CreateInstance<TestRunnerApi>();
        _run.Api = api;
        api.RegisterCallbacks(_run);
        Debug.Log($"[Measure] {scene}{(measuredAs != scene ? $" as {measuredAs}" : "")} with {asset}: {tools.Length} tools, " +
                  $"Burst {(BurstEnabled ? "on" : "OFF, so the jobs run managed and slowly")}.");
        _run.Guid = api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode, testNames = tools }));
    }

    // Jobs > Burst > Enable Compilation: off, the Burst scans run managed and slowly.
    static bool BurstEnabled => Unity.Burst.BurstCompiler.Options.EnableBurstCompilation;

    // The pinned scene the open one is measured as: itself, or the pinned scene whose terrain
    // settings and macro layer it copies, continent asset apart.
    static bool Resolve(string scene, TerrainSettings settings, out string measuredAs, out string why)
    {
        string path = AssetDatabase.GetAssetPath(settings);
        foreach (var p in Pinned)
        {
            if (p.Scene != scene) continue;
            measuredAs = scene;
            why = path == p.Settings ? null
                : $"{scene} runs {path}, not {p.Settings}, which StressSceneTests' copy of its terrain is held to.";
            return why == null;
        }
        foreach (var p in Pinned)
        {
            var pinned = AssetDatabase.LoadAssetAtPath<TerrainSettings>(p.Settings);
            if (pinned == null || settings.Macro == null || pinned.Macro == null) continue;
            if (Same(settings, pinned, "Macro") && Same(settings.Macro, pinned.Macro, "Continents"))
            {
                measuredAs = p.Scene;
                why = null;
                return true;
            }
        }
        measuredAs = null;
        why = $"{scene} is not a pinned continent scene, and its terrain and macro settings ({path}) are not a copy of one's. " +
              "The measuring tools measure a scene through its entry in MacroTests.StressMacros, so measure one of " +
              "the four, or a copy of one whose settings differ only in its continent asset, as ContinentSandbox is of EarthStress.";
        return false;
    }

    // Every serialized field the same, apart from Unity's own (m_*) and the one named.
    static bool Same(UnityEngine.Object a, UnityEngine.Object b, string except)
    {
        if (a.GetType() != b.GetType()) return false;
        var sa = new SerializedObject(a);
        var sb = new SerializedObject(b);
        var it = sa.GetIterator();
        for (bool enter = true; it.Next(enter); enter = false)
        {
            if (it.name.StartsWith("m_", StringComparison.Ordinal) || it.name == except) continue;
            var other = sb.FindProperty(it.propertyPath);
            if (other == null || !SerializedProperty.DataEquals(it, other)) return false;
        }
        return true;
    }

    static void ClearVariables()
    {
        foreach (string v in Variables) Environment.SetEnvironmentVariable(v, null);
    }

    // A struct's public fields as Measuring.Parse reads them: "Name=value;...", round-trip exact.
    static string Format<T>(T value) where T : struct
    {
        var parts = new List<string>();
        foreach (var f in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object v = f.GetValue(value);
            string s = v switch
            {
                float x  => x.ToString("R", CultureInfo.InvariantCulture),
                double x => x.ToString("R", CultureInfo.InvariantCulture),
                IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString(),
            };
            parts.Add(f.Name + "=" + s);
        }
        return string.Join(";", parts);
    }

    // One run: the progress bar between tools, each tool's output, and the summary at the end.
    sealed class Collector : ICallbacks
    {
        readonly string _scene, _measuredAs, _asset;
        readonly int    _tools;
        readonly List<ITestResultAdaptor> _results = new List<ITestResultAdaptor>();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        public TestRunnerApi Api;
        public string        Guid;
        bool _cancelled;

        public Collector(string scene, string measuredAs, string asset, int tools)
        {
            _scene = scene; _measuredAs = measuredAs; _asset = asset; _tools = tools;
        }

        bool Mine => _run == this;

        public void RunStarted(ITestAdaptor testsToRun) { }

        public void TestStarted(ITestAdaptor test)
        {
            if (!Mine || test.IsSuite) return;
            if (EditorUtility.DisplayCancelableProgressBar($"Measuring {_scene}", $"{test.Name} ({_results.Count + 1} of {_tools})",
                                                           (float)_results.Count / _tools))
                Cancel();
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            if (!Mine || result.Test.IsSuite) return;
            _results.Add(result);
            Debug.Log($"[Measure] {result.Name}: {result.ResultState} in {result.Duration:F1} s");
            if (Environment.GetEnvironmentVariable(CancelledVar) == "1") Cancel();
        }

        void Cancel()
        {
            if (_cancelled) return;
            _cancelled = true;
            Environment.SetEnvironmentVariable(CancelledVar, "1");
            TestRunnerApi.CancelTestRun(Guid);
        }

        public void RunFinished(ITestResultAdaptor run)
        {
            // The framework went on calling a run's callbacks in later runs after they were
            // unregistered, the whole suite included; a run answers only to its own events.
            if (!Mine) { TestRunnerApi.UnregisterTestCallback(this); return; }
            try { Report(); }
            finally
            {
                EditorUtility.ClearProgressBar();
                ClearVariables();
                SessionState.SetBool(RunningKey, false);
                if (Api != null) { Api.UnregisterCallbacks(this); UnityEngine.Object.DestroyImmediate(Api); }
                _run = null;
            }
        }

        void Report()
        {
            var text = new StringBuilder();
            text.AppendLine($"Measure Open Scene: {_scene}" + (_measuredAs != _scene ? $", measured as {_measuredAs}" : "") +
                            $", continents from {_asset}");
            text.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm}, Unity {Application.unityVersion}, Burst {(BurstEnabled ? "on" : "off")}, " +
                            $"{_clock.Elapsed.TotalSeconds:F0} s{(_cancelled ? ", CANCELLED" : "")}");
            text.AppendLine();

            var pins = new List<string[]>();
            foreach (var r in _results)
            {
                text.AppendLine($"== {r.Name}: {r.ResultState} in {r.Duration:F1} s");
                if (!string.IsNullOrEmpty(r.Message)) text.AppendLine(r.Message.TrimEnd());
                foreach (string line in (r.Output ?? "").Split('\n'))
                {
                    string l = line.TrimEnd('\r');
                    if (l.StartsWith("PIN|", StringComparison.Ordinal)) pins.Add(l.Split('|'));
                    else if (l.Length > 0) text.AppendLine(l);
                }
                text.AppendLine();
            }

            string summary = Summary(pins);
            text.AppendLine("== What to pin");
            text.Append(summary);

            string dir  = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "Measurements");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, _scene + ".txt");
            File.WriteAllText(file, text.ToString());

            int failed = _results.FindAll(r => r.TestStatus == TestStatus.Failed).Count;
            string head = $"[Measure] {_scene}{(_measuredAs != _scene ? $" as {_measuredAs}" : "")}: {_results.Count} of {_tools} tools " +
                          $"in {_clock.Elapsed.TotalSeconds:F0} s{(_cancelled ? ", cancelled" : "")}{(failed > 0 ? $", {failed} FAILED" : "")}; " +
                          $"written to Logs/Measurements/{_scene}.txt";
            string body = head + "\n" + summary;
            if (failed > 0 || _cancelled) Debug.LogWarning(body);
            else Debug.Log(body);
        }

        // Each pinned constant: the value to pin, the one pinned now, and what to do about it.
        static string Summary(List<string[]> pins)
        {
            if (pins.Count == 0) return "  nothing measured\n";
            var s = new StringBuilder();
            foreach (var p in pins)
            {
                if (p.Length < 8) continue;
                string scene = p[1], name = p[2], kind = p[3], value = p[4], current = p[5], budget = p[6], note = p[7];
                s.AppendLine($"  {scene} {name}: {value}, pinned {current}  -- {Verdict(kind, value, current, budget)}" +
                             (note.Length > 0 ? $" ({note})" : ""));
            }
            return s.ToString();
        }

        static string Verdict(string kind, string value, string current, string budget)
        {
            var ci = CultureInfo.InvariantCulture;
            if (!double.TryParse(value, NumberStyles.Float, ci, out double v) ||
                !double.TryParse(current, NumberStyles.Float, ci, out double c))
                return "unreadable";
            switch (kind)
            {
                case "exact":
                    return v == c ? "unchanged" : "RE-PIN: the tests hold it to the bit";
                case "bound":
                    return v == c ? "unchanged" : v > c ? "RE-PIN: the measurement is above the pin" : "may lower: the pin still covers it";
                case "count":
                {
                    string moved = Math.Abs(v - c) > c / 100.0 ? $"RE-PIN: moved {(v - c) / c:P1}, past the tests' 1%" : "within the tests' 1%";
                    if (!double.TryParse(budget, NumberStyles.Float, ci, out double b) || b <= 0.0) return moved;
                    double over = b / v;
                    string room = v >= b ? "OVER BUDGET" : over < 1.1 ? $"budget only {over - 1.0:P0} over it" :
                                  over > 1.2 ? $"budget {over - 1.0:P0} over it, past the 20% the cylinders' tests allow" :
                                  $"budget {b:F0} is {over - 1.0:P0} over it";
                    return moved + "; " + room;
                }
                default:
                    return kind;
            }
        }
    }
}
