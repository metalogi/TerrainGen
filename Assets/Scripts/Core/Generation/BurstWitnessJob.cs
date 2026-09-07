using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // A diagnostic, not part of generation. Nothing in the pipeline schedules it.
    //
    // It lives in this assembly on purpose. The question it answers is whether *this*
    // assembly is being Burst-compiled, so a copy in the test assembly would prove nothing
    // about the real jobs -- it would only show that the test assembly compiles.
    //
    // Burst deletes calls to [BurstDiscard] methods from compiled code, so ClearIfManaged
    // runs only in the managed fallback and Compiled[0] is 1 exactly when Burst compiled
    // this job. That is the failure mode nothing else reports: an unsupported construct
    // inside a [BurstCompile] job is a loud BC#### console error, but a missing attribute,
    // a disabled Jobs > Burst > Enable Compilation toggle, or an assembly Burst never
    // scanned all just run managed at a fraction of the speed and say nothing at all.
    //
    // Execute also calls the leaf code the real sample jobs call -- ChunkGrid.VertexUV,
    // SurfaceMath.SurfaceFrame (argument guards and all) and TerrainHeightFunction.Height
    // -- so an un-Burstable construct anywhere in that shared call graph shows up here as a
    // compile error rather than going unnoticed until someone profiles.
    [BurstCompile(CompileSynchronously = true)]
    public struct BurstWitnessJob : IJob
    {
        public SurfaceDef   Surface;
        public RootQuad     Root;
        public NodeId       Node;
        public HeightParams Params;

        [WriteOnly] public NativeArray<int>   Compiled;   // [0] = 1 Burst, 0 managed
        [WriteOnly] public NativeArray<float> Sample;     // keeps the leaf calls from being elided

        public void Execute()
        {
            bool burst = true;
            ClearIfManaged(ref burst);
            Compiled[0] = burst ? 1 : 0;

            ChunkGrid.VertexUV(Node, 1, 1, Params.Resolution, out double u, out double v);
            SurfaceMath.SurfaceFrame(Surface, Root, u, v, out double3 p, out float3 n);

            Sample[0] = TerrainHeightFunction.Height(p, Params.MaxOctave(Node.Depth),
                                                     MacroSample.Zero, Params) + n.x;
        }

        // Must return void: Burst discards the call itself, so a return value would have
        // nowhere to come from in the compiled path.
        [BurstDiscard]
        static void ClearIfManaged(ref bool burst) => burst = false;
    }
}
