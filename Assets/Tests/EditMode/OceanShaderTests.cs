using System.Globalization;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using UnityEditor;
using UnityEngine;

namespace Sonoma.Tests
{
    // The ocean shader and material (Docs/Ocean_A_Plan.md, Step 5), from the source and the asset.
    // The morph in every pass and the shadow reception are checked with the terrain's, in
    // GeomorphTests.ShaderMorphsInEveryPass and ShaderShadowTests, which run over both shaders.
    //
    // Editor-only: it needs Application.dataPath and AssetDatabase.
    public class OceanShaderTests
    {
        const string Shader   = "SonomaOcean.shader";
        const string Material = "Assets/Materials/Ocean.mat";

        // C5: exactly ForwardLit, DepthOnly and DepthNormals. A ShadowCaster would draw the sea's
        // flat surface into the shadow map over terrain it hides anyway, and is the pass whose
        // camera position URP leaves stale.
        [Test]
        public void OceanHasNoShadowCaster()
        {
            string source = ShaderSource.Load(Shader);
            var names = new System.Collections.Generic.List<string>();
            foreach (Match m in Regex.Matches(source, @"Name\s+""([^""]+)""")) names.Add(m.Groups[1].Value);
            CollectionAssert.AreEqual(new[] { "ForwardLit", "DepthOnly", "DepthNormals" }, names,
                "the ocean's passes");
            StringAssert.DoesNotContain("\"ShadowCaster\"", source, "the ocean casts shadows");
        }

        // The sky on the sea as on the ground: a local shader_feature in ForwardLit, the hemisphere
        // ambient and SonomaApplyHaze, the composite the terrain uses, so the sea and the coast fade
        // alike. Ocean.mat runs the ocean shader with it on, as TerrainBiomes.mat does for the stress
        // scenes' ground. Colour comes from depth, never from the biomes.
        [Test]
        public void OceanSkyIsAForwardLitFeature()
        {
            string source = ShaderSource.Load(Shader);
            int start = source.IndexOf("Name \"ForwardLit\"", System.StringComparison.Ordinal);
            int end   = source.IndexOf("ENDHLSL", start, System.StringComparison.Ordinal);
            string lit = source.Substring(start, end - start);

            StringAssert.Contains("#pragma shader_feature_local _SONOMA_SKY", lit,
                "the sky path must be a local shader_feature, not a global or a multi_compile");
            StringAssert.Contains("SonomaApplyHaze(", lit, "the sea is not hazed by the terrain's composite");
            StringAssert.Contains("_SonomaSkyAmbient", lit, "the sea takes no sky ambient");
            StringAssert.DoesNotContain("_SONOMA_BIOMES", source, "the sea's colour is by depth, not biome");

            int cbStart = source.IndexOf("CBUFFER_START(UnityPerMaterial)", System.StringComparison.Ordinal);
            int cbEnd   = source.IndexOf("CBUFFER_END", cbStart, System.StringComparison.Ordinal);
            string cb   = source.Substring(cbStart, cbEnd - cbStart);
            foreach (string global in new[] { "_SonomaSkyAmbient", "_SonomaHaze", "_SonomaMorphRanges", "_SonomaViewPosition" })
                StringAssert.DoesNotContain(global, cb, $"{global} is in UnityPerMaterial; it is a global TerrainRoot pushes");

            var material = AssetDatabase.LoadAssetAtPath<Material>(Material);
            Assert.IsNotNull(material, $"{Material} is missing");
            Assert.AreEqual("Sonoma/Ocean", material.shader.name, $"{Material}'s shader");
            Assert.IsTrue(material.IsKeywordEnabled("_SONOMA_SKY"), $"{Material}: _SONOMA_SKY is off");
        }

