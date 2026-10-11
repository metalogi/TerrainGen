using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Sonoma.Core.CoordinateSpace;
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

        double3                   _savedOrigin;
        GameObject                _host;
        WorldOriginSystem         _origin;
        readonly List<GameObject> _made = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _savedOrigin = WorldOriginSystem.WorldOrigin;
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
