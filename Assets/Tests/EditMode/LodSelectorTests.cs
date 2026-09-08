using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Rendering;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The selector's policy properties, driven through the real generation pipeline rather
    // than a stand-in: chunks really are built by the Burst jobs and really are uploaded, so
    // these also exercise the async arrival order that the atomic swap exists to survive.
    //
    // Editor-only -- unlike LodMathTests, this needs Mesh, GameObject and the job system, so
    // it cannot run in the out-of-Editor harness.
    public class LodSelectorTests
    {
        // Small on purpose: a 1000 m plane tile at resolution 5 and depth 3 is 85 nodes and
        // a few thousand vertices, which the whole suite can afford to build for real.
        const double TileSize    = 1000.0;
        const int    Resolution  = 5;
        const int    MaxDepth    = 3;
        // A plane grid has no node size spread, so SplitFactor 2 stays inside
        // LodMath.MaxMorphStartFraction here even though a cube sphere would refuse it.
        const float  SplitFactor = 2f;

        SurfaceDef          _surface;
        RootQuad[]          _roots;
        GameObject          _host;
        ChunkPool           _pool;
        GenerationScheduler _scheduler;
        LodSelector         _selector;
        double3             _camera;

        void Build(int maxResidentChunks)
        {
            _surface = SurfaceDef.PlaneGrid(TileSize, 1, 1);
            _roots   = SurfaceMath.BuildRoots(_surface);

            var p   = HeightParams.Create(_surface, Resolution, 0.0, 8, 20f, 0.5f, 2f, 42u);

            // MorphStartFraction 0.12, not the 0.15 ceiling this configuration allows and not
            // the 0.14 it used to carry. At SplitFactor 2 and hysteresis 1.2 on a plane, the
            // allowance LodMath.MaxHalfRelief leaves for terrain is (0.3 - 2*frac) of the node
            // size, so 0.14 leaves 0.0200 -- against a measured worst half-relief of 0.0224 at
            // depth 3, which had every run of this fixture printing the roughness warning.
            // 0.12 leaves 0.0600, or 2.7x the terrain that actually generates.
            //
            // Safe to change without re-deriving the preload geometry below: MorphStartFraction
            // feeds only MorphRange and MaxHalfRelief. Selection is SplitFactor, NominalSize,
            // Hysteresis and PreloadFactor, none of which move.
            var lod = LodMath.Create(_surface, p, SplitFactor, 0.12f, 1.2f, 1.5f, MaxDepth);

            _host      = new GameObject("LodSelectorTestHost");
            _pool      = new ChunkPool(_host.transform, null, Resolution);
            _scheduler = new GenerationScheduler(_surface, _roots, p, _pool, 1f, 16, 100f);
            _selector  = new LodSelector(_surface, _roots, lod, _scheduler, _pool, maxResidentChunks);
            _scheduler.ChunkReady += _selector.OnChunkReady;

            // Above the middle of the tile, close enough that the tree descends to MaxDepth
            // everywhere: every node is then a wanted leaf or a split parent.
            _camera = new double3(TileSize * 0.5, 100.0, TileSize * 0.5);
        }

        [TearDown]
        public void TearDown()
        {
            if (_scheduler != null && _selector != null) _scheduler.ChunkReady -= _selector.OnChunkReady;
            _selector?.Dispose();
            _scheduler?.Dispose();
            _pool?.Dispose();
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);

            _selector = null; _scheduler = null; _pool = null; _host = null;
            ChunkMeshBuffers.DisposeCache();
        }

        // One frame: the selector decides, then the scheduler lands whatever finished. That
        // is the order TerrainRoot uses, and the order the atomic-swap guarantee depends on.
        void Frame()
        {
            _selector.Run(_camera);
            _scheduler.Update();
        }

        // Drives until the resident count has been unchanged for a while, so the assertions
        // below run against a settled tree rather than a half-generated one.
        void DriveToSteadyState(int quietFrames = 30, int maxFrames = 4000)
        {
            int last = -1, quiet = 0;
            for (int i = 0; i < maxFrames && quiet < quietFrames; i++)
            {
                Frame();
                if (_selector.ResidentCount == last) quiet++;
                else { quiet = 0; last = _selector.ResidentCount; }
            }
            Assert.AreEqual(quietFrames, quiet,
                "the tree never settled; generation is not completing in this harness");
        }

        // The atomic swap, asserted on every single frame of the fill rather than only at
        // the end: a parent hands over to its four children in one step or not at all.
        //
        // Two failure modes, and they look nothing alike in the Game view. Both visible at
        // once is z-fighting; neither visible is a hole. The coverage sum catches the second
        // one, which is the easier to miss because a hole in a distant chunk reads as sky.
        [Test]
        public void ParentStaysVisibleUntilAllFourChildrenAreResident()
        {
            Build(maxResidentChunks: 0);

            int quiet = 0, last = -1;
            for (int i = 0; i < 4000 && quiet < 30; i++)
            {
                Frame();
                AssertNoOverlapAndNoHole();
                if (_selector.ResidentCount == last) quiet++;
                else { quiet = 0; last = _selector.ResidentCount; }
            }

            Assert.AreEqual(30, quiet, "the tree never settled");
            Assert.Greater(_selector.VisibleCount, 1,
                "nothing subdivided, so the invariant was never actually exercised");
        }

        void AssertNoOverlapAndNoHole()
        {
            // No visible node may have a visible ancestor. Walking up from each visible node
            // is O(depth) and catches the parent-and-child-together case directly.
            var visible = VisibleNodes();
            foreach (var node in visible)
            {
                var n = node;
                while (n.TryGetParent(out var parent))
                {
                    Assert.IsFalse(_selector.IsVisible(parent),
                        $"{node} is visible at the same time as its ancestor {parent}");
                    n = parent;
                }
            }

            // Complete coverage: a node at depth d covers 4^-d of its root. Built by shifting
            // rather than by math.pow, so the terms are exact powers of two and a fully
            // covered root sums to exactly 1 -- no delta needed, and a genuine hole cannot
            // hide inside one.
            for (int q = 0; q < _roots.Length; q++)
            {
                double covered = 0.0;
                foreach (var node in visible)
                    if (node.Quad == q) covered += 1.0 / (1L << (2 * node.Depth));

                Assert.IsTrue(covered == 0.0 || covered == 1.0,
                    $"root {q} is {covered:F6} covered: neither empty nor a complete partition");
            }
        }

        List<NodeId> VisibleNodes()
        {
            var list = new List<NodeId>();
            foreach (var node in _selector.ResidentNodes())
                if (_selector.IsVisible(node)) list.Add(node);
            return list;
        }

        // The preload margin: a leaf approaching its split distance asks for its four children
        // before it splits, so the swap has nothing to wait for when the camera arrives.
        //
        // M3a shipped a Preload whose condition could not be satisfied -- each child's distance
        // against PreloadDistance(depth + 1), which is 0.75 of a threshold the parent had
        // already exceeded -- and nothing here noticed, because everything else about the
        // selector is correct without it. The tree still converged, the swap was still atomic,
        // coverage was still complete; the only symptom was four chunks of latency at every
        // subdivision, and no assertion was looking at latency. This is that assertion.
        [Test]
        public void ChildrenAreRequestedBeforeTheParentSplits()
        {
            Build(maxResidentChunks: 0);

            // Off the west edge and low, so distance varies strongly across the tile rather
            // than being near-constant the way it is from directly overhead. That is what puts
            // all three regimes on screen at once: of the sixteen depth-2 nodes, 5 are inside
            // SplitDistance(2) = 707 and split, 6 are leaves inside PreloadDistance(2) = 1061
            // and preload, and 5 are leaves beyond it and do not.
            //
            // The classification does not depend on the terrain that happens to be generated.
            // NodeDistance offsets each sphere to the node's mid-elevation and widens it by
            // half the node's relief, either of which could in principle move a node across a
            // threshold; measured against the real height function at this configuration, the
            // nearest node to any threshold still sits 26 m clear of it.
            _camera = new double3(-900.0, 50.0, 500.0);
            DriveToSteadyState();

            int preloaded = 0, notPreloaded = 0;
            foreach (var leaf in VisibleNodes())
            {
                // A leaf at MaxDepth has nothing to preload; Preload returns before asking.
                if (leaf.Depth >= MaxDepth) continue;

                int resident = 0;
                for (int c = 0; c < 4; c++)
                    if (_selector.IsResident(leaf.Child(c))) resident++;

                // All four or none. The swap is all-or-nothing, so a preload that requested
                // the near children of a group and not the far ones would buy nothing at all.
                Assert.IsTrue(resident == 0 || resident == 4,
                    $"{leaf} is drawing with {resident} of its 4 children resident: preload " +
                    "requests a whole sibling group or none of it");

                if (resident == 4) preloaded++; else notPreloaded++;
            }

            Assert.Greater(preloaded, 0,
                "no visible leaf had its children resident, so Preload never fired -- which is " +
                "exactly what the M3a form did, and every other test still passed");
            Assert.Greater(notPreloaded, 0,
                "every visible leaf preloaded, so the preload distance is not discriminating " +
                "and this would pass just as well with the threshold deleted");

            // And preloading must not disturb the thing it exists to speed up: the preloaded
            // children arrive hidden, so the visible set is still an exact partition.
            AssertNoOverlapAndNoHole();
        }

        // Eviction takes complete sibling groups of four. A lone eviction would leave a
        // quarter of the parent's area with nothing drawing it, which is a hole rather than
        // a coarser LOD.
        //
        // Asserted on what *leaves* the resident set, not on what is in it: chunks arrive
        // one upload at a time, so a group with three of four members present is a normal
        // intermediate state on the way in. Only the way out has to be atomic.
        [Test]
        public void EvictionTakesCompleteSiblingGroups()
        {
            // Well under the ~85 nodes this configuration wants, so the budget really binds.
            Build(maxResidentChunks: 24);

            var before   = new HashSet<NodeId>();
            var after    = new HashSet<NodeId>();
            var removed  = new List<NodeId>();
            bool sawEviction = false;

            for (int i = 0; i < 600; i++)
            {
                before.Clear();
                foreach (var n in _selector.ResidentNodes()) before.Add(n);

                Frame();

                after.Clear();
                foreach (var n in _selector.ResidentNodes()) after.Add(n);

                removed.Clear();
                foreach (var n in before) if (!after.Contains(n)) removed.Add(n);
                if (removed.Count == 0) continue;

                sawEviction = true;
                Assert.AreEqual(0, removed.Count % 4,
                    $"frame {i}: {removed.Count} chunks evicted, which is not a whole number of " +
                    "sibling groups");

                foreach (var node in removed)
                {
                    Assert.IsTrue(node.TryGetParent(out var parent),
                        $"frame {i}: a root was evicted, which can never leave anything to draw");

                    for (int c = 0; c < 4; c++)
                        Assert.IsTrue(removed.Contains(parent.Child(c)),
                            $"frame {i}: {node} was evicted without its sibling {parent.Child(c)}");

                    // Or the parent went in the same frame: collapsing a whole level can
                    // leave the level above collapsible too, and a cascade is still correct
                    // as long as some ancestor is left drawing.
                    Assert.IsTrue(after.Contains(parent) || removed.Contains(parent),
                        $"frame {i}: {node} was evicted but its parent {parent} is neither " +
                        "resident to take over nor evicted with it");
                }
            }

            Assert.IsTrue(sawEviction,
                "the budget never bound, so eviction was never exercised");
        }

        // The selector runs every frame over the whole wanted set. A per-frame allocation
        // here is a per-frame GC spike in a build, and it is the kind of thing that creeps
        // in with an innocent-looking LINQ call or a lambda capture.
        [Test]
        public void SelectorDoesNotAllocatePerFrame()
        {
            Build(maxResidentChunks: 0);
            DriveToSteadyState();

            // A few more passes so every collection has reached its final capacity; the
            // first Run after a resize would otherwise be measured as the steady state.
            for (int i = 0; i < 10; i++) _selector.Run(_camera);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++) _selector.Run(_camera);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated,
                $"LodSelector.Run allocated {allocated} bytes over 200 frames");
        }
    }
}