        // A5 and B5: the colour by depth reads the floor depths the mesh job encodes in COLOR and
        // TEXCOORD0, morphed on the geometry's k so it does not pop at a swap, and blends shallow to
        // deep over _DepthScale. The decode is ChunkMeshLayout.DecodeFloor's, scale included.
        [Test]
        public void OceanColoursByDepth()
        {
            string source = ShaderSource.Load(Shader);
            StringAssert.IsMatch(@"float\s+k\s*=\s*SonomaMorph\(", source, "ForwardLit does not keep the morph's k");
            StringAssert.IsMatch(
                @"o\.depth\s*=\s*lerp\(SonomaDecodeFloor\(input\.floorFine\),\s*SonomaDecodeFloor\(input\.floorMorph\),\s*k\)",
                source, "the depth is not the floor morphed on k");
            StringAssert.IsMatch(@"float4\s+floorFine\s*:\s*COLOR;", source, "the fine floor is not read from COLOR");
            StringAssert.IsMatch(@"float4\s+floorMorph\s*:\s*TEXCOORD0;", source, "the morph floor is not read from TEXCOORD0");
            StringAssert.Contains($"return {ChunkMeshLayout.FloorScale.ToString("0.0", CultureInfo.InvariantCulture)} * e / (1.0 - e);", source,
                "the shader's decode is not ChunkMeshLayout.DecodeFloor's scale");
            StringAssert.IsMatch(@"lerp\(_ShallowColor\.rgb,\s*_DeepColor\.rgb,\s*1\.0 - exp\(-depth", source,
                "the colour is not shallow to deep by depth");
        }

        // The waves' include against the table that fills its globals (ocean-b): the array length
        // is OceanWaves.MaxWaves, the fade is OceanWaves.Fade's, the phase OceanWaves.ShaderPhase's,
        // and the globals are the names OceanWaveGlobals pushes, outside the per-material buffer.
        // The skirt flag spares the skirt bottoms, and the normal and glint read the slope variance.
        [Test]
        public void OceanWavesMatchTheTable()
        {
            string waves = ShaderSource.Load("SonomaWaves.hlsl");
            string F(double x) => x.ToString("0.0", CultureInfo.InvariantCulture);

            StringAssert.Contains($"#define SONOMA_MAX_WAVES {OceanWaves.MaxWaves}", waves, "the array length is not MaxWaves");
            StringAssert.Contains($"#define SONOMA_WAVE_FADE_START  {F(OceanWaves.FadeStart)}", waves, "the fade's start");
            StringAssert.Contains($"#define SONOMA_WAVE_FADE_FULL   {F(OceanWaves.FadeFull)}", waves, "the fade's end");
            StringAssert.Contains("float spacing = _SonomaWaveLod.x * exp2(-effectiveDepth);", waves,
                "the spacing is not OceanWaves.Spacing of the effective depth");
            StringAssert.Contains("smoothstep(SONOMA_WAVE_FADE_START, SONOMA_WAVE_FADE_FULL, _SonomaWaveExtra[i].x / max(spacing, 1e-20))",
                waves, "the geometric fade is not OceanWaves.Fade");
            StringAssert.Contains("return frac(dot(_SonomaWaves[i].xyz, positionWS) + _SonomaWavePhase[i]);", waves,
                "the phase is not K . p plus the pushed offset (OceanWaves.ShaderPhase)");
            StringAssert.Contains("float  keep     = 1.0 - step(0.5, skirtFlag);", waves, "the skirt bottoms are displaced");
            StringAssert.Contains("positionOS += TransformWorldToObjectDir(offset * keep, false);", waves,
                "the displacement is normalised or ignores the skirt flag");

            foreach (string global in new[]
                     { OceanWaveGlobals.WavesGlobal, OceanWaveGlobals.ExtraGlobal, OceanWaveGlobals.PhaseGlobal,
                       OceanWaveGlobals.CountGlobal, OceanWaveGlobals.LodGlobal })
                StringAssert.Contains(global, waves, $"the include does not read {global}");

            string ocean = ShaderSource.Load(Shader);
            int cbStart = ocean.IndexOf("CBUFFER_START(UnityPerMaterial)", System.StringComparison.Ordinal);
            int cbEnd   = ocean.IndexOf("CBUFFER_END", cbStart, System.StringComparison.Ordinal);
            StringAssert.DoesNotContain("_SonomaWave", ocean.Substring(cbStart, cbEnd - cbStart),
                "a wave global is in UnityPerMaterial; OceanWaveGlobals pushes them");

            int start = ocean.IndexOf("Name \"ForwardLit\"", System.StringComparison.Ordinal);
            string lit = ocean.Substring(start, ocean.IndexOf("ENDHLSL", start, System.StringComparison.Ordinal) - start);
            StringAssert.Contains("SonomaWaveNormal(input.wavePos, N, input.waveDepth, footprint", lit,
                "ForwardLit's normal is not the waves' at the undisplaced position (B3)");
            StringAssert.Contains("SonomaWaveGlintPower(", lit, "the glint does not widen by the slope variance (B3)");
        }

