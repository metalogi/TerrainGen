using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The macro layer's pure core: the pyramid's levels, the edge rule and the bicubic read.
    //
    // UnityEngine-free, so it runs in the out-of-Editor harness. The maps here are small --
    // resolution 9, three levels -- because every rule is generic in the cell count and a full
    // Earth pyramid is half a million texels, which the Editor's Mono would take seconds over.
    // MacroMapsTests holds the Burst build to MacroGenerator.Build at a real size.
    public class MacroTests
    {
        const int Resolution = 9;
        const int Cells0     = Resolution - 1;
        const int LevelCount = 3;

        // Every topology, and every kind of corner: three faces on a cube, four roots at the
        // terrarium's interior corners, two at a ring's open ends, a one-column cylinder whose
        // East edge is its own West, and a plane grid with outer edges that link nowhere.
        internal static readonly (string Name, SurfaceDef Surface)[] Surfaces =
        {
            ("cube sphere",         SurfaceDef.CubeSphere(6371000.0)),
            ("ring",                SurfaceDef.Cylinder(1592750.0, 1250000.0, 8, 1)),
            ("terrarium",           SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4)),
            ("one-column cylinder", SurfaceDef.Cylinder(1000.0, 3000.0, 1, 3)),
            ("plane grid",          SurfaceDef.PlaneGrid(1000.0, 3, 2)),
        };

        // ── The stress scenes' macro layers ──────────────────────────────────
        //
        // Hand copies of each stress scene's MacroLayerSettings asset, held to the files by
        // MacroMapsTests.StressScenesRunTheirMacroLayers. SampleScene has none (ground rule 1).
        //
        // Base heights are about a third of each planet's or ring's detail HeightScale, and equal
        // to the terrarium's, whose 10 km world wants hundreds of metres. They add to the relief
        // each scene's LOD allowance has to cover, so the relief tests size the demand
        // with them (Demand, below).
        //
        // Measured with the macro (2026-10-18, 300 random nodes per depth, 600 in the terrarium,
        // 17 x 17 vertices each), the roughest node's C in units of HeightScale / S0 rose from
        // 6.23 to 6.65 on Earth at depth 19, 5.24 to 5.69 on the small planet at depth 17, 5.80 to
        // 6.00 on the ring at 16 and 3.62 to 4.03 in the terrarium at 9 (its worst, 4.63, was at
        // depth 6). The selector's end-of-session report of the roughest chunk is the arbiter.
        internal const double MacroWavelength0  = 0.0;
        internal const float  MacroPersistence  = 0.5f;
        internal const float  PoleTemperature   = -30f;
        internal const float  LapseRate         = 6.5f;
        internal const float  TemperatureNoise  = 5f;
        internal const float  MoistureNoise     = 1.5f;
        internal const float  RuggednessNoise   = 1.5f;
        internal const uint   MacroSeed         = 7u;

        // ── The biomes, shared by all four ───────────────────────────────────
        //
        // Hand copies of Assets/Settings/Biomes/*.asset. Every band amplitude is at most 1:
        // biomes shape and smooth the terrain, and each scene's ruggedness range decides how far
        // above its configured HeightScale the mountain band goes, sized against its own relief
        // allowance (MaxBandAmplitude, DetailDemand). A table that amplified would have to fit the
        // tightest scene, the terrarium, with no room at all.
        internal const int   BiomeSharpness   = 2;
        internal const float TemperatureScale = 15f;
        internal const float MoistureScale    = 0.5f;

        internal static readonly (string Asset, float3 Color, BiomeConfig Config)[] BiomeTable =
        {
            ("Assets/Settings/Biomes/Grassland.asset",  new float3(0.36f, 0.52f, 0.22f), Biome(15f,  0.55f, 0.7f, 0.6f, 0.7f)),
            ("Assets/Settings/Biomes/Desert.asset",     new float3(0.82f, 0.70f, 0.45f), Biome(27f,  0.15f, 0.6f, 0.4f, 0.8f)),
            ("Assets/Settings/Biomes/Tundra.asset",     new float3(0.70f, 0.72f, 0.70f), Biome(-8f,  0.40f, 1.0f, 0.9f, 0.9f)),
            ("Assets/Settings/Biomes/Rainforest.asset", new float3(0.12f, 0.33f, 0.13f), Biome(24f,  0.85f, 0.8f, 0.8f, 0.6f)),
        };

        internal static BiomeConfig[] BiomeConfigs => Array.ConvertAll(BiomeTable, b => b.Config);

        // From amplitudes, as the assets hold them, to gains relative to one, as the runtime does.
        static BiomeConfig Biome(float t, float m, float low, float mid, float high)
            => new BiomeConfig { Temperature = t, Moisture = m, Gain = new float3(low, mid, high) - 1f };

        internal sealed class SceneMacro
        {
            public string     Scene, Settings, Asset;
            public SurfaceDef Surface;
            public int        Levels, MaxDepth;
            public float      BaseHeightScale, EquatorTemperature;
            public double     LowBandEdge, MidBandEdge;
            public float      RuggednessMin, RuggednessMax;   // amplitudes, as the asset holds them
            public float      RuggednessCentre;               // as the asset holds it
            public float      SceneLapseRate = LapseRate;     // degrees C per km, as the asset holds it

            // The continent map (Docs/Continents_Plan.md): the ContinentSettings asset and its copy,
            // or none. MacroMapsTests holds the copy to the asset.
            public string           ContinentAsset;
            public ContinentConfig? Continents;
            // The final terrain's land share the map's LandFraction is calibrated to (K3).
            public double           LandTarget = 0.30;

            // The macro layer's relief factor, C_m: the base height's half-relief across a node over
            // (node size x (ContinentBound + BaseHeightScale) / S0). MacroReliefFactor for a scene
            // without continents; with them it carries the coasts' slope and is measured per scene
            // (ReliefMeasurements).
            public double     ReliefFactor = MacroReliefFactor;

            // Macro erosion (M5c-1): the ErosionSettings asset and its copy, or none. MacroMapsTests
            // holds the copy to the asset. Config leaves it out, and every figure pinned from Config
            // -- relief factors, envelopes, working sets, skirt shares -- is the uneroded terrain's
            // until E5's re-measurement; ErodedConfig is the configuration the scene runs.
            public string         ErosionAsset;
            public ErosionConfig? Erosion;

            // The largest |BaseHeight'| the eroded build stores, every level (E5): the measured term
            // TerrainRoot's height envelope takes in place of the analytic base-height bound
            // (MacroMaps.MaxAbsHeight). Pinned from ReliefMeasurements.ErodedMaps, and held to the
            // Burst build by MacroMapsTests.ErodedEnvelopesMatchTheBuild, since building eroded maps
            // in a pure test costs seconds a scene.
            public double         ErodedMaxAbsBaseHeight;

            // The relief the LOD boundary has to cover with erosion, measured whole (E5): the largest
            // half-relief of detail and eroded base together over 20,000 random nodes per depth, as a
            // fraction of the node, unflattened, half again over (ReliefMeasurements.ErodedCombinedRelief).
            // Demand returns it, flattened, for an eroded scene in place of the sum of C and C_m.
            public double         ErodedCombinedRelief;

            public MacroConfig ErodedConfig
            {
                get { var c = Config; c.Erosion = Erosion; return c; }
            }

            public MacroParams ErodedParams =>
                MacroParams.Create(Surface, HeightParams.Create(Surface, 33, 0.0, 20, 1f, 0.5f, 2f, 42u), ErodedConfig);

            public MacroConfig Config => new MacroConfig
            {
                LevelCount = Levels, Wavelength0 = MacroWavelength0, Persistence = MacroPersistence,
                BaseHeightScale = BaseHeightScale, EquatorTemperature = EquatorTemperature,
                PoleTemperature = PoleTemperature, LapseRate = SceneLapseRate, TemperatureNoise = TemperatureNoise,
                MoistureNoise = MoistureNoise, RuggednessNoise = RuggednessNoise,
                RuggednessShift = RuggednessCentre - 0.5f, Seed = MacroSeed,
                Biomes = BiomeConfigs, BiomeSharpness = BiomeSharpness,
                TemperatureScale = TemperatureScale, MoistureScale = MoistureScale,
                RuggednessGainMin = RuggednessMin - 1f, RuggednessGainMax = RuggednessMax - 1f,
                Continents = Continents,
            };

            public MacroParams Params =>
                MacroParams.Create(Surface, HeightParams.Create(Surface, 33, 0.0, 20, 1f, 0.5f, 2f, 42u), Config);

            // The largest amplitude of each band anywhere in this scene (MacroParams.MaxBandAmplitude).
            public float3 MaxBandAmplitude => Params.MaxBandAmplitude;
        }

        // The cylinders have no poles, so EquatorTemperature is their temperature everywhere at
        // zero base height: temperate rather than tropical. The terrarium stops at three levels,
        // because its 32 roots would make four 48 MB, and level 2's 60 m texels are already
        // finer than anything a macro layer is for in a 10 km world.
        //
        // Band edges are 2 km and 50 m in the terrarium, whose whole world is 10 km across. On the
        // planets and the ring they are 150 km and 5 km, which makes the mid band, the one
        // ruggedness scales, the mountain band: octaves of 5 to about 100 km. Ruggedness runs
        // from 0.3 on plains to 3.3 in mountain belts there, so the mid band's largest amplitude
        // is 2.97 (the biomes' largest mid amplitude, 0.9, times 3.3). Centred on 0.2, the
        // ruggedness map saturates at 1 on about a ninth of Earth and puts about a third of it at
        // twice the amplitude or more (MountainBeltsAreAMinority). The fine band is left to the
        // biomes, at most 0.9: it spends the LOD relief allowance far faster per metre of height,
        // and scaling it with ruggedness cost the mountains (M5b, the mountain-belt amendment).
        // The terrarium's range, 0.5 to 1.0, had no room to amplify anything.
        internal static readonly SceneMacro[] StressMacros =
        {
            // Continents (Docs/Continents_Plan.md, D1 crisp): land 2,000 m and ocean 6,000 m, a
            // 1,000 km cutoff dropping detail octaves 0-3, and the macro fBm at 0.3 of M5b's 3,000 m.
            // C_m on the built maps, 20,000 random nodes per depth, was 53.7 at its roughest
            // (depth 14) in units of (6,000 + 900) / S0 (ReliefMeasurements.MacroReliefFactor,
            // 2026-10-24); 81 is half again over it. LandFraction 0.322 puts the final terrain at
            // 30%: 29.98% on 50,000 points and 29.68% on another 100,000, with 95.8% agreement and
            // medians of +2,330 and -5,752 m (ReliefMeasurements.CalibrateLandFraction, 2026-10-24).
            new SceneMacro { Scene = "EarthStress", Settings = "Assets/Settings/EarthTerrainSettings.asset",
                             Asset = "Assets/Settings/EarthMacro.asset", Surface = EarthScaleTests.Earth, MaxDepth = EarthScaleTests.MaxDepth,
                             Levels = 4, BaseHeightScale = 900f, EquatorTemperature = 30f,
                             LowBandEdge = 150000.0, MidBandEdge = 5000.0, RuggednessMin = 0.3f, RuggednessMax = 3.3f,
                             // Eroded (E5, 2026-10-25): C_m 63.7 at its roughest (depth 12), the channels
                             // steepening the base height, so 96 from 81 (ReliefMeasurements.ErodedMacroReliefFactor).
                             RuggednessCentre = 0.2f, ReliefFactor = 96.0,
                             ContinentAsset = "Assets/Settings/EarthContinents.asset",
                             Continents = SceneContinentConfig(landFraction: 0.322f, land: 2000f, ocean: 6000f, cutoff: 1000000.0),
                             // Erosion (2026-10-25, amendment 8): the ring's 21 texels of incision on 28.7 km
                             // texels, 600 km. Breaching at 2,000 m left basins it filled by up to 533 m;
                             // at 4,000 m by 58. On the managed build: under a lake 1.1% of the land, land
                             // cut 89 m at the median and 547 at the 99th percentile, rivers 8.1x the
                             // median, 2,270 m at most, and land 30.6% -> 30.2%.
                             ErosionAsset = "Assets/Settings/EarthErosion.asset", ErodedMaxAbsBaseHeight = 7042.6650390625,
                             // E5: 0.7495 of the node at its roughest (depth 19), 20,000 nodes a depth.
                             ErodedCombinedRelief = 1.1243,
                             Erosion = new ErosionConfig { Iterations = 30, IncisionDepth = 600000f, AreaExponent = 0.45f,
                                                           Diffusion = 0.002f, BaseLevel = 0f, MinOutletArea = 0f,
                                                           BreachDepth = 4000f } },
            // Continents (D1 crisp, at Earth's shares of HeightScale): land 1,000 m and ocean
            // 3,000 m, a 250 km cutoff dropping detail octaves 0-2, and the macro fBm at 0.3 of M5b's
            // 2,000 m. C_m was 51.4 at its roughest (depth 14) in units of (3,000 + 600) / S0, and 77
            // is half again over it. LandFraction 0.312 puts the final terrain at 30%: 30.03% on
            // 50,000 points and 29.78% on another 100,000, with 95.5% agreement, 9.4% of the map's
            // land flooded, and medians of +1,161 and -2,885 m (ReliefMeasurements, 2026-10-24).
            new SceneMacro { Scene = "SmallPlanetStress", Settings = StressSceneTests.SmallPlanet.Asset,
                             Asset = "Assets/Settings/SmallPlanetMacro.asset", Surface = StressSceneTests.SmallPlanet.Surface, MaxDepth = StressSceneTests.SmallPlanet.MaxDepth,
                             Levels = 4, BaseHeightScale = 600f, EquatorTemperature = 30f,
                             LowBandEdge = 150000.0, MidBandEdge = 5000.0, RuggednessMin = 0.3f, RuggednessMax = 3.3f,
                             // Eroded (E5): C_m 72.8 at its roughest (depth 16), so 109 from 77.
                             RuggednessCentre = 0.2f, ReliefFactor = 109.0,
                             ContinentAsset = "Assets/Settings/SmallPlanetContinents.asset",
                             Continents = SceneContinentConfig(landFraction: 0.312f, land: 1000f, ocean: 3000f, cutoff: 250000.0),
                             // Erosion (2026-10-25, amendment 8): the ring's 21 texels of incision on 7.2 km
                             // texels, 150 km; breaching at 4,000 m changed nothing over 2,000. On the managed
                             // build: under a lake 0.9% of the land, at most 65 m filled, land cut 38 m at the
                             // median and 266 at the 99th percentile, rivers 5.7x the median, 1,484 m at
                             // most, and land 30.2% -> 29.7%.
                             ErosionAsset = "Assets/Settings/SmallPlanetErosion.asset", ErodedMaxAbsBaseHeight = 3695.10986328125,
                             // E5: 0.6695 of the node at its roughest (depth 14).
                             ErodedCombinedRelief = 1.0042,
                             Erosion = new ErosionConfig { Iterations = 30, IncisionDepth = 150000f, AreaExponent = 0.45f,
                                                           Diffusion = 0.002f, BaseLevel = 0f, MinOutletArea = 0f,
                                                           BreachDepth = 2000f } },
            // Continents (D1 crisp): land 1,500 m and ocean 4,500 m, a 250 km cutoff dropping detail
            // octaves 0-2, and the macro fBm at 0.3 of M5b's 1,500 m. C_m was 48.7 at its roughest
            // (depth 10) in units of (4,500 + 450) / S0, and 73 is half again over it. LandFraction
            // 0.351 puts the final terrain at 30%: 29.99% on 50,000 points and 29.97% on another
            // 100,000, with 93.7% agreement, 16.4% of the map's land flooded, and medians of +1,780
            // and -4,221 m (ReliefMeasurements, 2026-10-24). The prototype's 97.9% took its band
            // gains from M5b's warmer climate; a lapse rate of 0 recovers only 1.3 points of it.
            new SceneMacro { Scene = "RingworldStress", Settings = StressSceneTests.Ringworld.Asset,
                             Asset = "Assets/Settings/RingworldMacro.asset", Surface = StressSceneTests.Ringworld.Surface, MaxDepth = StressSceneTests.Ringworld.MaxDepth,
                             Levels = 4, BaseHeightScale = 450f, EquatorTemperature = 18f,
                             LowBandEdge = 150000.0, MidBandEdge = 5000.0, RuggednessMin = 0.3f, RuggednessMax = 3.3f,
                             // Eroded (E5): C_m 52.4 at its roughest (depth 10), so 79 from 73.
                             RuggednessCentre = 0.2f, ReliefFactor = 79.0,
                             ContinentAsset = "Assets/Settings/RingworldContinents.asset",
                             Continents = SceneContinentConfig(landFraction: 0.351f, land: 1500f, ocean: 4500f, cutoff: 250000.0),
                             // M5c-1's second erosion, to tune with the export. Outlets at sea level, 0,
                             // the continents' coast. The texels are 4.8 km, so incision needs tens of
                             // kilometres: on the managed build (2026-10-25), with diffusion off, 30 km
                             // cut the land 48 m at the 90th percentile and 100 km 155. Diffusion is
                             // scale-free per texel, and at the terrarium's 0.02 it filled up to 564 m
                             // against the coasts' kilometres-per-texel slopes and swamped the
                             // incision; 0.002 fills at most 66 m. Before breaching and the base-level
                             // floor, the land was cut 60 m at the median, 159 at the 90th percentile and
                             // 294 at the 99th. As built now the deepest cut is 1,761 m and the envelope
                             // falls from 32,150 to 31,198 m (ReliefMeasurements.ErodedMaps).
                             ErosionAsset = "Assets/Settings/RingworldErosion.asset", ErodedMaxAbsBaseHeight = 4783.34912109375,
                             // E5: 0.4131 of the node at its roughest (depth 16).
                             ErodedCombinedRelief = 0.6197,
                             // BreachDepth 2,000 m with every water body a sea (2026-10-25, amendment 7): a
                             // fifth of the ring's land lay in closed basins walled by its mountain band,
                             // lakes its rivers crossed uncut. Breached, under a lake 5.5% -> 0.8%, rivers cut
                             // 3.7x the land's median against 0.5x, 0.9% raised by at most 52 m. A size
                             // threshold for seas (MinOutletArea) was tried first and is left at 0: small
                             // bodies include deep scraps of ocean, which breaching then filled by up to 9 km.
                             Erosion = new ErosionConfig { Iterations = 30, IncisionDepth = 100000f, AreaExponent = 0.45f,
                                                           Diffusion = 0.002f, BaseLevel = 0f, MinOutletArea = 0f,
                                                           BreachDepth = 2000f } },
            // Continents (D1 crisp, at the ring's shares of HeightScale): land 600 m and ocean
            // 1,800 m, and the macro fBm at 0.3 of M5b's 1,500 m. The plan started it without a
            // cutoff, which left 86% agreement and 28% of the map's land flooded; a 150 km cutoff
            // drops detail octave 0 alone, 187.5 km, in the low band, so the mountain band is whole.
            // CoastWidth is 0.8: at 1 the warp took four texels on the open rims, where a point has
            // seeds on one side only, 1.46 micro spacings from their class, past the plateau's 1.41
            // (0.8 allows 1.55). C_m was 24.9 at its roughest (depth 12) in units of (1,800 + 450) /
            // S0, and 37 is half again over it. LandFraction 0.300 puts the final terrain at 30%:
            // 30.01% on 50,000 points and 30.03% on another 100,000, with 92.5% agreement, 12.6%
            // flooded, and medians of +747 and -1,764 m (ReliefMeasurements, 2026-10-24).
            new SceneMacro { Scene = "NarrowRingStress", Settings = StressSceneTests.NarrowRing.Asset,
                             Asset = "Assets/Settings/NarrowRingMacro.asset", Surface = StressSceneTests.NarrowRing.Surface, MaxDepth = StressSceneTests.NarrowRing.MaxDepth,
                             Levels = 3, BaseHeightScale = 450f, EquatorTemperature = 18f,
                             LowBandEdge = 150000.0, MidBandEdge = 5000.0, RuggednessMin = 0.3f, RuggednessMax = 3.3f,
                             // Eroded (E5): C_m 54.9 at its roughest (depth 11), so 82 from 37.
                             RuggednessCentre = 0.2f, ReliefFactor = 82.0,
                             ContinentAsset = "Assets/Settings/NarrowRingContinents.asset",
                             Continents = SceneContinentConfig(landFraction: 0.300f, land: 600f, ocean: 1800f, cutoff: 150000.0, coastWidth: 0.8f),
                             // Erosion (2026-10-26, amendment 10): the other scenes' 21 texels of incision on
                             // 1.5 km texels, 30 km, with diffusion 0.002 and a 2,000 m breach. Without
                             // breaching 7.4% of the land lay under a lake and rivers cut 0.9x the median; a
                             // 1,000 m breach filled basins by up to 1.1 km. On the managed build: under a lake
                             // 1.0%, at most 120 m filled, land cut 38 m at the median and 361 at the 99th
                             // percentile, rivers 2.6x the median, and land 30.1% -> 29.5%.
                             // E5: the largest base height is 3,250 m, over its analytic bound, since the
                             // breach carves gorges up to 1.9 km into a base relief of 600 m land and 1,800
                             // m ocean: the envelope grows, 27.9 -> 28.7 km, the only scene where it does.
                             // Combined relief 0.536 of the node at its roughest (depth 14).
                             ErosionAsset = "Assets/Settings/NarrowRingErosion.asset", ErodedMaxAbsBaseHeight = 3250.10205078125,
                             ErodedCombinedRelief = 0.8046,
                             Erosion = new ErosionConfig { Iterations = 30, IncisionDepth = 30000f, AreaExponent = 0.45f,
                                                           Diffusion = 0.002f, BaseLevel = 0f, MinOutletArea = 0f,
                                                           BreachDepth = 2000f } },
            new SceneMacro { Scene = "TerrariumStress", Settings = StressSceneTests.Terrarium.Asset,
                             Asset = "Assets/Settings/TerrariumMacro.asset", Surface = StressSceneTests.Terrarium.Surface, MaxDepth = StressSceneTests.Terrarium.MaxDepth,
                             Levels = 3, BaseHeightScale = 200f, EquatorTemperature = 18f,
                             // Eroded (E5): the base height now carries the erosion correction, channels
                             // up to 258 m deep across 59 m texels, against the 200 m fBm C_m is priced
                             // in. C_m 69.2 at its roughest (depth 9), so 104 from MacroReliefFactor's 7.
                             ReliefFactor = 104.0,
                             LowBandEdge = 2000.0, MidBandEdge = 50.0, RuggednessMin = 0.5f, RuggednessMax = 1f,
                             RuggednessCentre = 0.5f,
                             // M5c-1's first erosion, a starting point to tune with the export. Outlets
                             // at +68 m, D1's sea level, which leaves 29.8% of the uneroded terrarium
                             // above it. Measured on the managed build (2026-10-25): land cut 12 m at
                             // the median, 30 at the 90th percentile and 69 at the 99th, 258 m at
                             // most; diffusion number 0.059; the envelope 2,593 m -> 2,318 m, since
                             // the stored base height, +-217 m, is under its analytic bound.
                             ErosionAsset = "Assets/Settings/TerrariumErosion.asset", ErodedMaxAbsBaseHeight = 217.47821044921875,
                             // E5: 1.0196 of the node at its roughest (depth 8): flattened 0.3059, which its 0.39
                             // allowance clears by 1.27x against the 1.25 asked, so deeper erosion here needs room first.
                             ErodedCombinedRelief = 1.5295,
                             // BreachDepth 50 m (amendment 7): under 1% of the land lay in basins, and at 50 m
                             // none does, with 3 m of fill at most and the cut unchanged.
                             Erosion = new ErosionConfig { Iterations = 30, IncisionDepth = 4000f, AreaExponent = 0.45f,
                                                           Diffusion = 0.02f, BaseLevel = 68f, MinOutletArea = 0f,
                                                           BreachDepth = 50f } },
        };

        // A stress scene's continents: K9's defaults with the scene's land fraction, heights and
        // cutoff, as its ContinentSettings asset holds them.
        static ContinentConfig SceneContinentConfig(float landFraction, float land, float ocean, double cutoff, float coastWidth = 1f)
        {
            var k = ContinentConfig.Default;
            k.LandFraction = landFraction; k.LandHeight = land; k.OceanDepth = ocean; k.DetailCutoff = cutoff; k.CoastWidth = coastWidth;
            return k;
        }

        internal static SceneMacro For(string scene)
        {
            var m = Array.Find(StressMacros, x => x.Scene == scene);
            Assert.IsNotNull(m, $"{scene} has no entry in MacroTests.StressMacros");
            return m;
        }

        // The macro layer's own relief factor, as C is the detail noise's: the base height's
        // half-relief across a node over (node size x BaseHeightScale / S0). Scale-free like C, it
        // depends on the macro's spectrum, which every stress scene shares: persistence 0.5,
        // octave 0 at S0, three or four levels. The roughest of 1,000 random nodes per depth at
        // 33 x 33 vertices was 4.12 on Earth (depth 19), 4.70 on the small planet (depth 17),
        // 3.52 on the ring (16) and 2.76 in the terrarium (9) (2026-10-18). 7 is half again over
        // the largest, the margin each scene's C has over its own sample.
        internal const double MacroReliefFactor = 7.0;

        // The detail noise's relief demand, as a fraction of node size (C7): C x HeightScale / S0,
        // where C was measured with the three bands at the amplitudes measuredAt -- the scene's
        // largest on each band, or more. Not one C scaled by the largest amplitude: a band's
        // relief cost per metre of height falls with its wavelength, so the mid band at 3 costs
        // less than every band at 1.44 did. The scene's largest amplitudes are checked against
        // measuredAt, so raising one in an asset fails here until C is re-measured at it.
        //
        // verticalScale is the scene's LodVerticalScale for the demand on the LOD relief allowance,
        // which is spent in the flattened world (LodVertical), and 1 for the true relief.
        internal static double DetailDemand(double reliefFactor, float3 measuredAt, float heightScale, string scene, double s0,
                                            double verticalScale = 1.0)
        {
            float3 max = For(scene).MaxBandAmplitude;
            Assert.IsTrue(math.all(max <= measuredAt),
                $"{scene}: band amplitudes reach {max}, over the {measuredAt} its relief factor was " +
                "measured at; re-measure C at the new amplitudes");
            return verticalScale * reliefFactor * heightScale / s0;
        }

        // The relief demand with the macro layer, as a fraction of node size: the detail estimate
        // plus the macro's. Half-relief is subadditive, so the sum bounds the relief of the two
        // together -- conservatively, since it assumes each one's roughest node is the other's,
        // and in the most amplified biome. Measured before biomes, the terrarium's combined worst
        // was 4.63 in units of HeightScale / S0 against the sum's 16. The flattened world scales
        // both, since it scales the whole height.
        //
        // The macro term prices the base height at its largest amplitude, the fBm's scale plus the
        // continents' bound (0 without continents, which leaves the term's bits as they were): with
        // continents most of the base height's relief is the coasts', which BaseHeightScale alone
        // would leave out.
        //
        // With erosion the sum no longer bounds sensibly: the correction is computed from the detail
        // octaves, so the steepest channels lie where detail was cut away, and on the terrarium the sum
        // came to 0.556 of a node against a measured 0.25 of its 0.39 allowance. An eroded scene's
        // demand is its combined relief measured whole, ErodedCombinedRelief, flattened (E5). The
        // detail check still runs, so raising a band amplitude still fails until C is re-measured.
        internal static double Demand(double reliefFactor, float3 measuredAt, float heightScale, string scene, double s0,
                                      double verticalScale = 1.0)
        {
            var    m      = For(scene);
            double detail = DetailDemand(reliefFactor, measuredAt, heightScale, scene, s0, verticalScale);
            if (m.Erosion.HasValue)
            {
                Assert.Greater(m.ErodedCombinedRelief, 0.0, $"{scene} erodes but has no ErodedCombinedRelief pinned");
                return verticalScale * m.ErodedCombinedRelief;
            }
            return detail + verticalScale * m.ReliefFactor * (m.Params.ContinentBound + m.BaseHeightScale) / s0;
        }

        // A stress scene's height parameters, as TerrainRoot.Start builds them from its spectrum:
        // its bands, and with continents the detail cutoff. The one route for every test that
        // measures a scene's terrain: a copy that missed the cutoff would measure the old terrain
        // and stay green (SceneParametersCarryTheCutoff).
        internal static HeightParams SceneParams(string scene, in HeightParams rough) => SceneParams(For(scene), rough);

        internal static HeightParams SceneParams(SceneMacro m, in HeightParams rough)
        {
            var p = rough.WithBands(m.LowBandEdge, m.MidBandEdge);
            if (m.Continents.HasValue)
                p = p.WithContinentalCutoff(m.Continents.Value.DetailCutoff, m.Continents.Value.CoarseOctaveAmplitude);
            return p;
        }

        // A stress scene's spectrum, before SceneParams: Earth's from EarthScaleTests, the others'
        // from StressSceneTests.
        internal static HeightParams RoughParams(string scene)
        {
            if (scene == "EarthStress") return EarthScaleTests.RoughParams;
            var sc = Array.Find(StressSceneTests.All, x => x.Name == scene);
            Assert.IsNotNull(sc, $"{scene} has no entry in StressSceneTests.All");
            return sc.RoughParams;
        }

        // A stress scene's height bound, as TerrainRoot and GenerationScheduler take it: its
        // spectrum with its bands and cutoff, and its macro layer. With erosion it is the measured
        // bound, MacroMaps.MaxAbsHeight's, from the scene's pinned largest base height (E5).
        internal static double MaxAbsHeight(string scene, in HeightParams rough)
        {
            var m  = For(scene);
            var hp = SceneParams(m, rough);
            return m.Erosion.HasValue
                ? TerrainHeightFunction.MaxAbsHeight(hp, m.ErodedParams, m.ErodedMaxAbsBaseHeight)
                : TerrainHeightFunction.MaxAbsHeight(hp, m.Params);
        }

        // A scene's eroded macro maps, built once per domain by the managed builder TerrainRoot's
        // Burst build is held to: about 7 s a scene under .NET 8, so the measuring tools use it and
        // the pinned tests use the constants it produced.
        internal sealed class SceneErosion
        {
            public HeightParams Height;
            public MacroParams  Macro;
            public float4[]     Texels;
            public float2[]     Drainage;
            public ErosionStats Stats;
        }

        static readonly Dictionary<string, SceneErosion> _sceneErosion = new Dictionary<string, SceneErosion>();

        internal static SceneErosion ErodedBuild(SceneMacro m)
        {
            Assert.IsTrue(m.Erosion.HasValue, $"{m.Scene} has no erosion");
            lock (_sceneErosion)
            {
                if (_sceneErosion.TryGetValue(m.Scene, out var built)) return built;
                var hp = SceneParams(m, RoughParams(m.Scene));
                var c  = m.ErodedConfig;
                var mp = MacroParams.Create(m.Surface, hp, c);
                var ep = ErosionParams.Create(m.Surface, hp, mp, c);
                built = new SceneErosion { Height = hp, Macro = mp };
                built.Texels = MacroGenerator.Build(m.Surface, SurfaceMath.BuildRoots(m.Surface), mp, ContinentParams.Create(m.Surface, mp, c),
                                                    hp, ep, out _, out built.Drainage, out _, out built.Stats);
                _sceneErosion.Add(m.Scene, built);
                return built;
            }
        }

        // A scene's continents, built once per domain in managed code -- about a second a scene
        // under .NET 8 and several under the Editor's Mono -- and shared by every test that reads
        // its terrain: the map, its parameters, and the continent channel LazyTexels adds to the
        // base height.
        internal sealed class SceneContinents
        {
            public MacroParams     Macro;
            public ContinentParams Params;
            public ContinentMap    Map;
            public float[]         Channel;
        }

        static readonly Dictionary<string, SceneContinents> _sceneContinents = new Dictionary<string, SceneContinents>();

        internal static SceneContinents ContinentBuild(SceneMacro m)
        {
            Assert.IsTrue(m.Continents.HasValue, $"{m.Scene} has no continents");
            lock (_sceneContinents)
            {
                if (_sceneContinents.TryGetValue(m.Scene, out var built)) return built;
                var mp = m.Params;
                var cp = ContinentParams.Create(m.Surface, mp, m.Config);
                built = new SceneContinents { Macro = mp, Params = cp };
                built.Channel = MacroGenerator.ContinentChannel(m.Surface, SurfaceMath.BuildRoots(m.Surface), mp, cp, out built.Map);
                _sceneContinents.Add(m.Scene, built);
                return built;
            }
        }

        // A continent scene for the plumbing, in no scene file: a sphere of Earth's radius at two
        // levels, 400 micro cells of seven top-level texels, and detail far below the continents',
        // so the final terrain keeps the map's coastline nearly everywhere.
        internal static readonly SceneMacro SyntheticContinents = new SceneMacro
        {
            Scene = "SyntheticContinents", Surface = SurfaceDef.CubeSphere(6371000.0), MaxDepth = 6,
            Levels = 2, BaseHeightScale = 300f, EquatorTemperature = 30f,
            LowBandEdge = 1500000.0, MidBandEdge = 100000.0, RuggednessMin = 1f, RuggednessMax = 1f,
            RuggednessCentre = 0.5f,
            Continents = new ContinentConfig
            {
                Seed = 9u, MacroCells = 10, MicroCells = 400, ContinentCount = 3,
                DecayMin = 0.5f, DecayMax = 0.75f, ElevationFloor = 0.1f, ScoreNoise = 0.25f, LandFraction = 0.3f,
                WarpAmplitude = 0.5f, WarpWavelength = 2f, CoastWidth = 1f,
                LandHeight = 2000f, OceanDepth = 6000f, InteriorRise = 0.8f, DetailCutoff = 1000000.0,
            },
        };

        internal static HeightParams SyntheticRough =>
            HeightParams.Create(SyntheticContinents.Surface, 33, 0.0, 12, 200f, 0.5f, 2f, 42u);

        // Every test that measures a stress scene's terrain builds its parameters through
        // SceneParams, and its texels with the continent channel: a scene with continents has its
        // cutoff in the bound and in SceneHeights, and one without has neither. And SceneParams
        // itself applies the cutoff, checked on a continent scene that no scene file runs, so the
        // check holds before any stress scene has continents.
        [Test]
        public void SceneParametersCarryTheCutoff()
        {
            var synthetic = SceneParams(SyntheticContinents, SyntheticRough);
            Assert.Greater(synthetic.FirstOctave, 0, "SceneParams drops no octave on a continent scene");
            Assert.AreEqual(SyntheticRough.WithBands(SyntheticContinents.LowBandEdge, SyntheticContinents.MidBandEdge)
                                          .WithContinentalCutoff(1000000.0, 0f).FirstOctave, synthetic.FirstOctave);

            foreach (var m in StressMacros)
            {
                var  rough = RoughParams(m.Scene);
                var  hp    = SceneParams(m.Scene, rough);
                bool cut   = m.Continents.HasValue;
                Assert.AreEqual(cut, hp.FirstOctave > 0, $"{m.Scene}: the cutoff");
                Assert.AreEqual(cut, m.Params.HasContinents, $"{m.Scene}: the macro parameters' continents");
                Assert.AreEqual(m.Erosion.HasValue
                                    ? TerrainHeightFunction.MaxAbsHeight(hp, m.ErodedParams, m.ErodedMaxAbsBaseHeight)
                                    : TerrainHeightFunction.MaxAbsHeight(hp, m.Params),
                                MaxAbsHeight(m.Scene, rough), $"{m.Scene}: MaxAbsHeight is not built from SceneParams");
                if (!cut) continue;
                var w = new StressSceneTests.SceneHeights(m.Scene, m.Surface, rough);
                Assert.AreEqual(hp.FirstOctave, w.Height.FirstOctave, $"{m.Scene}: SceneHeights' cutoff");
                Assert.IsTrue(w.Macro.HasContinents, $"{m.Scene}: SceneHeights' continents");
            }
        }

        // The flattened world a stress scene's LOD measures in, as TerrainRoot builds it: its
        // LodVerticalScale over the height bound.
        internal static LodVertical Vertical(string scene, in HeightParams rough, float verticalScale)
            => LodVertical.Create(For(scene).Surface, verticalScale, MaxAbsHeight(scene, rough));

        // What the relief tests ask of the allowance against Demand. Not the 2x they ask against
        // the detail estimate alone: Demand already stacks two worst cases padded by half again
        // that never fall on one node. At M5b's relief the terrarium cleared this at 1.31x, where
        // 2x would have let it carry about 40 m of base height, and above about 220 m it failed.
        // Flattened by LodVerticalScale 0.2 it clears 2.0x, still the least of the four scenes,
        // and its base height could reach about 1,100 m before this fails.
        internal const double CombinedMargin = 1.25;

        // Each stress scene's macro layer is a valid configuration, small enough to build at
        // startup without thought, and carries continental octaves at its top level.
        [Test]
        public void StressSceneMacroLayersAreValidAndSmall()
        {
            foreach (var m in StressMacros)
            {
                var hp = HeightParams.Create(m.Surface, 33, 0.0, 20, 1000f, 0.5f, 2f, 42u);
                var p  = MacroParams.Create(m.Surface, hp, m.Config);
                double mb = p.Layout.TexelCount * 16.0 / (1024.0 * 1024.0);
                Assert.LessOrEqual(mb, 16.0, $"{m.Scene}: {p.Layout.TexelCount:N0} texels, {mb:F1} MB");
                Assert.AreEqual(3, p.K0, $"{m.Scene}: macro octave 0 at S0 gives octaves 0-3 at level 0");
                Assert.AreEqual(m.Surface.Type == SurfaceType.CubeSphere, p.HasLatitude, $"{m.Scene}: latitude term");
                Assert.AreEqual(0, p.UnreadLevels(m.MaxDepth), $"{m.Scene}: builds levels no depth reads");
                Assert.AreEqual(Biomes.MaxBiomes, p.BiomeCount, $"{m.Scene}: the shared biome table");
                // C7: the largest effective amplitude stays at or under 3.
                Assert.LessOrEqual(p.MaxAmplitude, 3f, $"{m.Scene}: largest band amplitude");

                // K10's two startup warnings, as assertions, measured as TerrainRoot measures them:
                // a micro cell at least four top-level texels across, and a cutoff no shorter than
                // four texels, so a retune trips a test and not only a log line.
                if (!m.Continents.HasValue) continue;
                var    cp    = ContinentParams.Create(m.Surface, p, m.Config);
                double s0    = RoughParams(m.Scene).S0;
                double texel = ContinentParams.TopTexelSpacing(s0, p.Layout);
                TestContext.WriteLine($"{m.Scene}: micro cells {cp.MicroCellTexels(s0, p.Layout):F1} texels across, " +
                                      $"cutoff {m.Continents.Value.DetailCutoff / texel:F1} texels");
                Assert.GreaterOrEqual(cp.MicroCellTexels(s0, p.Layout), 4.0, $"{m.Scene}: micro cells under four texels across");
                Assert.GreaterOrEqual(m.Continents.Value.DetailCutoff, 4.0 * texel, $"{m.Scene}: cutoff under four texels");
            }

            // The spacing is a texel's edge, a quad's diagonal S0 over root 2: Earth's 10,404 km
            // diagonal over 256 cells is a 28.7 km texel, and the 319 km micro cell 11.1 of them.
            var earth = For("EarthStress");
            Assert.AreEqual(RoughParams("EarthStress").S0 / math.SQRT2_DBL / 256.0,
                            ContinentParams.TopTexelSpacing(RoughParams("EarthStress").S0, earth.Params.Layout), 1e-6);
        }

        // The mountain belts (M5b): ruggedness scales the mountain band from 0.3 to 3.3, and the
        // map's centre decides how much of the world is which. At M5a's centre, 0.5, a quarter of
        // Earth saturated at the full 3.3 and over half was at twice the amplitude or more:
        // mountains everywhere, which read as no mountains. Centred on 0.2, about a ninth is full
        // belt, a third twice the amplitude or more, and nearly a third plains under 0.6. The
        // small planet's figures are Earth's because its map is Earth's, scaled: the macro noise
        // is sampled in units of S0. The ring's surface differs, and so do its figures, and the
        // narrow ring's, over 0.15 of the S0, differ again: fewer belts and more plains. Measured
        // on every fourth texel of the top level, where the map has all its octaves, and pinned
        // to a percentage point, so a change to the map's noise or centre is a decision rather
        // than a drift.
        [Test]
        public void MountainBeltsAreAMinority()
        {
            var expected = new[]
            {
                (scene: "EarthStress",       full: 11.3, belts: 35.1, plains: 30.1),
                (scene: "SmallPlanetStress", full: 11.3, belts: 35.1, plains: 30.1),
                (scene: "RingworldStress",   full: 16.0, belts: 41.7, plains: 23.7),
                (scene: "NarrowRingStress",  full:  3.8, belts: 17.8, plains: 45.9),
            };
            foreach (var (scene, full, belts, plains) in expected)
            {
                var m     = For(scene);
                var p     = m.Params;
                var roots = SurfaceMath.BuildRoots(m.Surface);
                int level = p.Layout.LevelCount - 1, n = p.Layout.Cells(level);
                int count = 0, inFull = 0, inBelts = 0, inPlains = 0;
                for (int q = 0; q < roots.Length; q++)
                for (int j = 0; j < n; j += 4)
                for (int i = 0; i < n; i += 4)
                {
                    // Ruggedness never reads the base height, so the continent value is immaterial.
                    float r         = MacroGenerator.Texel(m.Surface, roots[q], p, level, i, j, 0f).w;
                    float amplitude = 1f + math.lerp(p.RuggednessGainMin, p.RuggednessGainMax, r);
                    count++;
                    if (r >= 1f)           inFull++;
                    if (amplitude >= 2f)   inBelts++;
                    if (amplitude <  0.6f) inPlains++;
                }
                double f = 100.0 * inFull / count, b = 100.0 * inBelts / count, pl = 100.0 * inPlains / count;
                string got = $"{scene}: {f:F1}% full belt, {b:F1}% at twice the amplitude or more, {pl:F1}% plains";
                Assert.AreEqual(full,   f,  1.0, got);
                Assert.AreEqual(belts,  b,  1.0, got);
                Assert.AreEqual(plains, pl, 1.0, got);
                Assert.Less(b, 50.0, $"{got}: belts are no longer a minority");
            }
        }

        // C8: TerrainHeightFunction.HeightAtPoint, the body of TerrainRoot.HeightAt, gives the
        // height the deepest chunks draw at their own vertices -- MaxDepth's octaves and the macro
        // at MacroLevel(MaxDepth), read at the vertex's own (quad, u, v) -- including on shared
        // edges and corners, where Locate may name the other quad. A tool path, so to a
        // tolerance: Locate's u and v carry about 1e-15 of rounding.
        //
        // It calls the real function rather than rebuilding it, and checks that the two mistakes
        // that would still look plausible both fail here: reading level 0 instead of MaxDepth's,
        // and passing MacroSample.Zero. Either one misplaces the fly camera's ground by up to the
        // base height.
        //
        // With continents too, and their detail cutoff: the continent height is in the base-height
        // channel the read returns, and the cut octaves are the same in both.
        [Test]
        public void HeightAtReadsWhatTheChunksRead()
        {
            foreach (var s in new[] { SurfaceDef.CubeSphere(6371000.0), SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4) })
            foreach (bool continents in new[] { false, true })
            {
                var roots = SurfaceMath.BuildRoots(s);
                var hp    = Params(s).WithBands(s.Radius * 0.05, s.Radius * 0.0005);
                var c     = Config();
                c.Biomes = BiomeConfigs; c.BiomeSharpness = BiomeSharpness;
                c.TemperatureScale = TemperatureScale; c.MoistureScale = MoistureScale;
                c.RuggednessGainMin = -0.5f; c.RuggednessGainMax = 1.5f;
                if (continents)
                {
                    c.Continents = SmallContinents(s);
                    hp = hp.WithContinentalCutoff(s.Radius * 0.5, 0f);
                    Assert.Greater(hp.FirstOctave, 0, "the cutoff drops nothing");
                }
                var p     = MacroParams.Create(s, hp, c);
                var maps  = new ArrayTexels(MacroGenerator.Build(s, roots, p, ContinentParams.Create(s, p, c), out _));
                var rng   = new Unity.Mathematics.Random(77u);
                const int MaxDepth = 12, Samples = 2000;
                int top   = p.Layout.MacroLevel(MaxDepth);
                int oct   = hp.MaxOctave(MaxDepth);
                int span  = 1 << MaxDepth;
                int levelZeroCaught = 0, zeroCaught = 0, unmodulatedCaught = 0;

                for (int k = 0; k < Samples; k++)
                {
                    // A vertex of a MaxDepth chunk; every eighth on a quad's West edge, every
                    // sixteenth on its North-West corner.
                    int x = (k & 7) == 0 ? 0 : rng.NextInt(0, span);
                    int y = (k & 15) == 0 ? span - 1 : rng.NextInt(0, span);
                    int i = (k & 7) == 0 ? 0 : rng.NextInt(0, Resolution);
                    int j = (k & 15) == 0 ? Resolution - 1 : rng.NextInt(0, Resolution);
                    int q = rng.NextInt(0, roots.Length);
                    var node = new NodeId(q, MaxDepth, x, y);
                    ChunkGrid.VertexUV(node, i, j, Resolution, out double u, out double v);
                    double3 point = SurfaceMath.SurfacePoint(s, roots[q], u, v);

                    // What the sample job computes for this vertex: the read, modulated by the
                    // biomes and ruggedness there.
                    var   raw   = MacroSampler.Read(maps, p, q, top, u, v);
                    float chunk = TerrainHeightFunction.Height(point, oct, Biomes.Modulate(raw, p, out _), hp);
                    float tool  = TerrainHeightFunction.HeightAtPoint(s, point, MaxDepth, hp, p, maps);
                    double tolerance = 2.0 * (math.asfloat(math.asuint(math.abs(chunk)) + 1u) - math.abs(chunk));
                    Assert.AreEqual(chunk, tool, tolerance, $"{s.Type} {node} vertex ({i}, {j})");

                    float atLevelZero = TerrainHeightFunction.Height(point, oct,
                                            Biomes.Modulate(MacroSampler.Read(maps, p, q, 0, u, v), p, out _), hp);
                    float noMacro     = TerrainHeightFunction.Height(point, oct, MacroSample.Zero, hp);
                    float unmodulated = TerrainHeightFunction.Height(point, oct, raw, hp);
                    if (math.abs(atLevelZero - chunk) > tolerance) levelZeroCaught++;
                    if (math.abs(noMacro - chunk) > tolerance) zeroCaught++;
                    if (math.abs(unmodulated - chunk) > tolerance) unmodulatedCaught++;
                }

                Assert.Greater(levelZeroCaught, Samples * 9 / 10,
                    $"{s.Type}: reading level 0 would pass almost unnoticed; the test cannot catch it");
                Assert.Greater(zeroCaught, Samples * 9 / 10,
                    $"{s.Type}: dropping the macro would pass almost unnoticed; the test cannot catch it");
                Assert.Greater(unmodulatedCaught, Samples * 9 / 10,
                    $"{s.Type}: dropping the biome band gains would pass almost unnoticed");
            }
        }

        internal readonly struct ArrayTexels : IMacroTexels
        {
            readonly float4[] _texels;
            readonly float2[] _drainage;
            public ArrayTexels(float4[] texels, float2[] drainage = null) { _texels = texels; _drainage = drainage; }
            public float4 Texel(int index)    => _texels[index];
            public float2 Drainage(int index) => _drainage != null ? _drainage[index] : default;
        }

        static MacroConfig Config(int levelCount = LevelCount) => new MacroConfig
        {
            LevelCount = levelCount, Wavelength0 = 0.0, Persistence = 0.5f, BaseHeightScale = 3000f,
            EquatorTemperature = 30f, PoleTemperature = -30f, LapseRate = 6.5f, TemperatureNoise = 5f,
            MoistureNoise = 1.5f, RuggednessNoise = 1.5f, Seed = 7u,
        };

        static HeightParams Params(in SurfaceDef s, int resolution = Resolution) =>
            HeightParams.Create(s, resolution, 0.0, 20, 200f, 0.5f, 2f, 42u);

        // Independent values in every texel, then the edge copies. Stronger than the generator for
        // the edge rule, which must hold whatever fills the maps -- erosion will not keep two
        // faces' edge rows equal by accident -- and a smooth field would let a wrong copy pass
        // within rounding.
        static float4[] RandomMaps(in MacroLayout layout, in SurfaceDef s, bool applyEdgeCopies = true)
        {
            var texels = new float4[layout.TexelCount];
            var rng    = new Unity.Mathematics.Random(0x9E3779B9u);
            for (int t = 0; t < texels.Length; t++)
                texels[t] = rng.NextFloat4(-1000f, 1000f);
            if (applyEdgeCopies)
                foreach (int2 c in layout.EdgeCopies(s))
                    texels[c.x] = texels[c.y];
            return texels;
        }

        static void OnEdge(Edge e, double t, out double u, out double v)
        {
            switch (e)
            {
                case Edge.South: u = t;   v = 0.0; return;
                case Edge.East:  u = 1.0; v = t;   return;
                case Edge.North: u = t;   v = 1.0; return;
                default:         u = 0.0; v = t;   return;   // West
            }
        }

        static bool Same(in MacroSample a, in MacroSample b) =>
            a.BaseHeight == b.BaseHeight && a.Temperature == b.Temperature &&
            a.Moisture == b.Moisture && a.Ruggedness == b.Ruggedness;

        internal delegate MacroSample Sampler(float4[] texels, in MacroLayout layout, int quad, int level,
                                              double u, double v);

        internal static MacroSample Real(float4[] texels, in MacroLayout layout, int quad, int level,
                                         double u, double v)
            => MacroSampler.Sample(new ArrayTexels(texels), layout, quad, level, u, v);

        // The read as anyone would first write it: the textbook Catmull-Rom weights, summed left to
        // right. Algebraically identical to MacroSampler's; the mutation the edge test must catch.
        static MacroSample Textbook(float4[] texels, in MacroLayout layout, int quad, int level,
                                    double u, double v)
        {
            int n = layout.Cells(level);
            Weights(u * n, n, out int ci, out double4 wu);
            Weights(v * n, n, out int cj, out double4 wv);
            int w  = layout.Width(level);
            int at = layout.Index(quad, level, ci - 1, cj - 1);

            double4 m = 0.0;
            for (int r = 0; r < 4; r++)
            {
                double4 row = 0.0;
                for (int k = 0; k < 4; k++) row += wu[k] * (double4)texels[at + r * w + k];
                m += wv[r] * row;
            }
            return new MacroSample { BaseHeight = m.x, Temperature = (float)m.y,
                                     Moisture = (float)m.z, Ruggedness = (float)m.w };

            static void Weights(double x, int n, out int cell, out double4 wt)
            {
                cell = math.clamp((int)math.floor(x), 1 - MacroLayout.Border, n + MacroLayout.Border - 2);
                double t = x - cell, t2 = t * t, t3 = t2 * t;
                wt = new double4((-t3 + 2.0 * t2 - t) * 0.5, (3.0 * t3 - 5.0 * t2 + 2.0) * 0.5,
                                 (-3.0 * t3 + 4.0 * t2 + t) * 0.5, (t3 - t2) * 0.5);
            }
        }

        // Samples both sides of every link at chunk vertices along the edge, at every depth from
        // 0 to 24 -- on texels, between them, and in the last cell at each corner -- and counts
        // the points where the two disagree. Returns the count, and how many of those were on
        // reversed links.
        //
        // The deep depths are not optional. Up to a few depths past the top level the cell
        // fraction t has only a few bits, so every weight and every product of a weight and a
        // float texel is exact in double and the order of the sum cannot matter: the textbook
        // read passes there. A real chunk is deeper -- Earth's depth-19 vertices read level 3 with
        // a 16-bit t -- and the products round. Shallow depths take every vertex; deep ones the
        // four at each end of the edge and a fixed random sample between.
        //
        // Only a link that reverses needs them. On one that does not, both sides do the same
        // arithmetic on the same texels, so all a sample can catch there is a wrong copy, and two
        // depths past the top level already read every edge-row texel of every level.
        const int DeepestDepth  = 24;
        const int FullEdgeLimit = 1024;
        const int DeepSamples   = 128;

        internal static int EdgeDisagreements(in SurfaceDef s, in MacroLayout layout, float4[] texels,
                                              Sampler sample, out int onReversed, out int samples)
        {
            int bad = 0;
            onReversed = 0;
            samples = 0;
            var rng = new Unity.Mathematics.Random(12345u);
            for (int q = 0; q < s.QuadCount; q++)
            for (int e = 0; e < 4; e++)
            {
                if (!SurfaceMath.QuadEdgeLink(s, q, (Edge)e, out EdgeLink link)) continue;

                int deepest = link.Reversed ? DeepestDepth : layout.LevelCount + 1;
                for (int depth = 0; depth <= deepest; depth++)
                {
                    int level    = layout.MacroLevel(depth);
                    int vertices = Cells0 << depth;   // vertex intervals along the edge at this depth
                    bool all     = vertices <= FullEdgeLimit;
                    int  count   = all ? vertices + 1 : 8 + DeepSamples;
                    for (int c = 0; c < count; c++)
                    {
                        int k = all ? c
                              : c < 4 ? c
                              : c < 8 ? vertices - (c - 4)
                              : rng.NextInt(4, vertices - 3);

                        // Exactly the (u, v) ChunkGrid.VertexUV gives an edge vertex at this depth:
                        // k / (Cells0 2^d), a dyadic, and its reverse is exact.
                        double t = k / (double)vertices;
                        OnEdge((Edge)e, t, out double ua, out double va);
                        OnEdge(link.Edge, link.Reversed ? 1.0 - t : t, out double ub, out double vb);

                        var a = sample(texels, layout, q,         level, ua, va);
                        var b = sample(texels, layout, link.Face, level, ub, vb);
                        samples++;
                        if (Same(a, b)) continue;
                        bad++;
                        if (link.Reversed) onReversed++;
                    }
                }
            }
            return bad;
        }

        // C2: a macro sample at a shared edge point is bit-identical from both quads, at every
        // level, on every topology. The base height is kilometres, so anything less than
        // identical along a cube edge is a crack in the ground rather than in the detail.
        //
        // Two mutation checks keep the test honest, the way RebaseIsExactAfterAThousandShifts was
        // checked against position -= shift: the textbook read must fail it on the cube's eight
        // reversed links (and only there, because on a non-reversed link both sides do the same
        // arithmetic), and maps without the edge copies must fail it everywhere.
        [Test]
        public void MacroEdgesAgreeAcrossEveryLink()
        {
            foreach (var (name, s) in Surfaces)
            {
                var layout = new MacroLayout(Cells0, LevelCount, s.QuadCount);
                var maps   = RandomMaps(layout, s);

                int bad = EdgeDisagreements(s, layout, maps, Real, out _, out int samples);
                Assert.AreEqual(0, bad, $"{name}: {bad} of {samples} edge samples disagree across a link");

                int unshared = EdgeDisagreements(s, layout, RandomMaps(layout, s, applyEdgeCopies: false),
                                                 Real, out _, out _);
                Assert.Greater(unshared, samples / 2,
                    $"{name}: without the edge copies the two sides should disagree almost everywhere; " +
                    "the test is not reading the edge rows");

                int textbook = EdgeDisagreements(s, layout, maps, Textbook, out int reversed, out _);
                if (s.Type == SurfaceType.CubeSphere)
                    Assert.Greater(reversed, 0,
                        "the textbook weights summed left to right should disagree on a reversed cube " +
                        "link; if they do not, the test is not sampling enough of the eight");
                Assert.AreEqual(textbook, reversed,
                    $"{name}: the textbook read disagrees on a link that is not reversed, where both " +
                    "sides do the same arithmetic");
            }
        }

        // The same agreement on maps the real generator filled, Earth's sphere and the terrarium,
        // and with continents on both: the continent channel is derived by the pyramid, whose
        // copies give every shared edge row its owner's bits at every level before the generator
        // adds it.
        [Test]
        public void GeneratedMacroEdgesAgreeAcrossEveryLink()
        {
            foreach (var s in new[] { SurfaceDef.CubeSphere(6371000.0), SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4) })
            foreach (bool continents in new[] { false, true })
            {
                var c = Config();
                if (continents) c.Continents = SmallContinents(s);
                var p    = MacroParams.Create(s, Params(s), c);
                var maps = MacroGenerator.Build(s, SurfaceMath.BuildRoots(s), p, ContinentParams.Create(s, p, c), out _);

                int bad = EdgeDisagreements(s, p.Layout, maps, Real, out _, out int samples);
                Assert.AreEqual(0, bad, $"{s.Type}{(continents ? " with continents" : "")}: {bad} of {samples} edge samples disagree");
            }
        }

        // Continents sized for this file's small layouts: about five top-level texels to a micro
        // cell on Earth's sphere and seven in the terrarium.
        internal static ContinentConfig SmallContinents(in SurfaceDef s)
            => s.Type == SurfaceType.CubeSphere ? ContinentMapTests.SmallConfig(8, 60, 2)
                                                : ContinentMapTests.SmallConfig(12, 300, 3);

        // Ground rule 1 of the continents: with none, MacroGenerator.Texel is M5b's to the bit on
        // every stress scene's configuration, whatever continent value it is handed. Against a frozen
        // copy of M5b's Texel, as HeightIsBitIdenticalWithoutAMacroLayer freezes M4's height.
        [Test]
        public void MacroMapsMatchM5bWithoutContinents()
        {
            var rng = new Unity.Mathematics.Random(0x5B5Bu);
            foreach (var m in StressMacros)
            {
                var c = m.Config; c.Continents = null;
                var p = MacroParams.Create(m.Surface, HeightParams.Create(m.Surface, 33, 0.0, 20, 1f, 0.5f, 2f, 42u), c);
                Assert.IsFalse(p.HasContinents, m.Scene);
                var roots = SurfaceMath.BuildRoots(m.Surface);
                for (int k = 0; k < 3000; k++)
                {
                    int t = rng.NextInt(0, p.Layout.TexelCount);
                    p.Layout.Decode(t, out int q, out int l, out int i, out int j);
                    float4 now = MacroGenerator.Texel(m.Surface, roots[q], p, l, i, j, rng.NextFloat(-9000f, 9000f));
                    float4 m5b = M5bTexel(m.Surface, roots[q], p, l, i, j);
                    Assert.IsTrue(math.all(math.asuint(now) == math.asuint(m5b)), $"{m.Scene}: texel {t} is {now}, M5b {m5b}");
                }
            }
        }

        // MacroGenerator.Texel exactly as M5b shipped it. Not to be tidied or kept in step with the
        // real one: being frozen is the point.
        static float4 M5bTexel(in SurfaceDef s, in RootQuad q, in MacroParams p, int level, int i, int j)
        {
            double  n  = p.Layout.Cells(level);
            double3 pt = SurfaceMath.SurfacePoint(s, q, i / n, j / n);
            double3 x  = pt / p.Wavelength0;

            int  octaves = math.max(0, p.MaxOctave(level) + 1);
            uint seed    = p.Seed + 0x6D2B79F5u;

            double baseHeight = p.BaseHeightScale * LatticeNoise.Fbm(x, octaves, p.Persistence, 2f, seed);

            double temperature = p.EquatorTemperature
                               + p.TemperatureNoise * LatticeNoise.Fbm(x, octaves, p.Persistence, 2f, seed + 0x00010000u)
                               - p.LapseRate * math.max(baseHeight, 0.0);
            if (p.HasLatitude)
            {
                double sinLatitude = pt.y / math.length(pt);
                temperature += (p.PoleTemperature - p.EquatorTemperature) * sinLatitude * sinLatitude;
            }

            double moisture   = math.saturate(0.5 + p.MoistureNoise   * LatticeNoise.Fbm(x, octaves, p.Persistence, 2f, seed + 2u * 0x00010000u));
            double ruggedness = math.saturate(p.RuggednessCentre + p.RuggednessNoise * LatticeNoise.Fbm(x, octaves, p.Persistence, 2f, seed + 3u * 0x00010000u));

            return new float4((float)baseHeight, (float)temperature, (float)moisture, (float)ruggedness);
        }

        // Every texel on a shared edge row is a copy of a texel nothing copies into, so the copies
        // can be applied once, in any order. If a source were itself a copy, the result would
        // depend on the order MacroMaps happened to apply them in.
        [Test]
        public void EdgeCopiesReadOnlyOwnedTexels()
        {
            foreach (var (name, s) in Surfaces)
            {
                var layout = new MacroLayout(Cells0, LevelCount, s.QuadCount);
                var copies = layout.EdgeCopies(s);
                var dst    = new System.Collections.Generic.HashSet<int>();
                foreach (int2 c in copies)
                    Assert.IsTrue(dst.Add(c.x), $"{name}: texel {c.x} is copied into twice");
                foreach (int2 c in copies)
                    Assert.IsFalse(dst.Contains(c.y), $"{name}: texel {c.y} is a source and a copy");
            }
        }

        // The corner rule: three quads at a cube corner, four at the terrarium's, two at a ring's
        // open end, and all of them read the lowest-indexed quad's corner texel.
        [Test]
        public void CornersComeFromTheLowestQuad()
        {
            var cube = SurfaceDef.CubeSphere(1.0);
            var cl   = new MacroLayout(Cells0, 1, 6);
            int n    = Cells0;
            var sources = new System.Collections.Generic.Dictionary<(int, int, int), int>();
            for (int q = 0; q < 6; q++)
            foreach (var (i, j) in new[] { (0, 0), (n, 0), (0, n), (n, n) })
            {
                cl.EdgeSource(cube, q, 0, i, j, out int sq, out int si, out int sj);
                Assert.IsTrue((si == 0 || si == n) && (sj == 0 || sj == n), "a corner's source is a corner");
                Assert.LessOrEqual(sq, q);
                // The source is its own source: a corner nothing copies into.
                Assert.IsFalse(cl.EdgeSource(cube, sq, 0, si, sj, out _, out _, out _));
                sources.TryGetValue((sq, si, sj), out int readers);
                sources[(sq, si, sj)] = readers + 1;
            }
            // Eight cube corners, each read by its three faces from one texel.
            Assert.AreEqual(8, sources.Count);
            foreach (var kv in sources) Assert.AreEqual(3, kv.Value, $"corner source {kv.Key}");

            var terrarium = SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4);
            var tl        = new MacroLayout(Cells0, 1, terrarium.QuadCount);
            // Quad (col 1, row 1) = 5, its South-West corner: shared with (0, 0) = 0, (0, 1) = 1
            // and (1, 0) = 4. Quad 0 owns it, at its own North-East corner.
            Assert.IsTrue(tl.EdgeSource(terrarium, 5, 0, 0, 0, out int tq, out int ti, out int tj));
            Assert.AreEqual((0, n, n), (tq, ti, tj));

            var ring = SurfaceDef.Cylinder(1592750.0, 1250000.0, 8, 1);
            var rl   = new MacroLayout(Cells0, 1, ring.QuadCount);
            // Quad 7's South-East corner is quad 0's South-West, across the closing seam.
            Assert.IsTrue(rl.EdgeSource(ring, 7, 0, n, 0, out int rq, out int ri, out int rj));
            Assert.AreEqual((0, 0, 0), (rq, ri, rj));
            Assert.IsFalse(rl.EdgeSource(ring, 0, 0, 0, 0, out _, out _, out _));
        }

        // C1: at every depth up to the top level a chunk's vertices land on texels, and the read
        // returns the stored texel exactly -- weights (0, 1, 0, 0) on both axes, no interpolation.
        // Border vertices included, and the coarse job's whole grid, which reads the parent's
        // level: at depth 1 and below its vertices are the parent's and land one texel out at
        // most; at depth 0 it reads level 0 too, so its border lands two texels out. That read is
        // drawn, because roots morph towards their coarse targets from far out, and with a stored
        // border of two it extrapolated instead of landing on a texel.
        [Test]
        public void MacroReadIsExactOnTexels()
        {
            var s     = SurfaceDef.CubeSphere(6371000.0);
            var p     = MacroParams.Create(s, Params(s), Config());
            var maps  = MacroGenerator.Build(s, SurfaceMath.BuildRoots(s), p);
            var texel = new ArrayTexels(maps);
            int cg    = ChunkMeshLayout.CoarseGridSize(Resolution);

            for (int q = 0; q < 6; q++)
            for (int depth = 0; depth < LevelCount; depth++)
            {
                int span = 1 << depth;
                for (int x = 0; x < span; x++)
                for (int y = 0; y < span; y++)
                {
                    var node = new NodeId(q, depth, x, y);
                    for (int j = -1; j <= Resolution; j++)
                    for (int i = -1; i <= Resolution; i++)
                    {
                        ChunkGrid.VertexUV(node, i, j, Resolution, out double u, out double v);
                        ExpectTexel(maps, texel, p.Layout, q, p.Layout.MacroLevel(depth),
                                    x * Cells0 + i, y * Cells0 + j, u, v, node, i, j);
                    }

                    // The coarse grid at the parent's level: vertex index (X Cells0 + i) at this
                    // depth is texel (X Cells0 + i) / 2^(depth - level) there.
                    int parent = p.Layout.MacroLevel(depth - 1);
                    int shift  = depth - parent;
                    for (int cj = 0; cj < cg; cj++)
                    for (int ci = 0; ci < cg; ci++)
                    {
                        int i = ChunkMeshLayout.CoarseToFine(ci), j = ChunkMeshLayout.CoarseToFine(cj);
                        ChunkGrid.VertexUV(node, i, j, Resolution, out double u, out double v);
                        ExpectTexel(maps, texel, p.Layout, q, parent,
                                    (x * Cells0 + i) >> shift, (y * Cells0 + j) >> shift, u, v, node, i, j);
                    }
                }
            }
        }

        // Between texels the read is NOT bounded by them: Catmull-Rom's outer weights are
        // negative, and against texels whose signs follow the weights' a read at a cell's centre
        // reaches exactly MacroSampler.MaxOvershoot of the largest texel. That is the factor
        // TerrainHeightFunction.MaxAbsHeight puts on the base height, so LodVertical's envelope
        // and the skirts' cap are bounds rather than typical cases; without it the base height
        // could read half again past the envelope. Random texels and reads stay within it.
        [Test]
        public void MacroReadOvershootIsBoundedAndAttained()
        {
            var layout = new MacroLayout(Cells0, 1, 1);
            var texels = new float4[layout.TexelCount];
            int c      = Cells0 / 2;
            // At t = 0.5 the weights are (-1/16, 9/16, 9/16, -1/16) on each axis.
            int[] sign = { -1, 1, 1, -1 };
            for (int a = 0; a < 4; a++)
            for (int b = 0; b < 4; b++)
                texels[layout.Index(0, 0, c - 1 + a, c - 1 + b)] = new float4(sign[a] * sign[b], 0f, 0f, 0f);

            double centre = (c + 0.5) / Cells0;
            double worst  = MacroSampler.Sample(new ArrayTexels(texels), layout, 0, 0, centre, centre).BaseHeight;
            Assert.AreEqual(MacroSampler.MaxOvershoot, worst, 1e-12, "the adversarial read at a cell centre");

            var rng = new Unity.Mathematics.Random(0x0E5u);
            for (int k = 0; k < texels.Length; k++) texels[k] = new float4(rng.NextFloat(-1f, 1f), 0f, 0f, 0f);
            for (int t = 0; t < 20000; t++)
            {
                double read = MacroSampler.Sample(new ArrayTexels(texels), layout, 0, 0, rng.NextDouble(), rng.NextDouble()).BaseHeight;
                Assert.LessOrEqual(math.abs(read), MacroSampler.MaxOvershoot + 1e-12, "a read past the overshoot bound");
            }

            // And the height bound carries it: with no detail noise, MaxAbsHeight is the texel
            // bound -- BaseHeightScale times the macro octaves' geometric sum -- times the factor.
            var s       = SurfaceDef.CubeSphere(6371000.0);
            var hp      = HeightParams.Create(s, Resolution, 0.0, 20, 0f, 0.5f, 2f, 42u);
            var macro   = MacroParams.Create(s, hp, Config());
            double sum  = 0.0, amp = 1.0;
            for (int j = 0; j <= macro.MaxOctave(macro.Layout.LevelCount - 1); j++) { sum += amp; amp *= macro.Persistence; }
            Assert.AreEqual(macro.BaseHeightScale * sum * MacroSampler.MaxOvershoot,
                            TerrainHeightFunction.MaxAbsHeight(hp, macro), 1e-9,
                            "MaxAbsHeight's base height share is not the texel bound times the overshoot");
        }

        static void ExpectTexel(float4[] maps, ArrayTexels texel, in MacroLayout layout, int q,
                                int level, int ti, int tj, double u, double v, NodeId node, int i, int j)
        {
            float4 want = maps[layout.Index(q, level, ti, tj)];
            var    got  = MacroSampler.Sample(texel, layout, q, level, u, v);
            Assert.IsTrue(got.BaseHeight == want.x && got.Temperature == want.y &&
                          got.Moisture == want.z && got.Ruggedness == want.w,
                $"{node} vertex ({i}, {j}) at level {level}: read ({got.BaseHeight}, {got.Temperature}, " +
                $"{got.Moisture}, {got.Ruggedness}) where texel ({ti}, {tj}) holds {want}");
        }

        // C1: the level is depth clamped to the pyramid, a function of depth alone, so same-depth
        // neighbours read the same level whatever their measured sizes. Up to the top level its
        // texel spacing is exactly the depth's vertex spacing; it is never finer, which is what
        // keeps a sample job's one-vertex border within the one-texel reach the stored border
        // allows for (C2).
        [Test]
        public void MacroLevelIsDepthOnly()
        {
            foreach (int levels in new[] { 1, 3, 4, 6 })
            {
                var layout = new MacroLayout(32, levels, 6);
                for (int depth = -1; depth <= 30; depth++)
                {
                    int level = layout.MacroLevel(depth);
                    Assert.AreEqual(math.clamp(depth, 0, levels - 1), level, $"{levels} levels, depth {depth}");
                    Assert.LessOrEqual(level, math.max(depth, 0), "a level finer than its depth's vertex spacing");
                    if (depth >= 0 && depth < levels)
                        Assert.AreEqual(32 << depth, layout.Cells(level),
                            $"{levels} levels, depth {depth}: texel spacing differs from vertex spacing");
                }
            }
        }

        // C1: each level carries every macro octave of at least four texel spacings, and no
        // finer one. On Earth at resolution 33 with octave 0 at S0 the ratio is exactly 8, so a
        // limit read through log2 rather than the exponent would be one octave short here.
        [Test]
        public void MacroLevelsAreBandLimited()
        {
            var earth = SurfaceDef.CubeSphere(6371000.0);
            var cases = new[]
            {
                (s: earth, r: 33, lambda: 0.0),
                (s: earth, r: 65, lambda: 0.0),
                (s: earth, r: 33, lambda: 2.0e6),
                (s: SurfaceDef.Cylinder(10000.0, 30000.0, 8, 4), r: 33, lambda: 12345.0),
                (s: SurfaceDef.PlaneGrid(1000.0, 2, 2), r: 9, lambda: 300.0),
            };
            foreach (var (s, r, lambda) in cases)
            {
                var hp = Params(s, r);
                var c  = Config(4);
                c.Wavelength0 = lambda;
                var p  = MacroParams.Create(s, hp, c);

                for (int level = 0; level < 4; level++)
                {
                    int    k       = p.MaxOctave(level);
                    double spacing = hp.S0 / p.Layout.Cells(level);
                    string at      = $"{s.Type} R {r} lambda {lambda} level {level} octave {k}";
                    Assert.GreaterOrEqual(p.Wavelength0 / math.pow(2.0, k), 4.0 * spacing,
                        $"{at}: the finest octave is shorter than four texels");
                    Assert.Less(p.Wavelength0 / math.pow(2.0, k + 1), 4.0 * spacing,
                        $"{at}: a further octave would still fit, so the level is band-limited short");
                }
            }

            var earth33 = MacroParams.Create(earth, Params(earth, 33), Config(4));
            Assert.AreEqual(new[] { 3, 4, 5, 6 },
                new[] { earth33.MaxOctave(0), earth33.MaxOctave(1), earth33.MaxOctave(2), earth33.MaxOctave(3) },
                "Earth at resolution 33: octaves 0-3 at level 0 up to 0-6 at level 3, as the plan's table says");
        }

        // Configuration is validated once, in MacroParams.Create.
        [Test]
        public void MacroParamsRefuseUnusableConfigurations()
        {
            var s  = SurfaceDef.CubeSphere(6371000.0);
            var hp = Params(s, 33);

            var c = Config(); c.LevelCount = 0;
            Assert.Throws<ArgumentOutOfRangeException>(() => MacroParams.Create(s, hp, c), "no levels");
            c = Config(); c.LevelCount = 20;
            Assert.Throws<ArgumentOutOfRangeException>(() => MacroParams.Create(s, hp, c), "too many texels");
            c = Config(); c.Persistence = 0f;
            Assert.Throws<ArgumentOutOfRangeException>(() => MacroParams.Create(s, hp, c), "persistence 0");
            c = Config(); c.BaseHeightScale = float.NaN;
            Assert.Throws<ArgumentOutOfRangeException>(() => MacroParams.Create(s, hp, c), "NaN amplitude");
            c = Config(); c.Wavelength0 = -1.0;
            Assert.Throws<ArgumentOutOfRangeException>(() => MacroParams.Create(s, hp, c), "negative wavelength");
            c = Config(); c.Wavelength0 = 1.0;   // 1 m against a 10,000 km face: every level flat
            Assert.Throws<ArgumentOutOfRangeException>(() => MacroParams.Create(s, hp, c), "flat everywhere");

            Assert.IsFalse(MacroParams.Disabled.Enabled);
            Assert.IsTrue(MacroParams.Create(s, hp, Config()).Enabled);

            // Levels past MaxDepth are built and never read; TerrainRoot warns on them.
            var six = Config(); six.LevelCount = 6;
            var p6  = MacroParams.Create(s, hp, six);
            Assert.AreEqual(2, p6.UnreadLevels(3), "MaxDepth 3 reads levels 0-3 of 0-5");
            Assert.AreEqual(0, p6.UnreadLevels(5));
            Assert.AreEqual(0, p6.UnreadLevels(19));
        }

        // The layout addresses every texel exactly once: Decode inverts Index across every
        // quad, level and border texel, and the levels tile the array with no gap.
        [Test]
        public void LayoutDecodeInvertsIndex()
        {
            var layout = new MacroLayout(Cells0, LevelCount, 6);
            var seen   = new bool[layout.TexelCount];
            for (int q = 0; q < 6; q++)
            for (int l = 0; l < LevelCount; l++)
            {
                int n = layout.Cells(l);
                for (int j = -MacroLayout.Border; j <= n + MacroLayout.Border; j++)
                for (int i = -MacroLayout.Border; i <= n + MacroLayout.Border; i++)
                {
                    int at = layout.Index(q, l, i, j);
                    Assert.IsFalse(seen[at], $"texel ({q}, {l}, {i}, {j}) shares index {at}");
                    seen[at] = true;
                    layout.Decode(at, out int dq, out int dl, out int di, out int dj);
                    Assert.AreEqual((q, l, i, j), (dq, dl, di, dj));
                }
            }
            Assert.IsTrue(Array.TrueForAll(seen, b => b), "the layout leaves texels nothing addresses");
        }
    }
}
