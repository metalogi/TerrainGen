using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
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

    // A hash of everything the calibration's measurement reads: the surface, the height
    // parameters and the macro configuration SceneSetup makes -- the continents, erosion and
    // biomes among it -- the depth the land is sampled at and the sea level. Regenerate stores it
    // and the inspector compares it, so a change to any of them, in this asset or the scene's
    // other settings, shows the measured land as stale. Null when the settings do not resolve.
    internal static string Inputs(TerrainRoot root)
    {
        var t = root.Settings;
        try
        {
            MacroMapExport.SceneSetup(root, t, out SurfaceDef s, out _, out HeightParams hp, out MacroConfig config);
            var sb = new StringBuilder();
            Dump(sb, s, 0);
            Dump(sb, hp, 0);
            Dump(sb, config, 0);
            Dump(sb, t.MaxDepth, 0);
            Dump(sb, t.SeaLevel, 0);
            sb.Append(Stride);
            return Hash128.Compute(sb.ToString()).ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Every field, recursively and in declaration order; floating point by its round-trip form,
    // since Mono's default ToString drops digits.
    static void Dump(StringBuilder sb, object o, int depth)
    {
        if (o == null) { sb.Append("null;"); return; }
        if (depth > 8) throw new InvalidOperationException("ContinentDesigner: the configuration nests too deeply to hash.");
        switch (o)
        {
            case float f:  sb.Append(f.ToString("R", CultureInfo.InvariantCulture)).Append(';'); return;
            case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)).Append(';'); return;
            case string s: sb.Append('"').Append(s).Append("\";"); return;
            case Array a:
                sb.Append('[');
                foreach (object e in a) Dump(sb, e, depth + 1);
                sb.Append("];");
                return;
        }
        Type type = o.GetType();
        if (type.IsPrimitive || type.IsEnum)
        {
            sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)).Append(';');
            return;
        }
        sb.Append('{');
        foreach (FieldInfo f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                    .OrderBy(f => f.MetadataToken))
        {
            sb.Append(f.Name).Append('=');
            Dump(sb, f.GetValue(o), depth + 1);
        }
        sb.Append("};");
    }

    // One build's land, and what the winner's result is finished from. The agreement, the pieces
    // and the preview are worked out for the winner alone, so a losing try costs its build and
    // the land share, and leaves no texture behind.
    sealed class Build
    {
        public ContinentConfig    Config;
        public MacroParams        Macro;
        public ContinentParams    Continent;
        public ContinentMap       Map;
        public MacroMapExport.Sea Sea;
        public double             FinalLand, Milliseconds, Envelope, MaxCut;
        public bool               Eroded;
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

        Build best = null;
        int builds = 0;
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
                Build one = Measure(s, roots, hp, config, t, k);
                builds = b + 1;
                log.Add($"map {map:P1} -> final {one.FinalLand:P2}");
                if (best == null || math.abs(one.FinalLand - target) < math.abs(best.FinalLand - target))
                    best = one;

                float final = (float)one.FinalLand;
                if (math.abs(final - target) <= Tolerance) break;
                float next = float.IsNaN(map0) ? ContinentDesign.NextMapLand(target, map, final)
                                               : ContinentDesign.NextMapLand(target, map0, final0, map, final);
                map0 = map; final0 = final; map = next;
            }
        }
        catch (InvalidOperationException e)
        {
            // Stage A's or B's plateau check: the seeds landed too sparse for the coast width.
            return Failed(asset, e.Message, log);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        Result r = Finish(best, s, roots, hp, root, target);
        r.Builds    = builds;
        r.Converged = math.abs(r.FinalLand - target) <= Tolerance;
        r.Tries.AddRange(log);

        Undo.RecordObject(asset, "Regenerate continents");
        asset.LandFraction             = best.Config.LandFraction;
        asset.ContinentCount           = best.Config.ContinentCount;
        asset.MacroCells               = best.Config.MacroCells;
        asset.Separation               = best.Config.Separation;
        asset.RegeneratedTargetLand    = target;
        asset.RegeneratedFragmentation = fragmentation;
        asset.MeasuredLand             = (float)r.FinalLand;
        asset.RegeneratedInputs        = Inputs(root) ?? "";
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

    // Builds the maps as TerrainRoot.BuildMacro does and measures the final land by area at every
    // Stride-th texel.
    static Build Measure(in SurfaceDef s, RootQuad[] roots, in HeightParams hp, in MacroConfig config,
                         TerrainSettings t, in ContinentConfig k)
    {
        var b = new Build { Config = k };
        b.Macro     = MacroParams.Create(s, hp, config);
        b.Continent = ContinentParams.Create(s, b.Macro, config);
        var ep = ErosionParams.Create(s, hp, b.Macro, config);

        float4[] texels;
        float2[] drainage;
        using (var maps = MacroMaps.Build(s, roots, b.Macro, b.Continent, hp, ep))
        {
            texels         = maps.Texels.ToArray();
            drainage       = maps.Drainage.ToArray();
            b.Map          = maps.Continents;
            b.Milliseconds = maps.BuildMilliseconds;
            b.Envelope     = maps.MaxAbsHeight(hp);
            b.Eroded       = maps.Erosion != null;
            b.MaxCut       = maps.Erosion != null ? maps.Erosion.MaxCut : 0.0;
        }

        b.Sea       = MacroMapExport.SampleSea(s, roots, hp, b.Macro, texels, drainage, t.MaxDepth, t.SeaLevel, Stride, null);
        b.FinalLand = b.Sea.LandShare;
        return b;
    }

    // The winner's result: its continents' shares, the share of the area where the final terrain
    // is on the continent map's side of sea level (ContinentTests' agreement, over texels rather
    // than random points), and the preview.
    static Result Finish(Build b, in SurfaceDef s, RootQuad[] roots, in HeightParams hp, TerrainRoot root, float target)
    {
        var r = new Result
        {
            Target = target, FinalLand = b.FinalLand, Map = b.Map.Stats, LandMicroCells = b.Map.Stats.LandCells,
            BuildMilliseconds = b.Milliseconds, Envelope = b.Envelope, Eroded = b.Eroded, MaxCut = b.MaxCut,
        };

        // The pieces are sorted largest first, so the first Stats.Continents are the continents.
        double land = 0.0;
        foreach (double a in b.Map.LandPieces) land += a;
        r.Pieces = new double[b.Map.Stats.Continents];
        for (int i = 0; i < r.Pieces.Length; i++) r.Pieces[i] = b.Map.LandPieces[i] / land;

        // Agreement, each texel weighted by its area as the sea's land share is.
        MacroMapExport.Sea sea = b.Sea;
        int full = b.Macro.Layout.Cells(b.Macro.Layout.LevelCount - 1);
        double[] area = ContinentMap.AreaTable(s, roots[0], full);
        var tables = new ArrayContinentTables(b.Map);
        double agree = 0.0, total = 0.0;
        for (int y = 0; y < sea.Height; y++)
        for (int x = 0; x < sea.Width; x++)
        {
            double h = sea.Heights[y * sea.Width + x];
            if (double.IsNaN(h) || !MacroMapExport.PixelPoint(s, roots, sea, x, y, out double3 p)) continue;
            double a = area[(y % sea.Tile) * Stride * (full + 1) + (x % sea.Tile) * Stride];
            total += a;
            if ((ContinentField.Sample(p, tables, b.Continent).S > 0.0) == (h > sea.Level)) agree += a;
        }
        r.Agreement = total > 0.0 ? agree / total : 0.0;

        Color32[] pixels = MacroMapExport.SeaPixels(sea, root.OceanMaterial, ContinentParams.TopTexelSpacing(hp.S0, b.Macro.Layout));
        var tex = new Texture2D(sea.Width, sea.Height, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
        };
        tex.SetPixels32(pixels);
        tex.Apply();
        r.Preview = tex;
        return r;
    }
}