        // Docs/Ocean_B_Shading_Plan.md: the shading that makes the waves read. S1: under the sky
        // path the Fresnel term reflects a gradient by the reflected ray's height above the local
        // horizon, from the lit haze the far sea already fades to, up to the sky's ambient, rather
        // than one constant colour that a tilted normal cannot change.
        [Test]
        public void OceanShadingReadsTheWaves()
        {
            string source = ShaderSource.Load(Shader);
            int start = source.IndexOf("Name \"ForwardLit\"", System.StringComparison.Ordinal);
            string lit = source.Substring(start, source.IndexOf("ENDHLSL", start, System.StringComparison.Ordinal) - start);
            // From the fragment function: ShaderSource.Load splices SonomaSky.hlsl in above it, and
            // that include has an #if defined(_SONOMA_SKY) of its own.
            int frag = lit.IndexOf("half4 Frag(", System.StringComparison.Ordinal);
            int sky = lit.IndexOf("#if defined(_SONOMA_SKY)", frag, System.StringComparison.Ordinal);
            string skyPath = lit.Substring(sky, lit.IndexOf("#else", sky, System.StringComparison.Ordinal) - sky);

            StringAssert.Contains("float3 R    = reflect(-V, N);", skyPath, "S1: the sky path does not reflect the view off the wave normal");
            // The gradient is SonomaSkyLight's, which the skybox draws too, so the sea reflects the
            // sky the camera sees (AtmosphereSkyTests holds the skybox to it).
            StringAssert.IsMatch(@"half3\s+sky\s*=\s*SonomaSkyLight\(R,\s*topoUp,\s*ml\);", skyPath,
                "S1: the sea does not reflect the shared sky along the reflected ray");
            string skyLight = SkyLightBody(source);
            StringAssert.IsMatch(@"smoothstep\(0\.0,\s*max\(_SonomaAtmosphere\.z,[^)]*\),\s*dot\(dir,\s*up\)\)", skyLight,
                "S1: the reflected sky is not a gradient by the ray's height above the horizon");
            StringAssert.IsMatch(@"lerp\(SonomaHazeLight\(up,\s*ml\),\s*_SonomaSkyAmbient\.rgb,\s*rise\)", skyLight,
                "S1: the gradient does not run from the lit haze at the horizon to the sky's ambient");
            StringAssert.DoesNotContain("half3 sky = _SonomaSkyAmbient.rgb;", skyPath, "S1: the reflected sky is still a constant");

            // S2: the crest measure is the normal's own sum, the steepness-weighted cosines of the
            // waves it carries over the undamped steepnesses, and the albedo lightens by it. A zero
            // strength adds nothing.
            string waves = ShaderSource.Load("SonomaWaves.hlsl");
            StringAssert.Contains("crestSum += steep * crestWeight * c;", waves, "S2: the crest is not the waves' weighted cosines");
            StringAssert.Contains("smoothstep(SONOMA_CREST_PIXEL_START, SONOMA_CREST_PIXEL_FULL,", waves,
                "S2: the crest does not take its waves from more pixels than the normal");
            StringAssert.Contains("steepSum    += _SonomaWaves[i].w * w;", waves, "S2: the crest is not over the undamped steepnesses");
            StringAssert.Contains("crest = steepSum > 0.0 ? crestSum / steepSum : 0.0;", waves, "S2: the crest is not normalised, or not 0 with no waves");
            StringAssert.Contains("albedo = lerp(albedo, _CrestColor.rgb, _CrestStrength * crestUp);", lit,
                "S2: the albedo does not lighten on the crests by _CrestStrength");

            // S3: whitecaps where the sum pinches the surface, read off the wave normal against the
            // unit up before it is renormalised, and diffuse: they replace the reflected colour.
            StringAssert.Contains("float pinch = SonomaWaveCount() > 0 ? 1.0 - dot(waveN, N) : 0.0;", lit,
                "S3: the pinch is not the wave normal's compression of the up");
            Assert.Less(lit.IndexOf("float pinch", System.StringComparison.Ordinal),
                        lit.IndexOf("N = normalize(waveN);", System.StringComparison.Ordinal),
                        "S3: the pinch is read after N has become the wave normal");
            StringAssert.IsMatch(@"half foam = _FoamStrength \* smoothstep\(_FoamStart,[^;]*pinch\);", lit,
                "S3: the foam is not the pinch from _FoamStart to _FoamFull by _FoamStrength");
            Assert.Greater(lit.IndexOf("_FoamColor.rgb", System.StringComparison.Ordinal),
                           lit.IndexOf("color = lerp(color, sky, fresnel);", System.StringComparison.Ordinal),
                           "S3: the foam is blended before the reflection, which would then cover it");

            // The gradient's height is the atmosphere's, one number for the sky and its reflection;
            // a material property of the sea's own would be a second home for it.
            var material = AssetDatabase.LoadAssetAtPath<Material>(Material);
            Assert.IsFalse(material.HasProperty("_SkyGradientHeight"), $"{Material}: a sky gradient of the sea's own");
            Assert.Greater(material.GetFloat("_CrestStrength"), 0f, $"{Material}: the crests are off");
        }

