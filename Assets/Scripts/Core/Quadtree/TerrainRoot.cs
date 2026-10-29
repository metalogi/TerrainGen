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
        [Tooltip("The water's material, for a TerrainSettings with Ocean on.")]
        public Material OceanMaterial;

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
        MacroMaps           _macro;
        HeightParams        _params;
        OceanParams         _ocean;
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
        static readonly int     ViewPositionId  = Shader.PropertyToID(WorldOriginSystem.ViewPositionGlobal);
        static readonly int     LodVerticalId   = Shader.PropertyToID("_SonomaLodVertical");
        readonly        Vector4[] _morphRanges  = new Vector4[MorphRangeCount];

        // The biome colours the _SONOMA_BIOMES shader path blends by the vertex's weights, one
        // per biome slot. A global, like _SonomaMorphRanges, for the same SRP-batcher reason;
        // and written into a material instead they would, in the Editor, permanently edit the
        // .mat asset from play mode. Linear, as the shader works.
        static readonly int     BiomeColorsId   = Shader.PropertyToID("_SonomaBiomeColors");
        readonly        Vector4[] _biomeColors  = new Vector4[Biomes.MaxBiomes];

        // The sky the _SONOMA_SKY shader path lights and hazes by, from this scene's
        // TerrainSettings; globals because the material is shared by scenes with different skies.
        static readonly int     SkyAmbientId    = Shader.PropertyToID("_SonomaSkyAmbient");
        static readonly int     GroundAmbientId = Shader.PropertyToID("_SonomaGroundAmbient");
        static readonly int     HazeId          = Shader.PropertyToID("_SonomaHaze");
        static readonly int     SurfaceId       = Shader.PropertyToID("_SonomaSurface");

        // Koschmieder's constant, ln 50: the optical depth at which haze has taken all but 2% of
        // a dark object's contrast against the sky, which is what "visibility" means. So a
        // visibility of V metres is a density of 3.912 / V per metre.
        const float VisibilityOpticalDepth = 3.912f;

        // Terrain height above the base surface at a base-surface point, in metres, at full
        // detail. For tools: TopologyFlyCamera scales its speed by clearance above the ground,
        // and at Earth scale the ground is kilometres from the base surface. Zero until the
        // terrain has started.
        //
        // Macro included, read at MaxDepth's level as the finest chunks read it. Without it the
        // ground is understated by the base height, kilometres: the fly camera then believes it
        // is that much higher, flies too fast, and sets its near plane to a hundredth of a
        // clearance it does not have, into the ground. The body is
        // TerrainHeightFunction.HeightAtPoint, which the tests call too.
        public float HeightAt(double3 surfacePoint) =>
            _selector == null || _macro == null ? 0f
            : TerrainHeightFunction.HeightAtPoint(Surface, surfacePoint, Settings.MaxDepth, _params,
                                                  _macro.Params, _macro.Reader);

        // Biome weights at a base-surface point, as the deepest chunks classify it; zero without a
        // macro layer or biomes. For tools (the fly camera's readout).
        public float4 BiomeWeightsAt(double3 surfacePoint)
        {
            if (_selector == null || _macro == null || !_macro.Params.Enabled) return 0f;
            SurfaceMath.Locate(Surface, surfacePoint, out int quad, out double u, out double v);
            Biomes.Modulate(MacroSampler.Read(_macro.Reader, _macro.Params, quad,
                                              _macro.Params.Layout.MacroLevel(Settings.MaxDepth), u, v),
                            _macro.Params, out float4 weights);
            return weights;
        }

        // Biome names in weight order, and whether this scene has continents (so that sea level,
        // 0, divides ocean from land).
        public string BiomeName(int slot)
        {
            var b = Settings.Macro != null ? Settings.Macro.Biomes : null;
            return b != null && slot < b.Length && b[slot] != null ? b[slot].name : "Biome " + slot;
        }
        public bool HasContinents => Settings != null && Settings.Macro != null && Settings.Macro.Continents != null;

        public int ResidentChunks =>_selector != null ? _selector.ResidentCount : 0;
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
            // The octave bands biomes scale separately. Without a macro layer every band gain is
            // zero, so where the bands fall changes nothing and they are left unset.
            if (Settings.Macro != null)
                _params = _params.WithBands(Settings.Macro.LowBandEdge, Settings.Macro.MidBandEdge);
            // Close-up roughness, when configured; without it one persistence runs throughout.
            if (Settings.RoughnessCrossover > 0.0)
                _params = _params.WithRoughness(Settings.FinePersistence, Settings.RoughnessCrossover);
            // The continental cutoff, with a continent map: the detail octaves it carries instead
            // are dropped (Docs/Continents_Plan.md, K4). Before LodMath and the scheduler, which
            // copy the parameters.
            ContinentSettings continents = Settings.Macro != null ? Settings.Macro.Continents : null;
            if (continents != null)
                _params = _params.WithContinentalCutoff(continents.DetailCutoff, continents.CoarseOctaveAmplitude);
            // The gully term's slope damping (M5c, E6), when configured. Before the macro build,
            // whose erosion checks it stays out of the octaves it erodes.
            if (Settings.GullyDamping > 0f)
                _params = _params.WithGullies(Settings.GullyDamping, Settings.GullyStartWavelength,
                                              Settings.WetFloorDamping);
            _lod    = LodMath.Create(Surface, Settings, _params);
            FillBiomeColors();

            if (_params.IsOctaveStarved(Settings.MaxDepth))
                Debug.LogWarning(
                    $"[TerrainRoot] OctaveCount {Settings.OctaveCount} runs out at depth " +
                    $"{Settings.OctaveCount - 1 - _params.K0}, so the " +
                    $"{Settings.MaxDepth + _params.K0 - Settings.OctaveCount + 1} deepest depths add " +
                    "vertices but no detail: the ground goes smooth as the camera descends. " +
                    $"Raise OctaveCount to {Settings.MaxDepth + _params.K0 + 1}. " +
                    "See HeightParams.IsOctaveStarved.");

            // Skirts are the fallback for transient states where the tree is briefly more
            // than one depth apart across an edge. A depth of zero leaves the skirt vertices
            // in the mesh but flat against the edge, so the vertex layout never changes.
            float skirtDepth    = Settings.SkirtsEnabled ? Settings.SkirtDepth    : 0f;
            float skirtFraction = Settings.SkirtsEnabled ? Settings.SkirtFraction : 0f;

            // The sea level and the erosion's base level are one number in two homes (ocean-a, A3).
            // Refused before the macro build, which a mismatch would otherwise run for seconds first.
            ErosionSettings erosion = Settings.Macro != null ? Settings.Macro.Erosion : null;
            OceanParams.CheckSeaLevel(Settings.SeaLevel, erosion != null, erosion != null ? erosion.BaseLevel : 0.0);
            if (Settings.Ocean && OceanMaterial == null)
            {
                Debug.LogError("[TerrainRoot] The TerrainSettings have Ocean on and there is no OceanMaterial; " +
                               "terrain will not generate.");
                enabled = false;
                return;
            }

            // Before the scheduler, whose jobs read it; built on workers and waited for, once.
            _macro = BuildMacro();

            // The flattened world the LOD measures in, which needs a bound on the terrain's height,
            // and so the macro layer's. Before the selector, which copies the LodMath.
            double envelope = _macro.MaxAbsHeight(_params);
            _lod = _lod.WithVertical(LodVertical.Create(Surface, Settings.LodVerticalScale, envelope));
            if (!_lod.Vertical.IsIdentity)
                Debug.Log($"[TerrainRoot] LOD measures distance with terrain heights scaled by " +
                          $"{_lod.Vertical.Scale:G3}, within a height envelope of " +
                          $"{_lod.Vertical.Envelope / 1000.0:F1} km. See LodVertical.");

            // The sea, after the macro maps, which bound the terrain's height it must lie within.
            _ocean = OceanParams.Create(Settings.Ocean, Settings.SeaLevel, Settings.OceanTerrainOverlap,
                                        Settings.ChunkResolution, envelope, erosion != null,
                                        erosion != null ? erosion.BaseLevel : 0.0);
            if (_ocean.Enabled)
                Debug.Log($"[TerrainRoot] Ocean at {_ocean.SeaLevel:F1} m, overlap {_ocean.TerrainOverlap:F1} m.");

            _pool      = new ChunkPool(transform, ChunkMaterial, Settings.ChunkResolution);
            _scheduler = new GenerationScheduler(Surface, Roots, _params, _pool, skirtDepth,
                                                 skirtFraction, Settings.MaxInFlightJobs,
                                                 Settings.UploadBudgetMs, _macro);
            _selector  = new LodSelector(Surface, Roots, _lod, _scheduler, _pool,
                                         Settings.MaxResidentChunks);
            _scheduler.ChunkReady += _selector.OnChunkReady;

            PushShaderGlobals(LodViewPosition(ViewPositionRenderSpace()));
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
        // selection and the morph cannot drift apart within a frame -- including a frame that
        // rebases, because WorldOriginSystem.ShiftOrigin moves this value with the world.
        //
        // It is the camera as the LOD sees it, flattened by LodVertical, and _SonomaLodVertical
        // is the map the shader flattens each vertex by before measuring: (Flatten, Anchor),
        // and (0, 0) -- the identity -- for a configuration that never set it, which is also
        // what an unset global reads as.
        void PushShaderGlobals(Vector3 viewPosition)
        {
            for (int d = 0; d < MorphRangeCount; d++)
            {
                _lod.MorphRange(d, out double start, out double end);
                _morphRanges[d] = new Vector4((float)start, (float)end, 0f, 0f);
            }
            Shader.SetGlobalVectorArray(MorphRangesId, _morphRanges);
            Shader.SetGlobalVector(ViewPositionId, viewPosition);
            Shader.SetGlobalVector(LodVerticalId, _lod.Vertical.ShaderParams);
            Shader.SetGlobalVectorArray(BiomeColorsId, _biomeColors);
            PushSky();
        }

        // Every frame rather than once, like the morph ranges, and so that the sky can be tuned
        // in the inspector during play. A handful of multiplies; nothing allocates.
        void PushSky()
        {
            bool  linear  = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Color sky     = linear ? Settings.SkyAmbient.linear    : Settings.SkyAmbient;
            Color ground  = linear ? Settings.GroundAmbient.linear : Settings.GroundAmbient;
            Color haze    = linear ? Settings.HazeColor.linear     : Settings.HazeColor;
            float density = Settings.HazeVisibility > 0f ? VisibilityOpticalDepth / Settings.HazeVisibility : 0f;
            Shader.SetGlobalVector(SkyAmbientId,    new Vector4(sky.r, sky.g, sky.b, 0f));
            Shader.SetGlobalVector(GroundAmbientId, new Vector4(ground.r, ground.g, ground.b, 0f));
            Shader.SetGlobalVector(HazeId,          new Vector4(haze.r, haze.g, haze.b, density));
            Shader.SetGlobalVector(SurfaceId,       new Vector4((float)Surface.Type, (float)Surface.Radius,
                                                                1f / Mathf.Max(Settings.HazeScaleHeight, 1f), 0f));
        }

        // Unused slots are black, which the shader never weights: a vertex's weights are zero
        // there.
        void FillBiomeColors()
        {
            var biomes = Settings.Macro != null ? Settings.Macro.Biomes : null;
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            for (int b = 0; b < _biomeColors.Length; b++)
            {
                Color c = biomes != null && b < biomes.Length && biomes[b] != null ? biomes[b].Color : Color.black;
                _biomeColors[b] = linear ? c.linear : c;
            }
        }

        // No MacroLayerSettings means no macro layer: a disabled one-texel map, and the height
        // function returns M4's bits exactly. With one, the build time is reported once; it is
        // a startup hitch, and its size should be known.
        MacroMaps BuildMacro()
        {
            if (Settings.Macro == null) return MacroMaps.Disabled();

            MacroConfig config = Settings.Macro.ToConfig();
            var p  = MacroParams.Create(Surface, _params, config);
            var cp = ContinentParams.Create(Surface, p, config);
            var ep = ErosionParams.Create(Surface, _params, p, config);
            int unread = p.UnreadLevels(Settings.MaxDepth);
            if (unread > 0)
                Debug.LogWarning(
                    $"[TerrainRoot] Macro layer '{Settings.Macro.name}' has {p.Layout.LevelCount} levels, " +
                    $"but MaxDepth {Settings.MaxDepth} reads only levels 0-{Settings.MaxDepth}: the top " +
                    $"{unread} are built and never read, and each costs four times the memory and build " +
                    $"time of the one below. Lower LevelCount to {Settings.MaxDepth + 1}. See " +
                    "MacroParams.UnreadLevels.");

            if (cp.Enabled) WarnContinents(p, cp, config.Continents.Value);

            var maps = MacroMaps.Build(Surface, Roots, p, cp, _params, ep);
            Debug.Log($"[TerrainRoot] Macro layer '{Settings.Macro.name}': {p.Layout.LevelCount} levels, " +
                      $"{p.Layout.TexelCount:N0} texels ({maps.Bytes / (1024.0 * 1024.0):F1} MB), " +
                      $"octaves up to {p.MaxOctave(p.Layout.LevelCount - 1)}, built in " +
                      $"{maps.BuildMilliseconds:F1} ms.");
            if (maps.Continents != null)
            {
                ContinentStats st = maps.Continents.Stats;
                Debug.Log($"[TerrainRoot] Continents '{Settings.Macro.Continents.name}': {st.MacroCells} macro and " +
                          $"{st.MicroCells:N0} micro cells, {st.LandCells:N0} land, {st.LandFraction:P1} of the map by area; " +
                          $"coverage {st.CoverageRadius / 1000.0:F0} km, saturation {st.SaturationRadius:F2} micro spacings " +
                          $"({st.WarpedSaturation:F2} warped); detail octaves from {_params.FirstOctave}; stages A " +
                          $"{st.StageAMilliseconds:F1} ms, B {st.StageBMilliseconds:F1} ms, C {st.StageCMilliseconds:F1} ms.");
            }
            if (maps.Erosion != null)
            {
                // Inside the build time above. Over about a second is decision D4's threshold for
                // caching the maps to disk (Docs/M5_MacroLayer_Plan.md, E4).
                ErosionStats e = maps.Erosion;
                Debug.Log($"[TerrainRoot] Erosion '{Settings.Macro.Erosion.name}': {e.NodeCount:N0} nodes, " +
                          $"{ep.Iterations} iterations, deepest cut {e.MaxCut:F0} m, base height now within " +
                          $"±{e.MaxAbsBaseHeight / 1000.0:F2} km; {e.TotalMilliseconds:F1} ms (graph " +
                          $"{e.GraphMilliseconds:F1}, heights {e.HeightsMilliseconds:F1}, pass {e.PassMilliseconds:F1}, " +
                          $"wetness {e.WriteMilliseconds:F1}, pyramid {e.PyramidMilliseconds:F1}).");
            }
            return maps;
        }

        // K10: the two configurations that build and draw a continent map nothing can carry. A
        // micro cell under four top-level texels across is aliased by the pyramid's band limit; a
        // detail cutoff under four top-level texels drops octaves the macro layer's top level cannot
        // carry either, so that band is drawn by nothing.
        void WarnContinents(in MacroParams p, in ContinentParams cp, in ContinentConfig k)
        {
            double texel = ContinentParams.TopTexelSpacing(_params.S0, p.Layout);
            if (cp.MicroCellTexels(_params.S0, p.Layout) < 4.0)
                Debug.LogWarning(
                    $"[TerrainRoot] Continents '{Settings.Macro.Continents.name}': a micro cell is " +
                    $"{cp.MicroCellTexels(_params.S0, p.Layout):F1} top-level macro texels across, under the four the pyramid's band " +
                    "limit needs, so the coastline aliases. Raise LevelCount or lower MicroCells.");
            if (k.DetailCutoff > 0.0 && k.DetailCutoff < 4.0 * texel)
                Debug.LogWarning(
                    $"[TerrainRoot] Continents '{Settings.Macro.Continents.name}': the detail cutoff, " +
                    $"{k.DetailCutoff / 1000.0:F1} km, is under four top-level macro texels ({4.0 * texel / 1000.0:F1} km), " +
                    "so the octaves between are drawn by neither the detail noise nor the macro layer. Raise " +
                    "DetailCutoff or LevelCount.");
        }

        // The surface this component's topology fields describe. Public for the Editor's macro
        // map export, which needs the surface without entering play mode.
        public SurfaceDef BuildSurface() => Topology switch
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
            Vector3 view = LodViewPosition(ViewPositionRenderSpace());

            PushShaderGlobals(view);
            _selector.Run(WorldOriginSystem.WorldOrigin + new double3(view.x, view.y, view.z));
            _scheduler.Update();
        }

        // The camera as the LOD sees it (LodVertical.MapPoint), still in render space. Mapped in
        // double from the absolute position, because the map is defined by altitude above the
        // base surface, and narrowed once; Update then hands the selector this same Vector3 with
        // the origin added back, so the two still measure from one point. The identity returns
        // the camera untouched, so SampleScene's view position is the bits it always was.
        Vector3 LodViewPosition(Vector3 view)
        {
            if (_lod.Vertical.IsIdentity) return view;

            double3 origin = WorldOriginSystem.WorldOrigin;
            double3 flat   = _lod.Vertical.MapPoint(Surface, origin + new double3(view.x, view.y, view.z));
            return (Vector3)(float3)(flat - origin);
        }

        // The camera transform is render space (world - origin), which is also the space
        // chunk transforms and the shader's positionWS are in, so this is what the morph
        // wants unmodified. LOD selection works in absolute world space, because node centres
        // do, so Update adds the origin back before calling Run -- skipping that would
        // collapse the whole tree the first time WorldOriginSystem rebases.
        //
        // The camera is WorldOriginSystem's, not one of TerrainRoot's own: the rebase moves that
        // camera and only that one, so measuring from any other would put selection and the
        // morph a whole RebaseThreshold off after every rebase. A rebase in LateUpdate also
        // carries the view position pushed here along with the world.
        Vector3 ViewPositionRenderSpace()
        {
            var cam = WorldOriginSystem.ActiveCamera;
            return cam != null ? cam.transform.position : Vector3.zero;
        }

        void OnDestroy()
        {
            if (_scheduler != null && _selector != null)
                _scheduler.ChunkReady -= _selector.OnChunkReady;

            _selector?.Dispose();
            _scheduler?.Dispose();
            // After the scheduler, which completes the in-flight jobs that read the maps.
            // Freed first, they throw in the Editor and are read after free in a build.
            _macro?.Dispose();
            _macro = null;
            _pool?.Dispose();
            ChunkMeshBuffers.DisposeCache();
        }
    }
}
