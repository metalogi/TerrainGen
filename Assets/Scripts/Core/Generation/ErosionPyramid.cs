using System;
using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // The drainage maps' levels below the top, and the correction folded into the base height
    // (Docs/M5_MacroLayer_Plan.md, M5c, E4). Eroding only the top level would leave the planet seen
    // from orbit uneroded, so every level below is derived from it:
    //
    //   1. Both drainage channels, wetness and D, go through MacroPyramid in its Fill mode: the
    //      pass wrote them only inside the quads (ErosionChannels.WriteTop), so the top level's
    //      border comes from the neighbours, and each level below is the one above filtered by the
    //      binomial and taken at the coincident texels, then haloed and edge-copied. The filter's
    //      weights are positive, so wetness stays in 0..1 and D at or below zero wherever it was.
    //   2. D is added into the base height channel at every texel of every level, borders
    //      included: BaseHeight' = BaseHeight + D (E1). Both sides of a shared edge store the same
    //      base height and the same D, so they store the same sum, and C2 holds for the folded
    //      channel as it did for its two parts.
    //
    // A channel is one component of the float2 drainage array, seen through a view, so the pyramid
    // is MacroPyramid's own code. Pure and UnityEngine-free; ErosionPyramidJob runs the same
    // functions in Burst.
    public static class ErosionPyramid
    {
        // One component of a float2 array as a macro channel.
        public struct Component<TA> : IMacroScalars where TA : struct, IErosionArray<float2>
        {
            TA        _a;
            readonly int _c;
            public Component(TA a, int component) { _a = a; _c = component; }
            public float Get(int index) => _a.Get(index)[_c];
            public void  Set(int index, float value)
            {
                float2 v = _a.Get(index);
                v[_c] = value;
                _a.Set(index, v);
            }
        }

        // The folded base height: one float addition, the same in both builds.
        public static float Fold(float baseHeight, float correction) => baseHeight + correction;

        // Steps 1 and 2 over any arrays: the drainage levels, then the fold.
        public static void Run<TA, TT, P>(TA drainage, TT texels, int texelCount, in MacroLayout layout,
                                          P halo, P copies, P ranges)
            where TA : struct, IErosionArray<float2>
            where TT : struct, IErosionArray<float4>
            where P  : struct, IMacroPairs
        {
            for (int c = 0; c < 2; c++)
            {
                var ch = new Component<TA>(drainage, c);
                MacroPyramid.Run(ref ch, ref ch, layout, halo, copies, ranges, MacroPyramidTop.Fill);
            }
            for (int t = 0; t < texelCount; t++)
            {
                float4 x = texels.Get(t);
                x.x = Fold(x.x, drainage.Get(t).y);
                texels.Set(t, x);
            }
        }

        // The managed reference, in place on both arrays.
        public static void Run(float4[] texels, float2[] drainage, in MacroLayout layout, MacroPyramidPlan plan)
        {
            if (texels == null || drainage == null) throw new ArgumentNullException(texels == null ? nameof(texels) : nameof(drainage));
            if (texels.Length != drainage.Length)
                throw new ArgumentException("ErosionPyramid: one drainage texel per macro texel.", nameof(drainage));
            MacroPyramid.Validate(drainage.Length, layout, plan);
            Run(new ErosionChannels.Float2s(drainage), new Float4s(texels), texels.Length, layout,
                new MacroPyramid.ArrayPairs(plan.Halo), new MacroPyramid.ArrayPairs(plan.Copies),
                new MacroPyramid.ArrayPairs(plan.Ranges));
        }


        public readonly struct Float4s : IErosionArray<float4>
        {
            readonly float4[] _a;
            public Float4s(float4[] a) { _a = a; }
            public float4 Get(int index)               => _a[index];
            public void   Set(int index, float4 value) => _a[index] = value;
        }
    }
}
