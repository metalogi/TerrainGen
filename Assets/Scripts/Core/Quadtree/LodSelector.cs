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

        // Double-buffered so a visibility change is a set difference rather than a walk over
        // every chunk. Swapped by reference at the end of each frame; never reallocated.
        HashSet<NodeId> _visibleNow  = new HashSet<NodeId>();
        HashSet<NodeId> _visiblePrev = new HashSet<NodeId>();

        double3 _camera;
        bool    _budgetWarned;

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
                Preload(node, depth);
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
        // to wait for. PreloadDistance(depth+1) is 0.75 of the parent's own split distance
        // at the default factors, which is roughly one node-width of warning.
        void Preload(NodeId node, int depth)
        {
            if (depth >= _lod.MaxDepth) return;

            double preload = _lod.PreloadDistance(depth + 1);
            for (int i = 0; i < 4; i++)
            {
                var    child = node.Child(i);
                double dist  = NodeDistance(child);
                if (dist < preload) Want(child, dist);
            }
        }

        void Want(NodeId node, double distance)
        {
            _wanted.Add(node);
            if (_nodes.ContainsKey(node)) return;

            _nodes[node] = default;
            // distance / node size, so near-and-coarse outranks far-and-fine -- which is the
            // ordering the scheduler's heap exists to serve. Nominal size, like every other
            // depth-derived quantity here.
            _scheduler.Enqueue(node, distance / _lod.NominalSize(node.Depth));
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
            if (_maxResident <= 0 || ResidentCount <= _maxResident) return;

            while (ResidentCount > _maxResident && TryFindFurthestCollapsible(out NodeId parent))
            {
                Collapse(parent);
                ResidentCount -= 4;
            }

            // Everything left is either a leaf the camera needs or a parent still holding
            // split children. Saying so once is more useful than silently rebuilding and
            // re-evicting the same chunks every frame, which is what a budget below the
            // configuration's working set actually produces.
            if (ResidentCount > _maxResident && !_budgetWarned)
            {
                _budgetWarned = true;
                Debug.LogWarning(
                    $"[LodSelector] {ResidentCount} chunks resident against a budget of {_maxResident}, " +
                    "and no further sibling group can be collapsed. The LOD configuration wants more " +
                    "chunks than the budget allows: raise MaxResidentChunks, or lower MaxDepth or " +
                    "SplitFactor. Until then chunks will be built and evicted repeatedly.");
            }
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
        bool TryFindFurthestCollapsible(out NodeId parent)
        {
            parent = default;
            double furthest = -1.0;
            bool   found    = false;

            foreach (var kv in _nodes)
            {
                if (kv.Value.Chunk == null) continue;      // the parent must be able to take over
                if (!IsCollapsible(kv.Key)) continue;

                double d = NodeDistance(kv.Key);
                if (d <= furthest) continue;

                furthest = d;
                parent   = kv.Key;
                found    = true;
            }
            return found;
        }

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
        }

        // Bounding-sphere distance, clamped at zero inside the sphere.
        //
        // Measured NodeWorldSize here, not nominal: this is the distance, and it should
        // follow the node's real geometry. Only the *threshold* it is compared against has
        // to be depth-only (LodMath.NominalSize).
        double NodeDistance(NodeId node)
        {
            ref readonly RootQuad root = ref _roots[node.Quad];

            double3 centre = SurfaceMath.SurfacePoint(_surface, root,
                                 0.5 * (node.UMin + node.UMax), 0.5 * (node.VMin + node.VMax));

            // Half the longer diagonal is the circumradius of the node's surface patch.
            double radius = 0.5 * SurfaceMath.NodeWorldSize(_surface, root, node);

            // Terrain displaces along the normal both ways, so the bound is the larger
            // magnitude rather than MaxHeight alone: a node whose terrain sits entirely
            // below the base surface has a negative MaxHeight and would shrink its sphere.
            if (_nodes.TryGetValue(node, out var state))
                radius += math.max(math.abs(state.MinHeight), math.abs(state.MaxHeight));

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
