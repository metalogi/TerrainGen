using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Sonoma.Core.CoordinateSpace;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Rendering;

namespace Sonoma.Tests
{
    // The floating origin over a long session at planetary scale -- SonomaRevisedPlan.md's
    // headline M4 test.
    //
    // Editor-only: it moves real Transforms. Chunks are registered with TerrainChunk.AllChunks
    // by hand, because Awake does not run for a component added outside play mode, and are
    // root objects so that reading a position back is exact; under the pool they sit beneath
    // TerrainRoot, at identity in every scene.
    public class WorldOriginTests
    {
        const double EarthRadius = 6371000.0;

        static readonly int OriginGlobal = Shader.PropertyToID("_SonomaWorldOrigin");
        static readonly int ViewGlobal   = Shader.PropertyToID(WorldOriginSystem.ViewPositionGlobal);

        double3                   _savedOrigin;
        Vector4                   _savedOriginGlobal, _savedViewGlobal;
        GameObject                _host;
        WorldOriginSystem         _origin;
        readonly List<GameObject> _made = new List<GameObject>();

        // ShiftOrigin pushes shader globals, and they are process-wide: saved and restored with
        // the static, or a test run would leave the Editor's globals megametres out while the C#
        // side said zero. The component's own Awake never runs for one added outside play mode.
        [SetUp]
        public void SetUp()
        {
            _savedOrigin       = WorldOriginSystem.WorldOrigin;
            _savedOriginGlobal = Shader.GetGlobalVector(OriginGlobal);
            _savedViewGlobal   = Shader.GetGlobalVector(ViewGlobal);
            WorldOriginSystem.WorldOrigin = double3.zero;
            _host   = new GameObject("WorldOriginTestHost");
            _origin = _host.AddComponent<WorldOriginSystem>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _made)
            {
                if (go == null) continue;
                var chunk = go.GetComponent<TerrainChunk>();
                if (chunk != null) { TerrainChunk.AllChunks.Remove(chunk); TerrainChunk.AllActive.Remove(chunk); }
                Object.DestroyImmediate(go);
            }
            _made.Clear();
            if (_host != null) Object.DestroyImmediate(_host);
            WorldOriginSystem.WorldOrigin = _savedOrigin;
            Shader.SetGlobalVector(OriginGlobal, _savedOriginGlobal);
            Shader.SetGlobalVector(ViewGlobal,   _savedViewGlobal);
        }

        TerrainChunk MakeChunk(double3 anchor)
        {
            var go = new GameObject("Chunk", typeof(MeshFilter), typeof(MeshRenderer));
            _made.Add(go);
            var chunk = go.AddComponent<TerrainChunk>();
            TerrainChunk.AllChunks.Add(chunk);
            chunk.Anchor = anchor;
            chunk.Rebase(WorldOriginSystem.WorldOrigin);
            return chunk;
        }

        static Vector3 Expected(TerrainChunk c)
        {
            float3 local = (float3)(c.Anchor - WorldOriginSystem.WorldOrigin);
            return new Vector3(local.x, local.y, local.z);
        }

