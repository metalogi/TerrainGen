using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;
using Sonoma.Systems.Configuration;

// The continent designer's Regenerate (Docs/Continents_Plan.md, the continent designer amendment):
// a ContinentSettings asset's two design settings, TargetLand and Fragmentation, turned into its
// low-level fields. ContinentDesign.Derive sets the count, the macro cells and the separation, and
// LandFraction is calibrated by building the open scene's maps as TerrainRoot does -- continents,
// erosion and all, in Burst -- and measuring the final land at MaxDepth, as the macro map export's
// sea image does, until it is within half a point of the target. In edit mode, a few seconds a
// build; the inspector (ContinentSettingsEditor) shows the last result.
static class ContinentDesigner
{
    const int    Stride    = 2;       // of the top level's texels, while calibrating
    const int    MaxBuilds = 4;
    const double Tolerance = 0.005;

    // The stress scenes' continents, whose fields the tests copy and whose measured figures they
    // pin (MacroTests.StressMacros): regenerating one is a retune, and the suite fails until the
    // measuring tools are rerun and the figures re-pinned.
    internal static readonly string[] PinnedAssets =
    {
        "Assets/Settings/EarthContinents.asset", "Assets/Settings/SmallPlanetContinents.asset",
        "Assets/Settings/RingworldContinents.asset", "Assets/Settings/NarrowRingContinents.asset",
    };

    internal sealed class Result
    {
        public Texture2D      Preview;
        public ContinentStats Map;
        public double[]       Pieces;          // each continent's share of the land, largest first
        public int            LandMicroCells;
        public double         FinalLand, Agreement, Target;
        public int            Builds;
        public bool           Converged, Written;
        public double         BuildMilliseconds, MaxCut, Envelope;
        public bool           Eroded;
        public string         Error;
        public readonly List<string> Tries = new List<string>();
    }

    // The last result for each asset, so the preview outlives the inspector that showed it.
    internal static readonly Dictionary<int, Result> Last = new Dictionary<int, Result>();

    // The open scene's TerrainRoot whose macro layer uses this asset, or null.
    internal static TerrainRoot FindRoot(ContinentSettings asset)
    {
        foreach (var r in UnityEngine.Object.FindObjectsByType<TerrainRoot>(FindObjectsSortMode.None))
            if (r.Settings != null && r.Settings.Macro != null && r.Settings.Macro.Continents == asset)
                return r;
        return null;
    }

    // One build and its measurement.
    struct Try
    {
        public ContinentConfig Config;
        public Result          Result;
    }

    internal static Result Regenerate(TerrainRoot root, ContinentSettings asset)
    {
        var t = root.Settings;
        MacroMapExport.SceneSetup(root, t, out SurfaceDef s, out RootQuad[] roots, out HeightParams hp, out MacroConfig config);
        ContinentConfig current = config.Continents.Value;
        float target = asset.TargetLand, fragmentation = asset.Fragmentation;

        // The last calibration's offset between the map and the final land is the best first
        // guess; with none, two points, about what the stress scenes needed.
        float offset = asset.MeasuredLand >= 0f ? asset.LandFraction - asset.MeasuredLand : 0.02f;
        float map    = math.clamp(target + offset, 0.01f, 0.99f);
        float map0   = float.NaN, final0 = float.NaN;

        Try? best = null;
        var log = new List<string>();
        try
        {
            for (int b = 0; b < MaxBuilds; b++)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Regenerate continents",
                        $"Build {b + 1} of at most {MaxBuilds}: map land {map:P1}", (float)b / MaxBuilds))
                    return Failed(asset, "Cancelled; the asset is unchanged.", log);

                var k = ContinentDesign.Derive(current, fragmentation, map);
                config.Continents = k;
                Try one = Measure(s, roots, hp, config, t, root, k, target);
                one.Result.Builds = b + 1;
                log.Add($"map {map:P1} -> final {one.Result.FinalLand:P2}");
                if (best == null || math.abs(one.Result.FinalLand - target) < math.abs(best.Value.Result.FinalLand - target))
                {
                    if (best != null) UnityEngine.Object.DestroyImmediate(best.Value.Result.Preview);
                    best = one;
                }
                else UnityEngine.Object.DestroyImmediate(one.Result.Preview);