        // SonomaSkyLight's body, from SonomaSky.hlsl as spliced into a shader's source.
        internal static string SkyLightBody(string source)
        {
            int start = source.IndexOf("half3 SonomaSkyLight(", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0, "SonomaSkyLight is not in the source");
            return source.Substring(start, source.IndexOf("\n    }", start, System.StringComparison.Ordinal) - start);
        }

        // The shallows (Docs/Ocean_B_Shading_Plan.md, amendment 8): the sea is drawn in the
        // Transparent queue and blended over the terrain where it is shallow, so the coast is a
        // gradient. The opacity is 1 - (1 - F) T, with T falling to 0 at _ShoreFadeDepth, and the
        // colour subtracts the transmitted share of the water body from the lit colour, so deep water
        // keeps its colour to the bit. Foam stays opaque. StressSceneAssetTests holds _ShoreFadeDepth
        // under every scene's overlap.
        [Test]
        public void OceanFadesInTheShallows()
        {
            string source = ShaderSource.Load(Shader);
            StringAssert.IsMatch(@"""Queue""\s*=\s*""Transparent""", source, "the sea is not drawn after the terrain");
            int start = source.IndexOf("Name \"ForwardLit\"", System.StringComparison.Ordinal);
            string lit = source.Substring(start, source.IndexOf("ENDHLSL", start, System.StringComparison.Ordinal) - start);
            StringAssert.Contains("Blend SrcAlpha OneMinusSrcAlpha", lit, "ForwardLit does not blend");
            StringAssert.Contains("half T     = 1.0 - smoothstep(0.0, max(_ShoreFadeDepth, 1e-3), depth);", lit,
                "the transmittance does not fall to 0 at _ShoreFadeDepth");
            StringAssert.Contains("half alpha = 1.0 - (1.0 - fresnel) * T;", lit, "the reflection is not kept over the shallows");
            StringAssert.Contains("color = (color - (1.0 - fresnel) * T * body) / max(alpha, 1e-4);", lit,
                "the colour is not the reflection and the remaining body over the opacity");
            StringAssert.Contains("alpha = lerp(alpha, 1.0, foam);", lit, "the foam lets the floor through");
            StringAssert.Contains("o.skirt     = input.floorFine.b;", lit, "the fragment does not know a skirt from the grid");
            // A skirt's depth is its edge's from top to bottom, so dropping it by the fade left walls
            // of water wherever the edge was deeper than the fade, seen through the shallows. It is
            // kept only in water deeper than the overlap, where an Ocean chunk draws no terrain.
            StringAssert.Contains($"if (input.skirt > 0.0 && depth < {TerrainRoot.OceanOverlapGlobal}) discard;", lit,
                "the skirts draw walls of water under the shallows' surface");
            StringAssert.Contains($"float {TerrainRoot.OceanOverlapGlobal};", source,
                "the overlap the shader reads is not the global TerrainRoot pushes");
            StringAssert.Contains("return half4(color, alpha);", lit, "ForwardLit does not return its opacity");

            var material = AssetDatabase.LoadAssetAtPath<Material>(Material);
            Assert.Greater(material.GetFloat("_ShoreFadeDepth"), 0f, $"{Material}: _ShoreFadeDepth");
            Assert.AreEqual((int)UnityEngine.Rendering.RenderQueue.Transparent, material.renderQueue,
                $"{Material}: the render queue is not Transparent");
        }

