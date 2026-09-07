using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Sonoma.Core.CoordinateSpace;
using Sonoma.Core.Generation;
using Sonoma.Core.Rendering;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Quadtree
{
    // Owns chunk generation: what to build, in what order, how many at once, and how much of
    // the main thread to spend landing the results.
    //
    // The prototype ran a plain FIFO queue and built whatever it dequeued. Moving the camera
    // quickly enqueued nodes that were collapsed before they generated; the collapse disposed
    // the node but left it queued, and the queue later built a chunk for a node nothing
    // referenced, registered it, and leaked the GameObject forever
    // (SonomaRevisedPlan.md section 2.4). The fix here is that "wanted" is checked twice --
    // before scheduling and again on completion -- and a result nobody wants is disposed
    // rather than shown.
    public class GenerationScheduler
    {
        public struct Request : IComparable<Request>
        {
            public NodeId Node;
            public double Priority;         // distance / node size; smaller is more urgent
            public int CompareTo(Request o) => Priority.CompareTo(o.Priority);
        }

        struct Pending
        {
            public NodeId  Node;
            public double3 Anchor;
            public JobHandle Handle;
            public Mesh.MeshDataArray MeshData;

            public NativeArray<double3> BasePoints,  CoarsePoints;
            public NativeArray<float3>  BaseNormals, CoarseNormals;
            public NativeArray<float>   Heights,     CoarseHeights;
            public NativeArray<float>   HeightBounds;

            public void DisposeBuffers()
            {
                if (BasePoints.IsCreated)    BasePoints.Dispose();
                if (BaseNormals.IsCreated)   BaseNormals.Dispose();
                if (Heights.IsCreated)       Heights.Dispose();
                if (CoarsePoints.IsCreated)  CoarsePoints.Dispose();
                if (CoarseNormals.IsCreated) CoarseNormals.Dispose();
                if (CoarseHeights.IsCreated) CoarseHeights.Dispose();
                if (HeightBounds.IsCreated)  HeightBounds.Dispose();
            }
        }

        readonly SurfaceDef   _surface;
        readonly RootQuad[]   _roots;
        readonly HeightParams _params;
        readonly ChunkPool    _pool;
        readonly int          _resolution;
        readonly float        _skirtDepth;
        readonly int          _maxInFlight;
        readonly float        _uploadBudgetMs;
        readonly bool         _flipWinding;

        readonly List<Request>                _heap     = new List<Request>();
        readonly HashSet<NodeId>              _queued   = new HashSet<NodeId>();
        readonly HashSet<NodeId>              _wanted   = new HashSet<NodeId>();
        readonly Dictionary<NodeId, Pending>  _inFlight = new Dictionary<NodeId, Pending>();
        readonly List<NodeId>                 _finished = new List<NodeId>();
        readonly Stopwatch                    _clock    = new Stopwatch();

        // What the mesh job measured, handed on with the chunk. The selector needs the
        // height bounds for its bounding sphere and would otherwise have to walk the mesh
        // again on the main thread to get numbers the job already had.
        public readonly struct Result
        {
            public readonly NodeId       Node;
            public readonly TerrainChunk Chunk;
            public readonly float        MinHeight, MaxHeight;

            public Result(NodeId node, TerrainChunk chunk, float minHeight, float maxHeight)
            {
                Node = node; Chunk = chunk; MinHeight = minHeight; MaxHeight = maxHeight;
            }
        }

        public event Action<Result> ChunkReady;

        public int QueuedCount   => _heap.Count;
        public int InFlightCount => _inFlight.Count;

        public GenerationScheduler(in SurfaceDef surface, RootQuad[] roots, in HeightParams p,
                                   ChunkPool pool, float skirtDepth, int maxInFlight, float uploadBudgetMs)
        {
            _surface        = surface;
            _roots          = roots;
            _params         = p;
            _pool           = pool;
            _resolution     = p.Resolution;
            _skirtDepth     = skirtDepth;
            _maxInFlight    = math.max(1, maxInFlight);
            _uploadBudgetMs = math.max(0.1f, uploadBudgetMs);
            _flipWinding    = SurfaceMath.UvFrameIsRightHanded(surface);
        }

        public void Enqueue(NodeId node, double priority)
        {
            _wanted.Add(node);
            if (_inFlight.ContainsKey(node) || _queued.Contains(node)) return;
            _queued.Add(node);
            HeapPush(new Request { Node = node, Priority = priority });
        }

        // Drops a node from the wanted set. Anything already in flight for it finishes on the
        // worker thread -- there is no way to interrupt a running job -- but its result is
        // thrown away rather than turned into a chunk.
        public void Cancel(NodeId node) => _wanted.Remove(node);

        public void Update()
        {
            ScheduleWork();
            CollectResults();
        }

        void ScheduleWork()
        {
            while (_inFlight.Count < _maxInFlight && _heap.Count > 0)
            {
                var req = HeapPop();
                _queued.Remove(req.Node);

                // First "still wanted?" check: nodes cancelled while queued never start.
                if (!_wanted.Contains(req.Node)) continue;
                if (_inFlight.ContainsKey(req.Node)) continue;

                _inFlight[req.Node] = Schedule(req.Node);
            }
        }

        Pending Schedule(NodeId node)
        {
            var root  = _roots[node.Quad];
            int R     = _resolution;
            int oct   = _params.MaxOctave(node.Depth);
            int coarseOct = _params.MaxOctave(node.Depth - 1);

            int samples = ChunkMeshLayout.SampleCount(R);
            int coarse  = ChunkMeshLayout.CoarseCount(R);

            var p = new Pending
            {
                Node          = node,
                Anchor        = SurfaceMath.SurfacePoint(_surface, root,
                                    0.5 * (node.UMin + node.UMax), 0.5 * (node.VMin + node.VMax)),
                BasePoints    = new NativeArray<double3>(samples, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                BaseNormals   = new NativeArray<float3>(samples,  Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                Heights       = new NativeArray<float>(samples,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                CoarsePoints  = new NativeArray<double3>(coarse,  Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                CoarseNormals = new NativeArray<float3>(coarse,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                CoarseHeights = new NativeArray<float>(coarse,    Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                HeightBounds  = new NativeArray<float>(2,         Allocator.Persistent),
                MeshData      = Mesh.AllocateWritableMeshData(1),
            };

            // Buffer parameters are set on the main thread; the job only fills the buffers.
            var data = p.MeshData[0];
            var attrs = new NativeArray<VertexAttributeDescriptor>(
                ChunkMeshBuffers.VertexAttributes(), Allocator.Temp);
            data.SetVertexBufferParams(ChunkMeshLayout.VertexCount(R), attrs);
            attrs.Dispose();
            data.SetIndexBufferParams(ChunkMeshLayout.IndexCount(R), IndexFormat.UInt16);
            data.subMeshCount = 1;

            var fine = new HeightSampleJob
            {
                Surface = _surface, Root = root, Node = node, Params = _params,
                MaxOctave = oct, Resolution = R,
                BasePoints = p.BasePoints, BaseNormals = p.BaseNormals, Heights = p.Heights,
            }.Schedule(samples, 64);

            var coarseJob = new CoarseHeightSampleJob
            {
                Surface = _surface, Root = root, Node = node, Params = _params,
                MaxOctave = coarseOct, Resolution = R,
                BasePoints = p.CoarsePoints, BaseNormals = p.CoarseNormals, Heights = p.CoarseHeights,
            }.Schedule(coarse, 64);

            var mesh = new ChunkMeshJob
            {
                Resolution = R, Anchor = p.Anchor, SkirtDepth = _skirtDepth, Depth = node.Depth,
                BasePoints = p.BasePoints, BaseNormals = p.BaseNormals, Heights = p.Heights,
                CoarseBasePoints = p.CoarsePoints, CoarseBaseNormals = p.CoarseNormals,
                CoarseHeights = p.CoarseHeights,
                Mesh = data, HeightBounds = p.HeightBounds,
            }.Schedule(JobHandle.CombineDependencies(fine, coarseJob));

            p.Handle = mesh;
            JobHandle.ScheduleBatchedJobs();
            return p;
        }

        void CollectResults()
        {
            _clock.Restart();
            _finished.Clear();

            foreach (var kv in _inFlight)
                if (kv.Value.Handle.IsCompleted)
                    _finished.Add(kv.Key);

            foreach (var node in _finished)
            {
                // Uploads are bounded by wall-clock time, not by a chunk count: a 129-vertex
                // chunk costs an order of magnitude more to upload than a 33-vertex one, so a
                // fixed count is a budget for the wrong thing.
                if (_clock.Elapsed.TotalMilliseconds > _uploadBudgetMs) break;

                var p = _inFlight[node];
                _inFlight.Remove(node);
                p.Handle.Complete();          // already true; makes the safety system happy

                // Second "still wanted?" check. Between scheduling and now the node may have
                // been collapsed; if so its buffers go back and nothing is ever shown.
                if (!_wanted.Contains(node))
                {
                    p.MeshData.Dispose();
                    p.DisposeBuffers();
                    continue;
                }

                Upload(p);
            }
        }

        void Upload(Pending p)
        {
            int R = _resolution;
            var data = p.MeshData[0];

            ChunkMeshBuffers.SharedIndices(R, _flipWinding).CopyTo(data.GetIndexData<ushort>());
            data.SetSubMesh(0, new SubMeshDescriptor(0, ChunkMeshLayout.IndexCount(R)),
                            MeshUpdateFlags.DontRecalculateBounds);

            var chunk = _pool.Acquire();
            var mesh  = chunk.Mesh;

            Mesh.ApplyAndDisposeWritableMeshData(p.MeshData, mesh, MeshUpdateFlags.DontRecalculateBounds);

            // Bounds from the job's measured min/max rather than a recalculation pass, which
            // would walk every vertex again on the main thread.
            var root   = _roots[p.Node.Quad];
            float half = (float)(SurfaceMath.NodeWorldSize(_surface, root, p.Node) * 0.5);
            float minH = p.HeightBounds[0], maxH = p.HeightBounds[1];
            float pad  = math.max(math.abs(minH), math.abs(maxH)) + _skirtDepth;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(half * 2f + pad * 2f,
                                                               half * 2f + pad * 2f,
                                                               half * 2f + pad * 2f));

            chunk.Anchor = p.Anchor;
            chunk.Rebase(WorldOriginSystem.WorldOrigin);
            chunk.gameObject.name = $"Chunk_q{p.Node.Quad}_d{p.Node.Depth}_{p.Node.X}_{p.Node.Y}";

            p.DisposeBuffers();
            ChunkReady?.Invoke(new Result(p.Node, chunk, minH, maxH));
        }

        // Play-mode teardown. Every in-flight job must be completed before its buffers are
        // freed, or the job system logs a leak for each one.
        public void Dispose()
        {
            foreach (var kv in _inFlight)
            {
                var p = kv.Value;
                p.Handle.Complete();
                p.MeshData.Dispose();
                p.DisposeBuffers();
            }
            _inFlight.Clear();
            _heap.Clear();
            _queued.Clear();
            _wanted.Clear();
        }

        // ── Binary min-heap ──────────────────────────────────────────────────
        //
        // A List-backed heap rather than SortedSet or a sorted insert: the queue is touched
        // every frame and must not allocate.

        void HeapPush(Request r)
        {
            _heap.Add(r);
            int i = _heap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_heap[parent].CompareTo(_heap[i]) <= 0) break;
                (_heap[parent], _heap[i]) = (_heap[i], _heap[parent]);
                i = parent;
            }
        }

        Request HeapPop()
        {
            var top = _heap[0];
            _heap[0] = _heap[_heap.Count - 1];
            _heap.RemoveAt(_heap.Count - 1);

            int i = 0, n = _heap.Count;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, best = i;
                if (l < n && _heap[l].CompareTo(_heap[best]) < 0) best = l;
                if (r < n && _heap[r].CompareTo(_heap[best]) < 0) best = r;
                if (best == i) break;
                (_heap[best], _heap[i]) = (_heap[i], _heap[best]);
                i = best;
            }
            return top;
        }
    }
}
