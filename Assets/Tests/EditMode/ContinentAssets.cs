using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Sonoma.Core.Generation;

namespace Sonoma.Tests
{
    // A ContinentSettings asset read as text, so the pure tests take each stress scene's continents
    // from the asset itself rather than from a hand copy: a scene regenerated in the continent
    // designer is tested as it now is, with no copy to update. The project serializes assets as
    // YAML text, one "  Name: value" line per field, and Unity writes a float with enough digits to
    // round-trip, so the text gives the asset's values to the bit. Pure, like the tests it serves:
    // no AssetDatabase, so it runs out of the Editor too. MacroMapsTests.StressScenesRunTheirMacroLayers
    // holds it to Unity's own reading, ContinentSettings.ToConfig, field for field.
    internal static class ContinentAssets
    {
        // Where the project is: SONOMA_PROJECT if set, for a harness run from elsewhere; otherwise
        // the first folder holding Assets and ProjectSettings above the working directory (the
        // Editor's is the project) or above this assembly.
        internal const string ProjectVar = "SONOMA_PROJECT";

        internal static string ProjectRoot
        {
            get
            {
                string set = Environment.GetEnvironmentVariable(ProjectVar);
                if (!string.IsNullOrWhiteSpace(set)) return set;
                foreach (string start in new[] { Directory.GetCurrentDirectory(), Path.GetDirectoryName(typeof(ContinentAssets).Assembly.Location) })
                    for (var dir = start == null ? null : new DirectoryInfo(start); dir != null; dir = dir.Parent)
                        if (Directory.Exists(Path.Combine(dir.FullName, "Assets")) && Directory.Exists(Path.Combine(dir.FullName, "ProjectSettings")))
                            return dir.FullName;
                Assert.Fail($"ContinentAssets: cannot find the project; set {ProjectVar} to its folder");
                return null;
            }
        }

        internal static string FullPath(string assetPath) => Path.Combine(ProjectRoot, assetPath);

        internal static DateTime Stamp(string assetPath) => File.GetLastWriteTimeUtc(FullPath(assetPath));

        // The runtime configuration the asset gives (ToConfig's fields, which share ContinentConfig's
        // names) and its TargetLand, the final land share the tests hold the scene to. Every field
        // must be in the file: an asset written before a field existed leaves it out, and Unity fills
        // it from the C# initializer, which this cannot see. Re-saving the asset writes it.
        internal static ContinentConfig Read(string assetPath, out float targetLand)
        {
            string path = FullPath(assetPath);
            Assert.IsTrue(File.Exists(path), $"{assetPath} does not exist");
            var fields = Fields(File.ReadAllLines(path));

            object boxed = default(ContinentConfig);
            foreach (var f in typeof(ContinentConfig).GetFields(BindingFlags.Public | BindingFlags.Instance))
                f.SetValue(boxed, Convert.ChangeType(Field(fields, assetPath, f.Name), f.FieldType, CultureInfo.InvariantCulture));
            targetLand = float.Parse(Field(fields, assetPath, "TargetLand"), CultureInfo.InvariantCulture);
            return (ContinentConfig)boxed;
        }

        static string Field(Dictionary<string, string> fields, string assetPath, string name)
        {
            if (!fields.TryGetValue(name, out string value))
                Assert.Fail($"{assetPath} has no {name} line, which an asset written before the field existed leaves out: " +
                            "re-save it so Unity writes every field (AssetDatabase.ForceReserializeAssets)");
            return value;
        }

        // The MonoBehaviour's own fields: the lines indented two spaces under "MonoBehaviour:".
        static Dictionary<string, string> Fields(string[] lines)
        {
            var fields = new Dictionary<string, string>();
            bool inside = false;
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd('\r');
                if (line == "MonoBehaviour:") { inside = true; continue; }
                if (!inside) continue;
                if (line.Length < 3 || line[0] != ' ' || line[1] != ' ' || line[2] == ' ') continue;
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                fields[line.Substring(2, colon - 2)] = line.Substring(colon + 1).Trim();
            }
            return fields;
        }
    }

    // Each test run reads the continent assets afresh. The Editor keeps the test assembly loaded
    // between runs, and an asset regenerated between them changes no code, so without this a
    // second run would test the asset as the first one read it, and the builds cached from it.
    [SetUpFixture]
    public class ContinentAssetsAfresh
    {
        [OneTimeSetUp]
        public void ReadThemAgain() => MacroTests.ReloadContinentAssets();
    }
}
