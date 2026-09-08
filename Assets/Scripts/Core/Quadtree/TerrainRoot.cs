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
        // The size matches SONOMA_MAX_MORPH_DEPTH + 1 in SonomaTerrainTriplanar.shader, and
        // is comfortably above the deepest addressable node: LodMath.Create refuses a
        // MaxDepth above NodeId.MaxAddressableDepth (30), so nothing can reach the shader's
        // clamp. That matters, because the clamp bounds the read but does not degrade
        // gracefully -- slot 31's range is centimetres wide, so a chunk landing there would
        // draw permanently morphed onto its parent rather than unmorphed.
        const  int              MorphRangeCount = 32;
        static readonly int     MorphRangesId   = Shader.PropertyToID("_SonomaMorphRanges");
        static readonly int     ViewPositionId  = Shader.PropertyToID("_SonomaViewPosition");
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
            _lod    = LodMath.Create(Surface, Settings, _params);

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

            PushShaderGlobals(ViewPositionRenderSpace());
        }

        // Everything the vertex shader needs to run the morph.
        //
        // _SonomaMorphRanges is (start_d, end_d) per depth in world metres, constant for a
        // given configuration but pushed every frame anyway: shader globals are process-wide,
        // and anything else that sets these names -- another TerrainRoot, a domain reload, an
        // editor script -- would otherwise leave the terrain morphing against someone else's
        // numbers.
        //
        // _SonomaViewPosition is the position the morph measures from, and pushing it here
        // rather than letting the shader read _WorldSpaceCameraPos is what makes all four
        // passes agree. URP restores _WorldSpaceCameraPos for the main-light shadow pass and
        // not for the additional-lights one, so a point or spot light casting shadows with no
        // shadowed directional light in the scene would morph the shadow geometry against a
        // stale camera. It is also the same value passed to LodSelector.Run below, so
        // selection and the morph cannot drift apart within a frame.
        void PushShaderGlobals(Vector3 viewPosition)
        {
            for (int d = 0; d < MorphRangeCount; d++)
            {
                _lod.MorphRange(d, out double start, out double end);
                _morphRanges[d] = new Vector4((float)start, (float)end, 0f, 0f);
            }
            Shader.SetGlobalVectorArray(MorphRangesId, _morphRanges);
            Shader.SetGlobalVector(ViewPositionId, viewPosition);
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

            // Read the camera once and feed both from it. The shader morphs by a per-vertex
            // distance and the selector splits by a per-node one; they are only guaranteed to
            // describe the same world if they are measured from the same point.
            Vector3 view = ViewPositionRenderSpace();

            PushShaderGlobals(view);
            _selector.Run(WorldOriginSystem.WorldOrigin + new double3(view.x, view.y, view.z));
            _scheduler.Update();
        }

        // The camera transform is render space (world - origin), which is also the space
        // chunk transforms and the shader's positionWS are in, so this is what the morph
        // wants unmodified. LOD selection works in absolute world space, because node centres
        // do, so Update adds the origin back before calling Run -- skipping that would
        // collapse the whole tree the first time WorldOriginSystem rebases.
        Vector3 ViewPositionRenderSpace()
        {
            var cam = ViewCamera != null ? ViewCamera : Camera.main;
            return cam != null ? cam.transform.position : Vector3.zero;
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
