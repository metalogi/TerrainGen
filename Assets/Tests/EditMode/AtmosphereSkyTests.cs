using System.Globalization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Sonoma.Core.Quadtree;
using Sonoma.Systems.Configuration;

namespace Sonoma.Tests
{
    // The atmosphere skybox in the scenes: every scene draws AtmosphereSkybox.mat and takes no
    // ambient from it, every world has its AtmosphereSettings, and the shader repeats
    // AtmosphereParams' fade and draws the sky the sea reflects. AtmosphereTests holds the fade's
    // arithmetic, out of the Editor too.
    //
    // Editor-only: it resolves GUIDs and loads assets through AssetDatabase, reads the shader through
    // Application.dataPath, and compiles it.
    public class AtmosphereSkyTests
    {
        const string SkyboxMaterial = "Assets/Materials/AtmosphereSkybox.mat";
        const string SkyboxShader   = "SonomaSkybox.shader";

        // Every scene, its TerrainSettings and the atmosphere of its world. ContinentSandbox is a copy
        // of EarthStress and breathes Earth's air.
        static readonly (string Scene, string Settings, string Atmosphere)[] Scenes =
        {
            ("SampleScene",       "Assets/Settings/TerrainSettings.asset",            "Assets/Settings/SampleSceneAtmosphere.asset"),
            ("EarthStress",       "Assets/Settings/EarthTerrainSettings.asset",       "Assets/Settings/EarthAtmosphere.asset"),
            ("ContinentSandbox",  "Assets/Settings/SandboxTerrainSettings.asset",     "Assets/Settings/EarthAtmosphere.asset"),
            ("SmallPlanetStress", "Assets/Settings/SmallPlanetTerrainSettings.asset", "Assets/Settings/SmallPlanetAtmosphere.asset"),
            ("RingworldStress",   "Assets/Settings/RingworldTerrainSettings.asset",   "Assets/Settings/RingworldAtmosphere.asset"),
            ("NarrowRingStress",  "Assets/Settings/NarrowRingTerrainSettings.asset",  "Assets/Settings/NarrowRingAtmosphere.asset"),
            ("TerrariumStress",   "Assets/Settings/TerrariumTerrainSettings.asset",   "Assets/Settings/TerrariumAtmosphere.asset"),
        };

        // Every scene in the project is listed above, so a new one cannot ship the default skybox.
        [Test]
        public void EverySceneIsListed()
        {
            var listed = new System.Collections.Generic.HashSet<string>();
            foreach (var s in Scenes) listed.Add($"Assets/Scenes/{s.Scene}.unity");
            foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets/Scenes" }))
                Assert.IsTrue(listed.Contains(AssetDatabase.GUIDToAssetPath(guid)),
                    $"{AssetDatabase.GUIDToAssetPath(guid)} is not in AtmosphereSkyTests.Scenes");
        }

        // The skybox is AtmosphereSkybox.mat, and the ambient is not taken from it: the sky is drawn
        // from per-frame globals and is black from orbit, which no ambient probe baked from it could
        // follow. The sky path lights by TerrainSettings' ambient; SampleScene's material reads the
        // SH, so its Trilight colours are fitted to the default skybox's probe it had before.
        [Test]
        public void EverySceneDrawsTheAtmosphereSkybox()
        {
            string material = AssetDatabase.AssetPathToGUID(SkyboxMaterial);
            Assert.IsNotEmpty(material, $"{SkyboxMaterial} is missing");
            Assert.AreEqual("Sonoma/Atmosphere Skybox",
                AssetDatabase.LoadAssetAtPath<Material>(SkyboxMaterial).shader.name, $"{SkyboxMaterial}'s shader");

            foreach (var s in Scenes)
            {
                string rs = SceneConfig.RenderSettings(s.Scene);
                Assert.AreEqual(material, SceneConfig.Field(rs, "m_SkyboxMaterial",
                                    @"\{fileID: 2100000, guid: ([0-9a-f]{32}), type: 2\}"),
                                $"{s.Scene} does not draw {SkyboxMaterial}");
                int mode = int.Parse(SceneConfig.Field(rs, "m_AmbientMode", @"(\d+)"), CultureInfo.InvariantCulture);
                Assert.AreNotEqual(0, mode, $"{s.Scene} takes its ambient from the skybox");
            }
        }

