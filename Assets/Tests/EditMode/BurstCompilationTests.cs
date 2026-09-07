using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // Whether the generation jobs are actually Burst-compiled, as opposed to compiling and
    // then running managed.
    //
    // This is the one check in the suite that cannot be answered by reading the code. Burst
    // reports an unsupported construct inside a [BurstCompile] job as a BC#### console
    // error, but says nothing whatsoever when a job simply never reaches it -- a forgotten
    // attribute, a disabled Editor toggle, an assembly outside its compilation set. The
    // result is correct terrain at a fraction of the speed, which no other test would catch.
    //
    // Unlike the rest of the suite this needs a real Burst backend, so it only means
    // anything inside the Editor's Test Runner; the out-of-Editor harness cannot run it.
    public class BurstCompilationTests
    {
        [Test]
        public void JobsCompileWithoutManagedFallback()
        {
            // Ignored rather than failed: Burst switched off is a deliberate Editor state
            // (usually for debugging a job), not a defect in the code under test.
            if (!BurstCompiler.Options.EnableBurstCompilation)
                Assert.Ignore("Burst compilation is disabled in this Editor. Re-enable it at " +
                              "Jobs > Burst > Enable Compilation to check for a managed fallback.");

            var s     = SurfaceDef.CubeSphere(6371000.0);
            var roots = SurfaceMath.BuildRoots(s);
            var p     = HeightParams.Create(s, 33, 0.0, 20, 200f, 0.5f, 2f, 42u);

            var compiled = new NativeArray<int>(1, Allocator.TempJob);
            var sample   = new NativeArray<float>(1, Allocator.TempJob);

            try
            {
                new BurstWitnessJob
                {
                    Surface  = s,
                    Root     = roots[0],
                    Node     = new NodeId(0, 0, 0, 0),
                    Params   = p,
                    Compiled = compiled,
                    Sample   = sample,
                }.Schedule().Complete();

                // BurstWitnessJob lives in Sonoma.Core alongside the real jobs, so this is a
                // statement about the assembly they are in, not about the test assembly.
                Assert.AreEqual(1, compiled[0],
                    "BurstWitnessJob ran as managed code. Sonoma.Core is not being Burst-compiled, " +
                    "so HeightSampleJob, CoarseHeightSampleJob and ChunkMeshJob are not either. " +
                    "Check the console for BC#### errors, and check Jobs > Burst > Open Inspector " +
                    "lists the three jobs by name.");

                // The leaf call graph ran at all. A NaN here would mean the Burst-compiled
                // path disagrees with the managed one, which is a different and worse problem.
                Assert.IsFalse(float.IsNaN(sample[0]), "the Burst-compiled height sample was NaN");
            }
            finally
            {
                compiled.Dispose();
                sample.Dispose();
            }
        }
    }
}
