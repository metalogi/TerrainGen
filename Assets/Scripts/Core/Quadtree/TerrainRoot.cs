using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Sonoma.Core.Generation;
using Sonoma.Core.Rendering;
using Sonoma.Core.Surface;
using Sonoma.Systems.Configuration;

namespace Sonoma.Core.Quadtree
{
    // The scene's entry point to the terrain: owns the surface definition, the chunk pool and
    // the generation scheduler, and asks for the chunks that should exist.
    //
    // In M2 "the chunks that should exist" is exactly the root quads -- six on a cube-sphere.
    // There is deliberately no subdivision: LodSelector is M3, and the point of stopping here
    // is to validate the Burst pipeline, the seam guarantee and the scheduler in isolation,
    // without an LOD policy on top confusing what is being tested. Expect a visibly coarser
    // scene than the prototype until M3 lands.
    public class TerrainRoot : MonoBehaviour
    {
        [Header("Settings")]
        public TerrainSettings Settings;
        public Material ChunkMaterial;

        [Header("Topology")]
        public SurfaceType Topology = SurfaceType.CubeSphere;
        public double Radius   = 6371000.0;
        public double TileSize = 1000.0;
        public double Length   = 32000.0;
        public int    Cols     = 8;
        public int    Rows     = 4;
        public bool   TangentAdjust = true;

        public SurfaceDef Surface { get; private set; }
        public RootQuad[] Roots   { get; private set; }

        GenerationScheduler _scheduler;
        ChunkPool           _pool;
        HeightParams        _params;
        readonly Dictionary<NodeId, TerrainChunk> _live = new Dictionary<NodeId, TerrainChunk>();

        public int LiveChunks    => _live.Count;
        public int InFlightJobs  => _scheduler != null ? _scheduler.InFlightCount : 0;

        void Start()
        {
            if (Settings == null)
            {
                Debug.LogError("[TerrainRoot] No TerrainSettings assigned; terrain will not generate.");
                enabled = false;
                return;
            }

            Surface = BuildSurface();
            Roots   = SurfaceMath.BuildRoots(Surface);
            _params = HeightParams.Create(Surface, Settings.ChunkResolution, Settings.OctaveWavelength0,
                                          Settings.OctaveCount, Settings.HeightScale,
                                          Settings.Persistence, Settings.Lacunarity, Settings.Seed);

            _pool      = new ChunkPool(transform, ChunkMaterial, Settings.ChunkResolution);
            _scheduler = new GenerationScheduler(Surface, Roots, _params, _pool, Settings.SkirtDepth,
                                                 Settings.MaxInFlightJobs, Settings.UploadBudgetMs);
            _scheduler.ChunkReady += OnChunkReady;

            for (int q = 0; q < Roots.Length; q++)
                _scheduler.Enqueue(new NodeId(q, 0, 0, 0), q);
        }

        SurfaceDef BuildSurface() => Topology switch
        {
            SurfaceType.CubeSphere => SurfaceDef.CubeSphere(Radius, TangentAdjust),
            SurfaceType.Cylinder   => SurfaceDef.Cylinder(Radius, Length, Cols, Rows),
            _                      => SurfaceDef.PlaneGrid(TileSize, Cols, Rows),
        };

        void Update()
        {
            _scheduler?.Update();
        }

        void OnChunkReady(NodeId node, TerrainChunk chunk)
        {
            // A node can only be built once at a time, but a rebuild would land here with the
            // old chunk still live. Release it rather than leaking it.
            if (_live.TryGetValue(node, out var existing) && existing != chunk)
                _pool.Release(existing);

            _live[node] = chunk;
        }

        void OnDestroy()
        {
            _scheduler?.Dispose();
            _pool?.Dispose();
            ChunkMeshBuffers.DisposeCache();
        }
    }
}