                float final = (float)one.Result.FinalLand;
                if (math.abs(final - target) <= Tolerance) break;
                float next = float.IsNaN(map0) ? ContinentDesign.NextMapLand(target, map, final)
                                               : ContinentDesign.NextMapLand(target, map0, final0, map, final);
                map0 = map; final0 = final; map = next;
            }
        }
        catch (InvalidOperationException e)
        {
            // Stage A's or B's plateau check: the seeds landed too sparse for the coast width.
            if (best != null) UnityEngine.Object.DestroyImmediate(best.Value.Result.Preview);
            return Failed(asset, e.Message, log);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        Try won = best.Value;
        Result r = won.Result;
        r.Tries.AddRange(log);
        r.Converged = math.abs(r.FinalLand - target) <= Tolerance;

        Undo.RecordObject(asset, "Regenerate continents");
        asset.LandFraction             = won.Config.LandFraction;
        asset.ContinentCount           = won.Config.ContinentCount;
        asset.MacroCells               = won.Config.MacroCells;
        asset.Separation               = won.Config.Separation;
        asset.RegeneratedTargetLand    = target;
        asset.RegeneratedFragmentation = fragmentation;
        asset.MeasuredLand             = (float)r.FinalLand;
        EditorUtility.SetDirty(asset);
        r.Written = true;

        Store(asset, r);
        Debug.Log($"[ContinentDesigner] {asset.name}: {r.Map.Continents} continents (largest {r.Map.LargestContinent:P0} " +
                  $"of the land), {r.Map.Islands} islands; LandFraction {asset.LandFraction:F3} gives {r.FinalLand:P2} land " +
                  $"against {target:P1}, agreement {r.Agreement:P1}; {asset.ContinentCount} centres, {asset.MacroCells} " +
                  $"macro cells, separation {asset.Separation}. Builds: {string.Join("; ", r.Tries)}.");
        return r;
    }

    static Result Failed(ContinentSettings asset, string error, List<string> log)
    {
        var r = new Result { Error = error, Target = asset.TargetLand };
        r.Tries.AddRange(log);
        Store(asset, r);
        return r;
    }

    static void Store(ContinentSettings asset, Result r)
    {
        int id = asset.GetInstanceID();
        if (Last.TryGetValue(id, out Result old) && old.Preview != null && old.Preview != r.Preview)
            UnityEngine.Object.DestroyImmediate(old.Preview);
        Last[id] = r;
    }

    // Builds the maps as TerrainRoot.BuildMacro does and measures the sea at every Stride-th
    // texel: the final land by area, and the share of the area where the final terrain is on the
    // continent map's side of sea level (ContinentTests' agreement, over texels rather than random
    // points).
    static Try Measure(in SurfaceDef s, RootQuad[] roots, in HeightParams hp, in MacroConfig config,
                       TerrainSettings t, TerrainRoot root, in ContinentConfig k, float target)
    {
        var mp = MacroParams.Create(s, hp, config);
        var cp = ContinentParams.Create(s, mp, config);
        var ep = ErosionParams.Create(s, hp, mp, config);

        float4[]     texels;
        float2[]     drainage;
        ContinentMap map;
        var r = new Result { Target = target };
        using (var maps = MacroMaps.Build(s, roots, mp, cp, hp, ep))
        {
            texels   = maps.Texels.ToArray();
            drainage = maps.Drainage.ToArray();
            map      = maps.Continents;
            r.BuildMilliseconds = maps.BuildMilliseconds;
            r.Envelope          = maps.MaxAbsHeight(hp);
            r.Eroded            = maps.Erosion != null;
            r.MaxCut            = maps.Erosion != null ? maps.Erosion.MaxCut : 0.0;
        }

        MacroMapExport.Sea sea = MacroMapExport.SampleSea(s, roots, hp, mp, texels, drainage, t.MaxDepth,
                                                          t.SeaLevel, Stride, null);
        r.FinalLand = sea.LandShare;
        r.Map       = map.Stats;

        double land = 0.0;
        foreach (double a in map.LandPieces) land += a;
        int shown = 0;
        while (shown < map.LandPieces.Length && map.LandPieces[shown] >= ContinentMap.ContinentShare * land) shown++;
        r.Pieces = new double[shown];
        for (int i = 0; i < shown; i++) r.Pieces[i] = map.LandPieces[i] / land;
        r.LandMicroCells = map.Stats.LandCells;

        // Agreement, each texel weighted by its area as the sea's land share is.
        int full = mp.Layout.Cells(mp.Layout.LevelCount - 1);
        double[] area = ContinentMap.AreaTable(s, roots[0], full);
        var tables = new ArrayContinentTables(map);
        double agree = 0.0, total = 0.0;
        for (int y = 0; y < sea.Height; y++)
        for (int x = 0; x < sea.Width; x++)
        {
            double h = sea.Heights[y * sea.Width + x];
            if (double.IsNaN(h) || !MacroMapExport.PixelPoint(s, roots, sea, x, y, out double3 p)) continue;
            double a = area[(y % sea.Tile) * Stride * (full + 1) + (x % sea.Tile) * Stride];
            total += a;
            if ((ContinentField.Sample(p, tables, cp).S > 0.0) == (h > sea.Level)) agree += a;
        }
        r.Agreement = total > 0.0 ? agree / total : 0.0;

        var tex = new Texture2D(sea.Width, sea.Height, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
        };
        tex.SetPixels32(MacroMapExport.SeaPixels(sea, root.OceanMaterial, ContinentParams.TopTexelSpacing(hp.S0, mp.Layout)));
        tex.Apply();
        r.Preview = tex;
        return new Try { Config = k, Result = r };
    }
}