        // After each of a thousand origin shifts, every chunk sits at exactly
        // (float3)(Anchor - WorldOrigin) -- to the bit, not within a delta. The property is that
        // nothing accumulates, and a tolerance would hide precisely the drift the design exists to
        // prevent.
        //
        // Mutation check: TerrainChunk.Rebase replaced by `transform.position -= shift`, as the
        // prototype had it, fails on the first shift. A chunk megametres from the camera rounds
        // to half a metre, and rounding the difference of two roundings is not rounding the
        // difference.
        [Test]
        public void RebaseIsExactAfterAThousandShifts()
        {
            var rng    = new System.Random(20261010);
            var chunks = new List<TerrainChunk>();

            // A flight path near the surface: origin starts on it, chunks scattered over the whole
            // sphere (megametres away, where float spacing is metres) and around the start (where
            // it is sub-millimetre).
            double3 start = new double3(0.0, EarthRadius, 0.0);
            WorldOriginSystem.WorldOrigin = start;
            for (int i = 0; i < 64; i++)
                chunks.Add(MakeChunk(RandomOnSphere(rng) * EarthRadius));
            for (int i = 0; i < 64; i++)
                chunks.Add(MakeChunk(start + RandomVector(rng, 5000.0)));

            for (int shift = 0; shift < 1000; shift++)
            {
                // Mostly what the camera does -- a rebase as it crosses RebaseThreshold -- with an
                // occasional teleport, which is what a fast descent from orbit looks like per frame.
                double reach = rng.NextDouble() < 0.05 ? 2.0e6 : 1500.0;
                var d = (float3)RandomVector(rng, reach);
                _origin.ShiftOrigin(new Vector3(d.x, d.y, d.z));

                foreach (var c in chunks)
                {
                    Vector3 want = Expected(c), got = c.transform.position;
                    if (want.x != got.x || want.y != got.y || want.z != got.z)
                        Assert.Fail($"after shift {shift + 1}, chunk anchored at {c.Anchor} sits at " +
                                    $"{got.x:R}, {got.y:R}, {got.z:R} instead of {want.x:R}, {want.y:R}, {want.z:R}");
                }
            }
        }

        // A tracked transform -- a rig, a character -- keeps its place in the world across a
        // rebase: the same absolute position, and the same offset from the terrain beside it. It
        // is shifted by the delta rather than recomputed, having no double anchor, so this holds
        // to float precision near the camera, not to the bit.
        [Test]
        public void TrackedTransformsMoveWithTheOrigin()
        {
            WorldOriginSystem.WorldOrigin = new double3(0.0, EarthRadius, 0.0);

            var rig = new GameObject("Rig");
            _made.Add(rig);
            rig.transform.position = new Vector3(812.5f, 3.25f, -440.75f);
            _origin.Tracked.Add(rig.transform);
            _origin.Tracked.Add(null);   // a destroyed entry is skipped, not dereferenced

            var ground = MakeChunk(WorldOriginSystem.WorldOrigin + new double3(800.0, 0.0, -450.0));

            double3 absBefore    = Absolute(rig.transform.position);
            Vector3 offsetBefore = rig.transform.position - ground.transform.position;

            var shifts = new[] { new Vector3(1200f, 0f, 0f), new Vector3(-900f, 35f, 640f), new Vector3(0f, 0f, -1800f) };
            foreach (var s in shifts)
            {
                Vector3 before = rig.transform.position;
                _origin.ShiftOrigin(s);

                Assert.AreEqual(before - s, rig.transform.position, "a tracked transform moves by the delta");
                Assert.AreEqual(0.0, math.distance(absBefore, Absolute(rig.transform.position)), 1e-3,
                    "the tracked transform moved in the world");
                Assert.AreEqual(0f, (rig.transform.position - ground.transform.position - offsetBefore).magnitude, 1e-3f,
                    "the tracked transform moved relative to the terrain beside it");
            }
        }

        // TerrainRoot pushes the view position the morph measures from in Update; the rebase lands
        // in LateUpdate, before rendering. So the shift has to carry that position with the
        // world, or every rebase frame morphs against a point a whole threshold away from the
        // camera -- the near field popping to its parents' geometry once per rebase. It must be
        // the same absolute point after the shift as before, which is what keeps it in step with
        // the position LodSelector selected from that frame.
        [Test]
        public void ShiftCarriesTheViewPositionWithIt()
        {
            WorldOriginSystem.WorldOrigin = new double3(0.0, EarthRadius, 0.0);

            var view = new Vector3(1210.5f, -3.25f, 640.125f);
            Shader.SetGlobalVector(ViewGlobal, new Vector4(view.x, view.y, view.z, 0f));
            double3 absBefore = Absolute(view);

            var shift = view;   // what LateUpdate passes: the camera's own render position
            _origin.ShiftOrigin(shift);

            Vector4 after = Shader.GetGlobalVector(ViewGlobal);
            Assert.AreEqual(Vector3.zero, new Vector3(after.x, after.y, after.z),
                "the view position should land where the camera does, at the new origin");
            Assert.AreEqual(0.0, math.distance(absBefore, Absolute(new Vector3(after.x, after.y, after.z))), 1e-3,
                "the view position moved in the world");

            // And a view that is not the camera -- a shift by something else -- moves by the delta.
            _origin.ShiftOrigin(new Vector3(-500f, 20f, 75f));
            after = Shader.GetGlobalVector(ViewGlobal);
            Assert.AreEqual(new Vector3(500f, -20f, -75f), new Vector3(after.x, after.y, after.z));
        }

