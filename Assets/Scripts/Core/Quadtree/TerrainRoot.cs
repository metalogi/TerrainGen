using Unity.Mathematics;
using UnityEngine;
using Sonoma.Core.CoordinateSpace;
using Sonoma.Core.Generation;
using Sonoma.Core.Rendering;
using Sonoma.Core.Surface;
using Sonoma.Systems.Configuration;

namespace Sonoma.Core.Quadtree
{
    // The scene's entry point to the terrain: owns the surface definition, the chunk pool,
    // the generation scheduler and the LOD selector, and drives them once per frame.
    //
    // The per-frame order is selector, then scheduler. The selector decides what should be
    // resident and what should be drawn using the chunks that already exist; the scheduler
    // then lands whatever finished, hidden, for the selector to show on the next pass. That
    // costs one frame of latency on a newly generated chunk and buys the guarantee that a
    // child is never drawn over the parent it replaces.
    public class TerrainRoot : MonoBehaviour
    {
        [Header("Settings")]
        public TerrainSettings Settings;
        public Material ChunkMaterial;
        [Tooltip("Camera driving LOD selection. Falls back to Camera.main.")]
        public Camera ViewCamera;

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
        LodSelector         _selector;
        ChunkPool           _pool;
        HeightParams        _params;
        LodMath             _lod;

        // The morph ranges the vertex shader reads, one entry per depth. A global array
        // rather than a per-chunk MaterialPropertyBlock: a property block would break SRP
        // batching, which is the whole reason the chunk's depth travels in TEXCOORD2.w
        // instead of as a material property.
        //
        // The size matches SONOMA_MAX_MORPH_DEPTH + 1 in SonomaTerrainTriplanar.shader. The
        // shader clamps its index into it, so a chunk deeper than this is drawn unmorphed
        // rather than reading off the end.
        const  int              MorphRangeCount = 32;
        static readonly int     MorphRangesId   = Shader.PropertyToID("_SonomaMorphRanges");
        readonly        Vector4[] _morphRanges  = new Vector4[MorphRangeCount];

        public int ResidentChunks => _selector != null ? _selector.ResidentCount : 0;
        public int VisibleChunks  => _selector != null ? _selector.VisibleCount  : 0;
        public int InFlightJobs   => _scheduler != null ? _scheduler.InFlightCount : 0;

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
            _lod    = LodMath.Create(Settings, _params);

            // Skirts are the fallback for transient states where the tree is briefly more
            // than one depth apart across an edge. A depth of zero leaves the skirt vertices
            // in the mesh but flat against the edge, so the vertex layout never changes.
            float skirtDepth = Settings.SkirtsEnabled ? Settings.SkirtDepth : 0f;

            _pool      = new ChunkPool(transform, ChunkMaterial, Settings.ChunkResolution);
            _scheduler = new GenerationScheduler(Surface, Roots, _params, _pool, skirtDepth,
                                                 Settings.MaxInFlightJobs, Settings.UploadBudgetMs);
            _selector  = new LodSelector(Surface, Roots, _lod, _scheduler, _pool,
                                         Settings.MaxResidentChunks);
            _scheduler.ChunkReady += _selector.OnChunkReady;

            PushMorphRanges();
        }

        // (start_d, end_d) per depth, in world metres. Constant for a given configuration,
        // but pushed every frame: shader globals are process-wide, and anything else that
        // sets this name -- another TerrainRoot, a domain reload, an editor script -- would
        // otherwise leave the terrain morphing against someone else's ranges.
        void PushMorphRanges()
        {
            for (int d = 0; d < MorphRangeCount; d++)
            {
                _lod.MorphRange(d, out double start, out double end);
                _morphRanges[d] = new Vector4((float)start, (float)end, 0f, 0f);
            }
            Shader.SetGlobalVectorArray(MorphRangesId, _morphRanges);
        }

        SurfaceDef BuildSurface() => Topology switch
        {
            SurfaceType.CubeSphere => SurfaceDef.CubeSphere(Radius, TangentAdjust),
            SurfaceType.Cylinder   => SurfaceDef.Cylinder(Radius, Length, Cols, Rows),
            _                      => SurfaceDef.PlaneGrid(TileSize, Cols, Rows),
        };

        void Update()
        {
            if (_selector == null) return;

            PushMorphRanges();
            _selector.Run(CameraWorldPosition());
            _scheduler.Update();
        }

        // LOD works in absolute world space, because node centres do. The camera transform
        // is render space (world - origin), so the origin has to be added back; skipping
        // that would collapse the whole tree the first time WorldOriginSystem rebases.
        double3 CameraWorldPosition()
        {
            var cam = ViewCamera != null ? ViewCamera : Camera.main;
            if (cam == null) return WorldOriginSystem.WorldOrigin;

            Vector3 p = cam.transform.position;
            return WorldOriginSystem.WorldOrigin + new double3(p.x, p.y, p.z);
        }

        void OnDestroy()
        {
            if (_scheduler != null && _selector != null)
                _scheduler.ChunkReady -= _selector.OnChunkReady;

            _selector?.Dispose();
            _scheduler?.Dispose();
            _pool?.Dispose();
            ChunkMeshBuffers.DisposeCache();
        }
    }
}
