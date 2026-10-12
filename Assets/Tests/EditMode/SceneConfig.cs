using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using Sonoma.Systems.Configuration;

namespace Sonoma.Tests
{
    // A scene's components read as text, for the tests that hold their copied constants to the
    // scene they describe. Read rather than opened, because opening a scene would replace
    // whatever scene the Editor has open.
    //
    // Editor-only: it resolves GUIDs through AssetDatabase.
    static class SceneConfig
    {
        // The serialized TerrainRoot component of Assets/Scenes/<scene>.unity.
        public static string TerrainRoot(string scene) =>
            Script(scene, "Assets/Scripts/Core/Quadtree/TerrainRoot.cs");

        // The first MonoBehaviour in the scene running the script at `scriptPath`.
        public static string Script(string scene, string scriptPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(scriptPath);
            Assert.IsNotEmpty(guid, $"{scriptPath} has moved; update the path in the test");

            foreach (string doc in Documents(scene))
                if (doc.Contains($"guid: {guid},")) return doc;
            Assert.Fail($"{scene} has nothing running {scriptPath}");
            return null;
        }

        // The scene's first Camera component (class ID 20).
        public static string Camera(string scene)
        {
            foreach (string doc in Documents(scene))
                if (doc.StartsWith("!u!20 ", StringComparison.Ordinal)) return doc;
            Assert.Fail($"{scene} has no camera");
            return null;
        }

        static string[] Documents(string scene) =>
            File.ReadAllText(Path.Combine(UnityEngine.Application.dataPath, "Scenes", scene + ".unity"))
                .Split(new[] { "\n--- " }, StringSplitOptions.None);

        // The TerrainSettings asset a TerrainRoot component points at.
        public static TerrainSettings Settings(string terrainRoot, out string path)
        {
            string guid = Field(terrainRoot, "Settings", @"\{fileID: \d+, guid: ([0-9a-f]{32}), type: \d+\}");
            path = AssetDatabase.GUIDToAssetPath(guid);
            var settings = AssetDatabase.LoadAssetAtPath<TerrainSettings>(path);
            Assert.IsNotNull(settings, "the scene's TerrainRoot has no TerrainSettings asset");
            return settings;
        }

        // One serialized field of a component, by name: the first capture group of `pattern`
        // matched against the rest of its line.
        public static string Field(string component, string name, string pattern)
        {
            var m = Regex.Match(component, $@"^\s*{name}: {pattern}\s*$", RegexOptions.Multiline);
            Assert.IsTrue(m.Success, $"the scene component has no readable {name} field");
            return m.Groups[1].Value;
        }
    }
}