        // A fixed 1000 m threshold rebases every frame at 50 km/s, each rebase a pass over every
        // chunk. Scaled by the camera's clearance it rebases only as often as precision needs:
        // never below RebaseThreshold, RebasePerClearance x clearance above it.
        [Test]
        public void RebaseThresholdScalesWithClearance()
        {
            _origin.RebaseThreshold = 1000f;
            Assert.AreEqual(1000f, _origin.EffectiveRebaseThreshold, "unscaled by default");

            _origin.RebasePerClearance = 10f;
            _origin.ViewClearance      = 2f;
            Assert.AreEqual(1000f, _origin.EffectiveRebaseThreshold, "the fixed threshold is the floor");

            _origin.ViewClearance = 60000f;
            Assert.AreEqual(600000f, _origin.EffectiveRebaseThreshold, "ten times the clearance above it");
        }

        // The origin is zeroed at play-mode boundaries -- on entering by RuntimeInitializeOnLoad,
        // on leaving by the Editor's WorldOriginPlayModeReset -- and not when a component goes
        // away, which would move the origin under terrain still drawing. Both call this.
        [Test]
        public void ResetOriginZeroesTheOriginAndTheShaderGlobal()
        {
            _origin.ShiftOrigin(new Vector3(1.5e6f, 2.5e6f, -3.5e6f));
            Assert.AreNotEqual(double3.zero, WorldOriginSystem.WorldOrigin);

            Object.DestroyImmediate(_host);
            _host = null;
            Assert.AreNotEqual(double3.zero, WorldOriginSystem.WorldOrigin,
                "destroying the system must not move the origin");

            WorldOriginSystem.ResetOrigin();
            Assert.AreEqual(double3.zero, WorldOriginSystem.WorldOrigin);
            Assert.AreEqual(Vector4.zero, Shader.GetGlobalVector(OriginGlobal));
        }

