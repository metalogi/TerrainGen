using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;
using static Sonoma.Tests.TerrainReliefTests;

namespace Sonoma.Tests
{
    // MaxResidentChunks against the working set SampleScene actually wants.
    //
    // The relief pad is part of what sets that working set: a node's bounding sphere is
    // centred on its mid-elevation and widened by half its relief, so rough ground is nearer
    // and splits further out. With the pad zeroed the worst case over a face is 2946 (a face
    // corner). With SampleScene's real relief it is 3546, at the camera below, half a metre
    // above the terrain -- the worst of 1.58 million camera positions over all six faces,
    // measured out of Editor on 2026-10-10. MaxResidentChunks shipped at 3200, sized from the
    // geometry alone, and the budget warning fired in the first play session.
    //
    // Editor-only: the relief comes from the real HeightSampleJob under Burst. The same
    // selection with the relief computed managed takes about 16 s under the Editor's Mono,
    // for some 2,700 chunks; under Burst it is a fraction of a second. With Burst compilation
    // switched off it still passes, at the slow speed.
    public class WorkingSetTests
    {
        // Jobs in flight at once. Each has its own buffers -- sub-arrays of one big buffer would
        // share a safety handle, and the job system would refuse to run them side by side.
        const int Batch = 64;

        SurfaceDef   _surface;
        RootQuad[]   _roots;
        HeightParams _params;
        LodMath      _lod;

        NativeArray<double3>[] _points;
        NativeArray<float3>[]  _normals;
        NativeArray<float>[]   _heights;
        readonly JobHandle[]   _handles = new JobHandle[Batch];

        readonly Dictionary<NodeId, (float min, float max)> _relief =
            new Dictionary<NodeId, (float min, float max)>();
        readonly HashSet<NodeId> _wanted = new HashSet<NodeId>();
        List<NodeId> _level = new List<NodeId>();
        List<NodeId> _next  = new List<NodeId>();

        [SetUp]
        public void SetUp()
        {
            _surface = SurfaceDef.CubeSphere(Radius);
            _roots   = SurfaceMath.BuildRoots(_surface);
            _params  = HeightParams.Create(_surface, Resolution, 0.0, OctaveCount, HeightScale,
                                           Persistence, Lacunarity, Seed);
            _lod     = LodMath.Create(_surface, _params, SplitFactor, MorphStart, Hysteresis,
                                      Preload, MaxDepth);

            int samples = (Resolution + 2) * (Resolution + 2);
            _points  = new NativeArray<double3>[Batch];
            _normals = new NativeArray<float3>[Batch];
            _heights = new NativeArray<float>[Batch];
            for (int k = 0; k < Batch; k++)
            {
                _points[k]  = new NativeArray<double3>(samples, Allocator.Persistent);
                _normals[k] = new NativeArray<float3>(samples, Allocator.Persistent);
                _heights[k] = new NativeArray<float>(samples, Allocator.Persistent);
            }
        }

        [TearDown]
        public void TearDown()
        {
            for (int k = 0; k < Batch; k++)
            {
                if (_points[k].IsCreated)  _points[k].Dispose();
                if (_normals[k].IsCreated) _normals[k].Dispose();
                if (_heights[k].IsCreated) _heights[k].Dispose();
            }
        }

        [Test]
        public void WorstCaseWorkingSetFitsTheBudget()
        {
            SurfaceMath.SurfaceFrame(_surface, _roots[5], 18.0 / 512.0, 154.0 / 512.0,
                                     out double3 ground, out float3 up);
            float h = TerrainHeightFunction.Height(ground, _params.MaxOctave(MaxDepth),
                                                   MacroSample.Zero, _params);
            int wanted = Select(ground + (double3)up * (h + 0.5));

            Assert.AreEqual(3546, wanted, 36,
                "the worst-case working set has moved by more than 1%; re-measure it and " +
                "re-check MaxResidentChunks against the new figure");
            Assert.Less(wanted, Budget,
                "MaxResidentChunks is below the working set; the selector will build and evict " +
                "the same chunks every frame");
        }

        // LodSelector's wanted set in its steady state: every wanted node resident and padded by
        // its own relief, and every node held split that hysteresis can hold
        // (currentlySplit: true), which is what a camera that has just backed away leaves behind.
        //
        // Level by level rather than recursively, so each depth's chunks go to the job system as
        // one batch. The set is the same either way.
        int Select(double3 camera)
        {
            _wanted.Clear();
            _level.Clear();
            for (int q = 0; q < _roots.Length; q++) _level.Add(new NodeId(q, 0, 0, 0));

            for (int depth = 0; _level.Count > 0; depth++)
            {
                foreach (var node in _level) _wanted.Add(node);

                // A node at MaxDepth neither splits nor preloads, so neither its distance nor
                // its relief can change the count.
                if (depth >= _lod.MaxDepth) break;

                ComputeRelief(_level);

                _next.Clear();
                foreach (var node in _level)
                {
                    double dist = Distance(node, camera);
                    if (_lod.ShouldSplit(dist, depth, currentlySplit: true))
                        for (int i = 0; i < 4; i++) _next.Add(node.Child(i));
                    else if (dist < _lod.PreloadDistance(depth))
                        for (int i = 0; i < 4; i++) _wanted.Add(node.Child(i));
                }
                (_level, _next) = (_next, _level);
            }
            return _wanted.Count;
        }

        // LodSelector.NodeDistance with the node resident.
        double Distance(NodeId node, double3 camera)
        {
            var r    = _relief[node];
            var root = _roots[node.Quad];
            SurfaceMath.SurfaceFrame(_surface, root,
                0.5 * (node.UMin + node.UMax), 0.5 * (node.VMin + node.VMax),
                out double3 centre, out float3 normal);
            centre += (double3)normal * (0.5 * (r.min + r.max));
            double radius = 0.5 * SurfaceMath.NodeWorldSize(_surface, root, node)
                          + 0.5 * (r.max - r.min);
            return math.max(0.0, math.distance(camera, centre) - radius);
        }

        // Each chunk's height range as the generation path produces it: HeightSampleJob over the
        // (R+2)^2 grid, then min and max over the R x R main grid, as ChunkMeshJob takes them.
        void ComputeRelief(List<NodeId> nodes)
        {
            int g = Resolution + 2;

            for (int start = 0; start < nodes.Count; start += Batch)
            {
                int n = math.min(Batch, nodes.Count - start);
                for (int k = 0; k < n; k++)
                {
                    var node = nodes[start + k];
                    _handles[k] = new HeightSampleJob
                    {
                        Surface     = _surface,
                        Root        = _roots[node.Quad],
                        Node        = node,
                        Params      = _params,
                        MaxOctave   = _params.MaxOctave(node.Depth),
                        Resolution  = Resolution,
                        BasePoints  = _points[k],
                        BaseNormals = _normals[k],
                        Heights     = _heights[k],
                    }.Schedule(g * g, 64);
                }
                JobHandle.ScheduleBatchedJobs();

                for (int k = 0; k < n; k++)
                {
                    _handles[k].Complete();
                    var   heights = _heights[k];
                    float min = float.MaxValue, max = float.MinValue;
                    for (int j = 0; j < Resolution; j++)
                    for (int i = 0; i < Resolution; i++)
                    {
                        float v = heights[(j + 1) * g + (i + 1)];
                        min = math.min(min, v);
                        max = math.max(max, v);
                    }
                    _relief[nodes[start + k]] = (min, max);
                }
            }
        }
    }
}
