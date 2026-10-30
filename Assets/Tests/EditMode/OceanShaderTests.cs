using System.Text.RegularExpressions;
using NUnit.Framework;
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

        // A5: the colour by depth reads the floor the mesh job writes in TEXCOORD3.w, against the sea
        // level in TEXCOORD1.w, and blends shallow to deep over _DepthScale.
        [Test]
        public void OceanColoursByDepth()
        {
            string source = ShaderSource.Load(Shader);
            StringAssert.IsMatch(@"o\.depth\s*=\s*input\.uv2\.w\s*-\s*input\.morphNrm\.w", source,
                "the depth is not the sea level less the floor");
            StringAssert.IsMatch(@"lerp\(_ShallowColor\.rgb,\s*_DeepColor\.rgb,\s*1\.0 - exp\(-depth", source,
                "the colour is not shallow to deep by depth");
        }
    }
}