        // ocean-b, B1, the parent plan's test 14: the waves' phase offsets are pushed in double from
        // the origin, so a rebase has to re-push them in the frame it happens, which OceanWaveGlobals
        // does on WorldOriginSystem.OriginShifted. Over a thousand real ShiftOrigins at Earth radius,
        // the pushed offsets equal OceanWaves.PhaseOffset at the new origin to the bit, at the time
        // the frame's push used; and the phase at a fixed absolute point, computed as the shader
        // will from those globals, stays within the pure test's tolerance. With the globals
        // unsubscribed, the mutation, the offsets go stale at the next shift and the phase jumps.
        [Test]
        public void RebaseCarriesTheWavePhase()
        {
            int phaseId = Shader.PropertyToID(OceanWaveGlobals.PhaseGlobal);
            int countId = Shader.PropertyToID(OceanWaveGlobals.CountGlobal);
            var waves   = OceanWaves.Build(OceanWaveTests.Default);
            var globals = new OceanWaveGlobals(waves, 0.5 * math.PI_DBL * EarthRadius / 32.0);
            try
            {
                var     rng   = new Unity.Mathematics.Random(20261117u);
                double3 fixedPoint = math.normalize(new double3(0.2, 0.3, 0.93)) * EarthRadius;
                const double time  = 4321.125;
                WorldOriginSystem.WorldOrigin = fixedPoint + new double3(700.0, -300.0, 1200.0);
                globals.Push(time);
                Assert.AreEqual(waves.Length, Shader.GetGlobalInteger(countId), "the wave count");

                double worst = 0.0;
                for (int s = 0; s < 1000; s++)
                {
                    // A camera wandering within 3 km of the point: the shift is its render position,
                    // as LateUpdate passes it, so the origin moves by a float's value.
                    double3 camera = fixedPoint + rng.NextDouble3Direction() * rng.NextDouble(0.0, 3000.0);
                    _origin.ShiftOrigin((Vector3)(float3)(camera - WorldOriginSystem.WorldOrigin));

                    worst = math.max(worst, CheckPhases(waves, phaseId, fixedPoint, time, s));
                }
                TestContext.WriteLine($"worst phase error at a fixed point over 1000 rebases: {worst:G3} cycles");
                Assert.AreEqual(time, globals.Time, "a rebase changed the wave time");
                Assert.LessOrEqual(worst, 1e-3, "the phase at a fixed point strays past float rounding of K · p");

                // The mutation: unsubscribed, the offsets stay at the old origin.
                globals.Dispose();
                double stale = 0.0;
                for (int s = 0; s < 4; s++)
                {
                    double3 camera = fixedPoint + rng.NextDouble3Direction() * rng.NextDouble(1000.0, 3000.0);
                    _origin.ShiftOrigin((Vector3)(float3)(camera - WorldOriginSystem.WorldOrigin));
                    stale = math.max(stale, PhaseError(waves, Shader.GetGlobalFloatArray(phaseId), fixedPoint, time));
                }
                TestContext.WriteLine($"unsubscribed: {stale:G3} cycles");
                Assert.Greater(stale, 0.05, "an offset left at the old origin should jump at a rebase");
            }
            finally
            {
                globals.Dispose();
                Shader.SetGlobalInteger(countId, 0);
            }
        }

        // The pushed offsets against PhaseOffset at the current origin, to the bit, and the phase
        // error at the point; returns the error.
        static double CheckPhases(Sonoma.Core.Generation.OceanWave[] waves, int phaseId, double3 point, double time, int shift)
        {
            float[] pushed = Shader.GetGlobalFloatArray(phaseId);
            Assert.IsNotNull(pushed, "no phases pushed");
            Assert.AreEqual(OceanWaves.MaxWaves, pushed.Length, "the phase array is not the shader's length");
            for (int i = 0; i < waves.Length; i++)
                Assert.AreEqual((float)OceanWaves.PhaseOffset(waves[i], WorldOriginSystem.WorldOrigin, time), pushed[i],
                    $"shift {shift}, wave {i}: the pushed offset is not the one at the new origin");
            for (int i = waves.Length; i < pushed.Length; i++)
                Assert.AreEqual(0f, pushed[i], $"unused slot {i}");
            return PhaseError(waves, pushed, point, time);
        }

        static double PhaseError(Sonoma.Core.Generation.OceanWave[] waves, float[] pushed, double3 point, double time)
        {
            float3 render = (float3)(point - WorldOriginSystem.WorldOrigin);
            double worst  = 0.0;
            for (int i = 0; i < waves.Length; i++)
            {
                double d = OceanWaves.Frac(OceanWaves.ShaderPhase(waves[i], render, pushed[i]) -
                                           OceanWaves.Phase(waves[i], point, time));
                worst = math.max(worst, math.min(d, 1.0 - d));
            }
            return worst;
        }

        static double3 Absolute(Vector3 render) =>
            WorldOriginSystem.WorldOrigin + new double3(render.x, render.y, render.z);

        static double3 RandomVector(System.Random rng, double reach) =>
            new double3(rng.NextDouble() * 2.0 - 1.0, rng.NextDouble() * 2.0 - 1.0, rng.NextDouble() * 2.0 - 1.0) * reach;

        static double3 RandomOnSphere(System.Random rng)
        {
            while (true)
            {
                double3 v = RandomVector(rng, 1.0);
                double  l = math.length(v);
                if (l > 1e-3 && l <= 1.0) return v / l;
            }
        }
    }
}
