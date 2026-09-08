using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Sonoma.Tests
{
    // How the terrain shader receives main-light shadows.
    //
    // A source-text guard, like GeomorphTests.ShaderMorphsInEveryPass, and for the same
    // reason: every failure guarded here is silent. A keyword the pipeline enables but the
    // shader never declares does not warn -- the shader just gets the variant with that
    // keyword off. A shadow coordinate interpolated when it must not be does not warn
    // either; it renders, and it renders wrong only in a ring a triangle or two wide.
    //
    // Editor-only: it needs Application.dataPath to find the shader.
    public class ShaderShadowTests
    {
        static string ForwardLitPass()
        {
            string path = Path.Combine(Application.dataPath, "Shaders", "SonomaTerrainTriplanar.shader");
            Assert.IsTrue(File.Exists(path), $"shader not found at {path}");

            string source = File.ReadAllText(path);
            int start = source.IndexOf("Name \"ForwardLit\"", StringComparison.Ordinal);
            Assert.Greater(start, 0, "the ForwardLit pass is missing from the shader");
            int end = source.IndexOf("ENDHLSL", start, StringComparison.Ordinal);
            Assert.Greater(end, start, "the ForwardLit pass has no HLSL block");

            return source.Substring(start, end - start);
        }

        // The bug this file was written for. Under _MAIN_LIGHT_SHADOWS_CASCADE the shadow
        // coordinate is not an interpolatable quantity: TransformWorldToShadowCoord picks a
        // cascade with ComputeCascadeIndex -- a hard step across four camera-centred split
        // spheres -- and each cascade has its own matrix into its own quadrant of the shadow
        // atlas. A triangle whose vertices straddle a split sphere therefore interpolates
        // between two unrelated atlas tiles, samples a depth belonging to neither, and fails
        // the compare. Across the whole terrain that is one thin black ring per cascade
        // boundary, concentric and following the camera, because the split spheres do.
        //
        // URP's name for "the cheap per-vertex path is valid here" is
        // REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR, and Shadows.hlsl defines it for
        // _MAIN_LIGHT_SHADOWS and _MAIN_LIGHT_SHADOWS_SCREEN only -- deliberately not for
        // the cascaded variant. Both the interpolator and its assignment must sit behind it.
        [Test]
        public void ShadowCoordIsNotInterpolatedUnderCascades()
        {
            string pass = ForwardLitPass();

            Assert.IsTrue(pass.Contains("REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR"),
                "the ForwardLit pass does not test REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR, so " +
                "it either always interpolates the shadow coordinate or never does; under " +
                "_MAIN_LIGHT_SHADOWS_CASCADE the first draws a black ring at every cascade split");

            // Every mention of the varying is inside such a guard. Counting is enough: the
            // declaration, the write in Vert and the read in Frag are three mentions, and
            // there are exactly three #if guards naming the macro.
            Assert.AreEqual(3, Occurrences(pass, "shadowCoord :") + Occurrences(pass, "o.shadowCoord")
                               + Occurrences(pass, "input.shadowCoord"),
                "the ForwardLit shadow coordinate varying is used somewhere unexpected; check " +
                "each use still sits behind REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR");
            Assert.AreEqual(3, Occurrences(pass, "#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)"),
                "there is not one REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR guard per use of the " +
                "shadow coordinate varying");

            // ...and the fallback for when it is not defined actually computes one.
            Assert.IsTrue(pass.Contains("TransformWorldToShadowCoord(input.positionWS)"),
                "nothing computes the shadow coordinate per fragment, so the cascaded variants " +
                "receive no shadow coordinate at all");
        }

        // GetMainLight(shadowCoord) alone skips GetMainLightShadowFade, which is what fades
        // the shadow out over the cascade border and to nothing at the shadow distance.
        // Without it shadows stop at a hard circle at _ShadowDistance -- another
        // camera-following ring, on top of the cascade ones. Only the three-argument
        // overload applies it.
        [Test]
        public void MainLightShadowsAreFadedAtTheShadowDistance()
        {
            string pass = ForwardLitPass();

            Assert.IsTrue(pass.Contains("GetMainLight(shadowCoord, input.positionWS"),
                "the ForwardLit pass does not pass positionWS to GetMainLight, so " +
                "GetMainLightShadowFade never runs and shadows end at a hard circle at the " +
                "shadow distance");
        }

        // URP 17 (Unity 6) replaced the single _SHADOWS_SOFT with a quality tier, and the
        // pipeline enables whichever tier the URP asset asks for. A shader that declares
        // only the old keyword silently gets the no-soft-shadows variant instead -- single
        // tap, maximum acne, no warning, and no visible relationship to the Soft Shadows
        // setting the user just changed. Kept in step with URP's own Lit.shader.
        [Test]
        public void ShadowKeywordsMatchThePipeline()
        {
            string pass = ForwardLitPass();

            foreach (string keyword in new[]
                     {
                         "_MAIN_LIGHT_SHADOWS", "_MAIN_LIGHT_SHADOWS_CASCADE", "_MAIN_LIGHT_SHADOWS_SCREEN",
                         "_SHADOWS_SOFT", "_SHADOWS_SOFT_LOW", "_SHADOWS_SOFT_MEDIUM", "_SHADOWS_SOFT_HIGH",
                     })
            {
                Assert.IsTrue(pass.Contains(keyword),
                    $"the ForwardLit pass never declares {keyword}, so the pipeline enabling it " +
                    "silently selects the variant with it off");
            }
        }

        static int Occurrences(string haystack, string needle)
        {
            int n = 0;
            for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                n++;
            }
            return n;
        }
    }
}