        // Every ocean pass compiles, on D3D for Windows, with the sky on and off and under every main
        // light shadow keyword, soft shadows on and off: no error, and no warning from Sonoma's own
        // files. A shader that fails to compile draws the pink error shader with nothing in the test
        // log, and the frame driver would be the first to see it.
        [Test]
        public void OceanPassesCompile()
        {
            var shader = AssetDatabase.LoadAssetAtPath<UnityEngine.Shader>("Assets/Shaders/" + Shader);
            Assert.IsNotNull(shader, "the ocean shader is missing");
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader), "the ocean shader has an error");

            var data = ShaderUtil.GetShaderData(shader);
            var sub  = data.GetSerializedSubshader(0);
            string[] mainLight = { null, "_MAIN_LIGHT_SHADOWS", "_MAIN_LIGHT_SHADOWS_CASCADE", "_MAIN_LIGHT_SHADOWS_SCREEN" };
            string[] soft      = { null, "_SHADOWS_SOFT_HIGH" };
            string[] sky       = { null, "_SONOMA_SKY" };
            int compiled = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();

            for (int p = 0; p < sub.PassCount; p++)
            {
                var pass = sub.GetPass(p);
                bool lit = pass.Name == "ForwardLit";
                foreach (string m in lit ? mainLight : new string[] { null })
                foreach (string s in lit ? sky : new string[] { null })
                foreach (var stage in new[] { UnityEditor.Rendering.ShaderType.Vertex, UnityEditor.Rendering.ShaderType.Fragment })
                foreach (string f in lit && stage == UnityEditor.Rendering.ShaderType.Fragment ? soft : new string[] { null })
                {
                    var keywords = new System.Collections.Generic.List<string>();
                    foreach (string k in new[] { m, s, f }) if (k != null) keywords.Add(k);
                    string label = $"{pass.Name} {stage} [{string.Join(" ", keywords)}]";

                    var info = pass.CompileVariant(stage, keywords.ToArray(),
                        UnityEditor.Rendering.ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64);
                    foreach (var msg in info.Messages)
                    {
                        bool ours = msg.file != null && msg.file.Contains("Sonoma");
                        Assert.IsFalse(msg.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error,
                            $"{label}: {msg.message} ({msg.file}:{msg.line})");
                        Assert.IsFalse(ours, $"{label}: a warning from our own source: {msg.message} ({msg.file}:{msg.line})");
                    }
                    Assert.IsTrue(info.Success, $"{label} did not compile");
                    compiled++;
                }
            }
            TestContext.WriteLine($"{compiled} ocean variants compiled in {watch.Elapsed.TotalSeconds:F1} s");
            Assert.AreEqual(3, sub.PassCount, "the ocean's passes");
        }
    }
}