        // Each scene runs its world's atmosphere, through its TerrainSettings, and each asset is one
        // AtmosphereParams accepts.
        [Test]
        public void EveryWorldHasItsAtmosphere()
        {
            foreach (var s in Scenes)
            {
                SceneConfig.Settings(SceneConfig.TerrainRoot(s.Scene), out string path);
                Assert.AreEqual(s.Settings, path, $"{s.Scene}'s TerrainRoot points at another settings asset");
                var settings = AssetDatabase.LoadAssetAtPath<TerrainSettings>(path);
                var atmosphere = AssetDatabase.LoadAssetAtPath<AtmosphereSettings>(s.Atmosphere);
                Assert.IsNotNull(atmosphere, $"{s.Atmosphere} is missing or is not an AtmosphereSettings");
                Assert.AreEqual(atmosphere, settings.Atmosphere, $"{path}: Atmosphere");
                Assert.IsTrue(atmosphere.ToParams().Enabled, $"{s.Atmosphere}");

                // The haze's colour moved here from TerrainSettings, unchanged, and the sea's
                // gradient from Ocean.mat's default, so the terrain and the sea draw what they drew.
                Assert.AreEqual(new Color(0.5f, 0.6f, 0.75f, 1f), atmosphere.HazeColor, $"{s.Atmosphere}: HazeColor");
                Assert.AreEqual(0.35f, atmosphere.SkyGradientHeight, $"{s.Atmosphere}: SkyGradientHeight");
            }

            // The terrarium's air fills it: its axis, 10 km in, is inside the atmosphere.
            var terrarium = AssetDatabase.LoadAssetAtPath<AtmosphereSettings>("Assets/Settings/TerrariumAtmosphere.asset");
            double radius = double.Parse(SceneConfig.Field(SceneConfig.TerrainRoot("TerrariumStress"), "Radius", @"(\S+)"),
                                         CultureInfo.InvariantCulture);
            Assert.GreaterOrEqual((double)terrarium.Thickness, radius, "the terrarium's axis is above its air");
            // The planets' and rings' air reaches past the haze's scale height many times, and the
            // sky is black long before orbit.
            foreach (string world in new[] { "Earth", "SmallPlanet", "Ringworld", "NarrowRing" })
            {
                var a = AssetDatabase.LoadAssetAtPath<AtmosphereSettings>($"Assets/Settings/{world}Atmosphere.asset");
                var t = AssetDatabase.LoadAssetAtPath<TerrainSettings>($"Assets/Settings/{world}TerrainSettings.asset");
                Assert.GreaterOrEqual(a.Thickness, 3f * t.HazeScaleHeight, $"{world}: the sky dims inside the haze");
                Assert.LessOrEqual(a.Thickness + a.FadeDistance, 100000f, $"{world}: the sky is not black by 100 km");
            }
        }

        // The shader's fade is AtmosphereParams' in float, and its sky is the sea's reflection.
        [Test]
        public void TheSkyboxDrawsTheSharedSky()
        {
            string source = ShaderSource.Load(SkyboxShader);
            StringAssert.Contains("#define _SONOMA_SKY", source, "the skybox does not turn the sky path on");
            StringAssert.Contains("float3 eye = _WorldSpaceCameraPos + _SonomaWorldOrigin;", source,
                "the camera's altitude is not taken from its absolute position");
            StringAssert.IsMatch(@"SonomaSkyLight\(dir,\s*SonomaUp\(eye\),\s*ml\)\s*\*\s*SonomaSkyFactor\(eye\)", source,
                "the skybox does not draw the shared sky, faded at the camera");

            int f = source.IndexOf("half SonomaSkyFactor(", System.StringComparison.Ordinal);
            string factor = source.Substring(f, source.IndexOf("\n    }", f, System.StringComparison.Ordinal) - f);
            StringAssert.Contains("_SonomaSurface.x > 1.5 && a < 0.0 ? -a : a - _SonomaAtmosphere.x", factor,
                "the fade is not AtmosphereParams.FadeDepth: outside a cylinder from the wall, else past the thickness");
            StringAssert.Contains("_SonomaAtmosphere.w * (1.0 - smoothstep(0.0, 1.0, into * _SonomaAtmosphere.y))", factor,
                "the fade is not AtmosphereParams.SkyFactor's smoothstep");

            // The sea reflects the same function the skybox draws, from the same include.
            StringAssert.Contains("half3 SonomaSkyLight(", ShaderSource.Raw("SonomaSky.hlsl"), "SonomaSkyLight is not shared");
            StringAssert.DoesNotContain("SonomaSkyLight(", ShaderSource.Raw(SkyboxShader).Replace(
                "SonomaSkyLight(dir, SonomaUp(eye), ml)", ""), "the skybox has a SonomaSkyLight of its own");
            StringAssert.Contains($"float4 {TerrainRoot.AtmosphereGlobal};", source,
                "the atmosphere the shader reads is not the global TerrainRoot pushes");
        }

        // The skybox compiles on D3D for Windows, with no warning from Sonoma's own files: a skybox that
        // fails draws the pink error shader across the whole sky.
        [Test]
        public void TheSkyboxCompiles()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/" + SkyboxShader);
            Assert.IsNotNull(shader, "the skybox shader is missing");
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader), "the skybox shader has an error");

            var pass = ShaderUtil.GetShaderData(shader).GetSerializedSubshader(0).GetPass(0);
            foreach (var stage in new[] { UnityEditor.Rendering.ShaderType.Vertex, UnityEditor.Rendering.ShaderType.Fragment })
            {
                var info = pass.CompileVariant(stage, new string[0],
                    UnityEditor.Rendering.ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64);
                foreach (var msg in info.Messages)
                {
                    Assert.IsFalse(msg.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error,
                        $"{stage}: {msg.message} ({msg.file}:{msg.line})");
                    Assert.IsFalse(msg.file != null && msg.file.Contains("Sonoma"),
                        $"{stage}: a warning from our own source: {msg.message} ({msg.file}:{msg.line})");
                }
                Assert.IsTrue(info.Success, $"{stage} did not compile");
            }
        }
    }
}
