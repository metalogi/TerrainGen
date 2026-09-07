using Sonoma.Core.Generation;
using Sonoma.Systems.Configuration;

namespace Sonoma.Core.Quadtree
{
    // The one part of LodMath that knows about a ScriptableObject.
    //
    // Split out of LodMath.cs so that file references nothing from UnityEngine and can be
    // compiled and run by the out-of-Editor harness (see the unity-verification note). All
    // the arithmetic worth testing lives there; this is only the field copy.
    public partial struct LodMath
    {
        public static LodMath Create(TerrainSettings t, in HeightParams p)
            => Create(p.S0, t.SplitFactor, t.MorphStartFraction, t.HysteresisFactor,
                      t.PreloadFactor, t.MaxDepth);
    }
}
