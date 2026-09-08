using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Sonoma.Core.Rendering;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Quadtree
{
    // Decides, every frame, which nodes should be resident and which of those should be
    // drawn, then reconciles that against what actually exists.
    //
    // There is no tree of node objects. M2 deleted QuadtreeNode and it does not come back:
    // NodeId is a value type with tested equality and hashing, so a Dictionary keyed on it
    // is the tree. The branch that descends is the one containing the camera, so the wanted
    // set is O(MaxDepth) deep rather than O(4^MaxDepth) wide.
    //
    // Nothing here allocates once it is warm. It runs every frame over the whole wanted set,
    // and a per-frame allocation in this class is a per-frame GC spike in a build.
    // LodSelectorTests.SelectorDoesNotAllocatePerFrame pins that.
    public class LodSelector
    {
        public struct NodeState
        {
            public TerrainChunk Chunk;        // null while the node is queued or in flight
            public bool         HasChildren;  // last frame's split decision; feeds hysteresis
            public float        MinHeight, MaxHeight;

            // NodeDistance for this node, as of the current frame's Want. Cached rather than
            // recomputed because NodeDistance is five trigonometric surface evaluations, and
            // eviction used to call it once per resident node per evicted group. Every node
            // still in _nodes after ReleaseUnwanted was Wanted this frame, so this is always
            // current by the time the budget pass reads it.
            public double       Distance;
        }

        readonly SurfaceDef          _surface;
        readonly RootQuad[]          _roots;
        readonly LodMath             _lod;
        readonly GenerationScheduler _scheduler;
        readonly ChunkPool           _pool;
        readonly int                 _maxResident;

        readonly Dictionary<NodeId, NodeState> _nodes  = new Dictionary<NodeId, NodeState>();
        readonly HashSet<NodeId>               _wanted = new HashSet<NodeId>();

        // Nodes that are split *and* covered: all four children are resident, so the swap
        // can happen. A node that wants to split but is still waiting on a child is
        // deliberately not in here, which is what keeps it drawing itself for one more frame.
        readonly HashSet<NodeId>               _frameSplit = new HashSet<NodeId>();
        readonly List<NodeId>                  _scratch    = new List<NodeId>();

        // The sibling groups the budget pass may collapse, gathered once per over-budget
        // frame. A field rather than a local so it keeps its capacity and the frame path
        // stays allocation-free; see EvictToBudget.
        readonly List<NodeId>                  _collapsible = new List<NodeId>();

        // Double-buffered so a visibility change is a set difference rather than a walk over
        // every chunk. Swapped by reference at the end of each frame; never reallocated.
        HashSet<NodeId> _visibleNow  = new HashSet<NodeId>();
        HashSet<NodeId> _visiblePrev = new HashSet<NodeId>();

        double3 _camera;
        bool    _budgetWarned;
        bool    _reliefWarned;

        public int ResidentCount { get; private set; }
        public int VisibleCount  => _visiblePrev.Count;
        public int WantedCount   => _nodes.Count;

        public LodSelector(in SurfaceDef surface, RootQuad[] roots, in LodMath lod,
                           GenerationScheduler scheduler, ChunkPool pool, int maxResidentChunks)
        {
            _surface     = surface;
            _roots       = roots;
            _lod         = lod;
            _scheduler   = scheduler;
            _pool        = pool;
            _maxResident = maxResidentChunks;
        }

        // -- The frame -------------------------------------------------------

        public void Run(double3 cameraWorldPosition)
        {
            _camera = cameraWorldPosition;
            _wanted.Clear();
            _frameSplit.Clear();
            _visibleNow.Clear();

            for (int q = 0; q < _roots.Length; q++)
                Descend(new NodeId(q, 0, 0, 0), 0);

            ReleaseUnwanted();
            EvictToBudget();

            for (int q = 0; q < _roots.Length; q++)
                AssignVisible(new NodeId(q, 0, 0, 0));

            ApplyVisibility();
        }

        // Returns whether this node's subtree covers its own area this frame -- either the
        // node itself is resident, or all four children are (recursively) covered. The
        // caller uses that to decide whether it may hand over.
        bool Descend(NodeId node, int depth)
        {
            double dist = NodeDistance(node);
            Want(node, dist);

            var  state = _nodes[node];
            bool split = _lod.ShouldSplit(dist, depth, state.HasChildren);
            if (split != state.HasChildren)
            {
                state.HasChildren = split;
                _nodes[node] = state;
            }

            if (!split)
            {
                Preload(node, depth, dist);
                return state.Chunk != null;
            }

            // Deliberately non-short-circuiting: every child must be visited, or the three
            // after the first unready one never get requested and the swap never completes.
            bool childrenCovered = true;
            for (int i = 0; i < 4; i++)
                childrenCovered &= Descend(node.Child(i), depth + 1);

            // The atomic swap. Only when all four children are there does this node step
            // aside; until then it keeps drawing and the children stay hidden. There is
            // never a frame with both, and never a frame with a hole.
            if (childrenCovered)
            {
                _frameSplit.Add(node);
                return true;
            }

            return state.Chunk != null;
        }

        // Children are requested before the parent splits, so the swap usually has nothing
        // to wait for.
        //
        // The test is on the PARENT's own distance against its own PreloadDistance, and the
        // shape of that is not incidental. M3a shipped it the other way round -- each child's
        // distance against PreloadDistance(depth + 1) -- and it never once fired. Preload runs
        // only on a node that did *not* split, so its distance is already at or beyond
        // SplitDistance(depth); a child's bounding sphere is contained in its parent's, so
        // NodeDistance(child) >= NodeDistance(node); and PreloadDistance(depth + 1) works out
        // to PreloadFactor / 2 of SplitDistance(depth), which at the default 1.5 is 0.75 of a
        // threshold the parent has already exceeded. The condition was unsatisfiable, so every
        // subdivision in the world waited on four cold chunks and the atomic swap held the
        // parent at coarse LOD for the whole of it.
        // LodSelectorTests.ChildrenAreRequestedBeforeTheParentSplits pins the fixed form.
        //
        // All four children go together, unconditionally. The swap is atomic, so three of four
        // buys nothing; only the scheduling *priority* is per child, which is why the distance
        // is still measured individually below.
        void Preload(NodeId node, int depth, double distance)
        {
            if (depth >= _lod.MaxDepth) return;
            if (distance >= _lod.PreloadDistance(depth)) return;

            for (int i = 0; i < 4; i++)
            {
                var child = node.Child(i);
                Want(child, NodeDistance(child));
            }
        }

        void Want(NodeId node, double distance)
        {
            _wanted.Add(node);

            // distance / node size, so near-and-coarse outranks far-and-fine -- which is the
            // ordering the scheduler's heap exists to serve. Nominal size, like every other
            // depth-derived quantity here.
            double priority = distance / _lod.NominalSize(node.Depth);

            // The distance is refreshed every frame even for a node that already exists: it is
            // what the budget pass sorts on, and a stale one would evict by where the camera
            // used to be.
            if (_nodes.TryGetValue(node, out var state))
            {
                state.Distance = distance;
                _nodes[node]   = state;

                // And so is the queue priority, for a node still waiting to be built. Pricing
                // a node once, when it is first sighted, is the same staleness one step
                // earlier: the preload margin enters a node at PreloadFactor * SplitFactor
                // node sizes and by the time the camera arrives it is the node the tree is
                // waiting on, but the heap still holds the number it was requested with. It
                // then generates after every entry whose own stale priority is smaller, and
                // the atomic swap holds the parent at coarse LOD for the whole of it -- the
                // exact latency preload exists to remove, and invisible to every test here
                // because the tree still converges and coverage stays complete.
                //
                // Only while Chunk is null: a resident node needs nothing built, and skipping
                // it is also what keeps the steady-state frame free of heap traffic.
                if (state.Chunk == null) _scheduler.Enqueue(node, priority);
                return;
            }

            _nodes[node] = new NodeState { Distance = distance };
            _scheduler.Enqueue(node, priority);
        }

        // Nodes that stopped being wanted are cancelled and their chunks returned. This is
        // the other half of the orphan-chunk fix: the scheduler drops results nobody wants,
        // but only if something tells it they are no longer wanted.
        void ReleaseUnwanted()
        {
            _scratch.Clear();
            foreach (var kv in _nodes)
                if (!_wanted.Contains(kv.Key)) _scratch.Add(kv.Key);

            for (int i = 0; i < _scratch.Count; i++) Forget(_scratch[i]);
        }

        void Forget(NodeId node)
        {
            if (!_nodes.TryGetValue(node, out var state)) return;

            _scheduler.Cancel(node);
            if (state.Chunk != null) _pool.Release(state.Chunk);

            _nodes.Remove(node);
            _frameSplit.Remove(node);
            _visibleNow.Remove(node);
            _visiblePrev.Remove(node);
        }

        // -- Resident budget -------------------------------------------------

        // MaxResidentChunks counts every chunk that exists, hidden parents included -- they
        // hold a mesh and a GameObject whether or not they are drawn.
        void EvictToBudget()
        {
            ResidentCount = CountResident();
            if (_maxResident <= 0) return;

            WarnIfTheBudgetIsBelowTheWorkingSet();
            if (ResidentCount <= _maxResident) return;

            // The collapsible groups are gathered in ONE pass and then drained furthest-first.
            //
            // Re-scanning every resident node after each collapse is what made this O(n^2),
            // and each probe called NodeDistance -- five trigonometric surface evaluations --
            // so a twenty-group eviction at the shipped working set cost on the order of a
            // hundred thousand of them in a single frame, against a 2 ms budget. Distances now
            // come from NodeState, where Descend put them earlier this frame.
            //
            // Gathering once is safe because collapses cannot invalidate each other. A
            // candidate's four children are leaves (IsCollapsible requires it), so no candidate
            // is another candidate's child, and no candidate's children or grandchildren belong
            // to another candidate. The set only ever loses the entry that is taken.
            //
            // What is deliberately given up is the cascade *within* one frame: a node that
            // becomes collapsible only because its own children have just gone is picked up by
            // the next frame's pass. That costs a frame of being marginally over budget and
            // saves rebuilding the candidate set.
            _collapsible.Clear();
            foreach (var kv in _nodes)
                if (kv.Value.Chunk != null && IsCollapsible(kv.Key)) _collapsible.Add(kv.Key);

            while (ResidentCount > _maxResident && _collapsible.Count > 0)
            {
                int best = 0;
                for (int i = 1; i < _collapsible.Count; i++)
                    if (_nodes[_collapsible[i]].Distance > _nodes[_collapsible[best]].Distance)
                        best = i;

                Collapse(_collapsible[best]);
                _collapsible[best] = _collapsible[_collapsible.Count - 1];
                _collapsible.RemoveAt(_collapsible.Count - 1);

                ResidentCount -= 4;
            }
        }

        // The budget is too small when the *wanted set* does not fit in it, and that is what
        // to test -- not what eviction managed to do about it.
        //
        // _nodes is exactly the set this frame wants resident: ReleaseUnwanted has already
        // dropped everything else, and every entry left is a node Descend or Preload asked
        // for. So _nodes.Count > _maxResident says the selector is about to evict chunks it
        // will ask for again next frame: build, evict, rebuild, forever, at a full Burst job
        // and mesh upload each time.
        //
        // The previous test -- over budget *and* nothing could be collapsed -- could not fire
        // in the case it was written for. A tree deep enough to overrun its budget always has
        // a collapsible sibling group somewhere, so the collapse succeeded, the count came
        // back under budget, and both halves of the condition went false while the churn ran
        // on in silence. Only a budget below the six root quads ever reached it.
        //
        // Warn-once, and the working-set size is the number worth printing: it is what
        // MaxResidentChunks has to clear.
        void WarnIfTheBudgetIsBelowTheWorkingSet()
        {
            if (_budgetWarned || _nodes.Count <= _maxResident) return;

            _budgetWarned = true;
            Debug.LogWarning(
                $"[LodSelector] this LOD configuration wants {_nodes.Count} chunks resident from " +
                $"here ({ResidentCount} built so far) against a budget of {_maxResident}. Chunks " +
                "the camera still needs will be evicted and rebuilt every frame, at a full " +
                "generation job each. Raise MaxResidentChunks above the working set, or lower " +
                "MaxDepth, SplitFactor or PreloadFactor to shrink it.");
        }

        int CountResident()
        {
            int n = 0;
            foreach (var kv in _nodes)
                if (kv.Value.Chunk != null) n++;
            return n;
        }

        // Eviction takes complete sibling groups of four, never a lone node: evicting one
        // child leaves a quarter of the parent's area uncovered, which is a hole rather than
        // a coarser LOD. The parent is already resident, so the collapse is instant.
        bool IsCollapsible(NodeId parent)
        {
            for (int i = 0; i < 4; i++)
            {
                var child = parent.Child(i);
                if (!_nodes.TryGetValue(child, out var state) || state.Chunk == null) return false;

                // A child with descendants of its own cannot go without orphaning them.
                for (int j = 0; j < 4; j++)
                    if (_nodes.ContainsKey(child.Child(j))) return false;
            }
            return true;
        }

        void Collapse(NodeId parent)
        {
            for (int i = 0; i < 4; i++) Forget(parent.Child(i));

            var state = _nodes[parent];
            state.HasChildren = false;
            _nodes[parent] = state;
            _frameSplit.Remove(parent);   // so the visibility pass draws the parent instead
        }

        // -- Visibility ------------------------------------------------------

        void AssignVisible(NodeId node)
        {
            if (_frameSplit.Contains(node))
            {
                for (int i = 0; i < 4; i++) AssignVisible(node.Child(i));
                return;
            }

            if (_nodes.TryGetValue(node, out var state) && state.Chunk != null)
                _visibleNow.Add(node);
        }

        void ApplyVisibility()
        {
            foreach (var node in _visiblePrev)
                if (!_visibleNow.Contains(node) && _nodes.TryGetValue(node, out var s) && s.Chunk != null)
                    s.Chunk.gameObject.SetActive(false);

            foreach (var node in _visibleNow)
                if (!_visiblePrev.Contains(node) && _nodes.TryGetValue(node, out var s) && s.Chunk != null)
                    s.Chunk.gameObject.SetActive(true);

            (_visiblePrev, _visibleNow) = (_visibleNow, _visiblePrev);
        }

        // -- Bookkeeping -----------------------------------------------------

        // Chunks arrive hidden and stay that way until the next Run decides otherwise. The
        // selector runs before the scheduler each frame, so a chunk landing now is shown at
        // most one frame later -- which costs a frame of latency and buys the guarantee that
        // a child is never drawn on top of the parent it is replacing.
        public void OnChunkReady(GenerationScheduler.Result result)
        {
            if (!_nodes.TryGetValue(result.Node, out var state))
            {
                // Nothing wants it any more. The scheduler's own "still wanted" check should
                // have caught this; releasing rather than leaking is the belt to its braces.
                _pool.Release(result.Chunk);
                return;
            }

            if (state.Chunk != null && state.Chunk != result.Chunk)
                _pool.Release(state.Chunk);

            result.Chunk.gameObject.SetActive(false);
            _visiblePrev.Remove(result.Node);

            state.Chunk     = result.Chunk;
            state.MinHeight = result.MinHeight;
            state.MaxHeight = result.MaxHeight;
            _nodes[result.Node] = state;

            WarnIfTerrainIsTooRoughToCloseBoundaries(result);
        }

        // The terrain half of the LOD boundary budget, checked against terrain that exists.
        //
        // LodMath.MaxMorphStartFraction spends that budget on geometry -- node size spread and
        // hysteresis -- and Create refuses a configuration whose geometry alone overruns it.
        // Terrain draws on the same budget, because a node's bounding sphere is widened by half
        // its relief, but it cannot be checked at Create time: the relief of an fbm at a given
        // scale is a property of the noise, and an analytic bound safe across every persistence
        // runs several times the truth and would refuse worlds that are fine. So it is checked
        // here, once, against the first chunk that actually overruns.
        //
        // A warning rather than a throw for the same reason it is not in Create: the bound is a
        // worst case over a whole face, the excess is usually small, and skirts cover a good
        // deal of it. What it costs when it is exceeded is a hairline seam along LOD boundaries
        // in the roughest terrain, which is worth knowing about and is very easy to misdiagnose.
        void WarnIfTerrainIsTooRoughToCloseBoundaries(in GenerationScheduler.Result result)
        {
            if (_reliefWarned) return;

            double half    = 0.5 * (result.MaxHeight - result.MinHeight);
            double allowed = _lod.MaxHalfRelief(result.Node.Depth);
            if (half <= allowed) return;

            _reliefWarned = true;
            Debug.LogWarning(
                $"[LodSelector] terrain at depth {result.Node.Depth} has a half-relief of " +
                $"{half:F2} m where this configuration allows {allowed:F2} m " +
                $"(node size {_lod.NominalSize(result.Node.Depth):F2} m). LOD boundaries in the " +
                "roughest terrain may not close: the coarse side can begin morphing before the " +
                "fine side has finished, leaving a hairline seam that skirts will mostly, but " +
                "not always, cover. Lower MorphStartFraction, raise SplitFactor, lower " +
                "HysteresisFactor, or reduce HeightScale or MaxDepth. See LodMath.MaxHalfRelief.");
        }

        // Bounding-sphere distance, clamped at zero inside the sphere.
        //
        // Measured NodeWorldSize here, not nominal: this is the distance, and it should
        // follow the node's real geometry. Only the *threshold* it is compared against has
        // to be depth-only (LodMath.NominalSize).
        double NodeDistance(NodeId node)
        {
            ref readonly RootQuad root = ref _roots[node.Quad];

            SurfaceMath.SurfaceFrame(_surface, root,
                0.5 * (node.UMin + node.UMax), 0.5 * (node.VMin + node.VMax),
                out double3 centre, out float3 normal);

            // Half the longer diagonal is the circumradius of the node's surface patch.
            double radius = 0.5 * SurfaceMath.NodeWorldSize(_surface, root, node);

            // Terrain MOVES the sphere and widens it by its relief; it does not inflate the
            // radius by the elevation.
            //
            // Padding with max(|MinHeight|, |MaxHeight|) -- the absolute elevation -- is what
            // this did until it was measured. It bounds the geometry correctly but grows
            // without limit relative to the node, because the elevation is fixed while the
            // node halves every level: on the sample scene, a 300 m sphere with a 50 m height
            // scale, the pad reached 10.5x the node size at depth 7 and 21x at depth 8. Every
            // node within about forty metres of the camera then returned distance 0, so the
            // whole hierarchy split to MaxDepth regardless of where the camera actually was.
            //
            // Centring on the node's own mid-elevation and padding by half its relief keeps
            // the sphere proportional to the node: the same measurement gives 0.30 of the node
            // size, flat across depth and independent of radius and HeightScale. It is still
            // a containing sphere, up to the normal's divergence across the patch.
            if (_nodes.TryGetValue(node, out var state))
            {
                centre += (double3)normal * (0.5 * (state.MinHeight + state.MaxHeight));
                radius += 0.5 * (state.MaxHeight - state.MinHeight);
            }

            return math.max(0.0, math.distance(_camera, centre) - radius);
        }

        // Play-mode teardown. The pool destroys the meshes; this only drops the references,
        // so a domain reload does not find a half-populated tree.
        public void Dispose()
        {
            foreach (var kv in _nodes)
                if (kv.Value.Chunk != null) _pool.Release(kv.Value.Chunk);

            _nodes.Clear();
            _wanted.Clear();
            _frameSplit.Clear();
            _visibleNow.Clear();
            _visiblePrev.Clear();
            ResidentCount = 0;
        }

        // -- Test and debug access -------------------------------------------

        public bool TryGetState(NodeId node, out NodeState state) => _nodes.TryGetValue(node, out state);
        public bool IsVisible(NodeId node)  => _visiblePrev.Contains(node);
        public bool IsResident(NodeId node) => _nodes.TryGetValue(node, out var s) && s.Chunk != null;

        // Allocates an iterator; for tests and debug overlays only, never the frame path.
        public IEnumerable<NodeId> ResidentNodes()
        {
            foreach (var kv in _nodes)
                if (kv.Value.Chunk != null) yield return kv.Key;
        }
    }
}
