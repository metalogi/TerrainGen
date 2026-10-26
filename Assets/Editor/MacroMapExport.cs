using System;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Core.Surface;

// Writes the open scene's macro maps to PNGs in Logs/MacroMaps/, one image per channel, so the
// macro layer can be seen and tuned without flying over it.
//
// The scene shows the macro layer poorly. Its base height is a third of the detail noise's
// amplitude on the planets, at the same wavelengths as the detail's coarsest octaves, and the
// shared terrain material colours by elevation bands tuned for SampleScene's tens of metres, so a
// planet is green below about 50 m and white above. The climate channels are not drawn at all
// until M5b's biomes. These images are the macro layer on its own:
//
//   <scene>_base.png         base height: the macro layer's own relief
//   <scene>_detail.png       the detail noise at the same points, carrying the octaves a chunk at
//                            the top level's depth carries -- what was there before M5a
//   <scene>_total.png        the two added: what a chunk at that depth draws
//   <scene>_temperature.png  -40 to 40 degrees C, blue through white to red
//   <scene>_moisture.png     0 to 1, dry tan through green to wet blue
//   <scene>_ruggedness.png   0 to 1, black to white
//   <scene>_biomes.png       the biome classifier's weights over the biome colours, unlit, and
//                            the climate and biome at each pole of a sphere in the console
//
// With a continent map (Docs/Continents_Plan.md) the base height includes the continent height,
// the detail drops the octaves the cutoff removes, and two more images show the map itself:
//
//   <scene>_continents.png   the micro cells, green land and blue ocean, brighter with their parent
//                            macro cell's elevation; micro cell edges darkened, macro cell edges in
//                            black, and the continent centres' cells tinted red
//   <scene>_coast.png        the signed coast distance s, green on land and blue at sea, pale near
//                            the coast and saturated at two micro spacings, with the coast in black
//
// With erosion (Docs/M5_MacroLayer_Plan.md, M5c-1) the base height is the eroded one, D folded in,
// and two more images show what the pass did:
//
//   <scene>_erosion.png      D, the correction: white where nothing changed, through orange to dark
//                            red where ground was cut, and to blue where it was raised -- diffusion,
//                            or a basin the breach could not drain, filled -- on a
//                            square-root scale symmetric about zero to the largest |D|, so shallow
//                            cuts show beside the deep ones; valleys that join should read as lines
//   <scene>_wetness.png      wetness, 0 to 1: dark ground through blue to white, ridges dark and
//                            rivers bright; a river network should read as a branching tree
//
// The three heights share one colour scale -- blue below zero, green through brown to white
// above, by the largest |total| -- and a hillshade lit from the upper left with the vertical
// exaggeration logged, so they compare directly. Each image is the top pyramid level, one pixel
// per texel. A cube sphere is unfolded as a cross: +Z, +X, -Z, -X along the middle row with u to
// the right and v up, +Y above +Z and -Y below it, so every join in the cross is a real edge. A
// cylinder or plane grid is its roots laid out by column and row.
//
// It builds the maps with the same MacroMaps.Build the terrain runs, so it needs no play mode.
static class MacroMapExport
{
    const int    HeightSlots = 3;   // base, detail, total
    const double TempMin = -40.0, TempMax = 40.0;

    [MenuItem("Tools/Sonoma/Export Macro Maps")]
    static void Export()
    {
        var root = UnityEngine.Object.FindAnyObjectByType<TerrainRoot>();
        if (root == null || root.Settings == null)
        {
            Debug.LogError("[MacroMapExport] The open scene has no TerrainRoot with TerrainSettings.");
            return;
        }
        var t = root.Settings;
        if (t.Macro == null)
        {
            Debug.LogWarning($"[MacroMapExport] {t.name} has no macro layer; nothing to export.");
            return;
        }

        try
        {
            EditorUtility.DisplayProgressBar("Export Macro Maps", "Building the maps", 0f);
            Write(root, t);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    static void Write(TerrainRoot root, Sonoma.Systems.Configuration.TerrainSettings t)
    {
        SurfaceDef s     = root.BuildSurface();
        RootQuad[] roots = SurfaceMath.BuildRoots(s);
        // As TerrainRoot.Start builds them, so the detail image carries the octaves the terrain does.
        var hp = HeightParams.Create(s, t.ChunkResolution, t.OctaveWavelength0, t.OctaveCount,
                                     t.HeightScale, t.Persistence, t.Lacunarity, t.Seed)
                             .WithBands(t.Macro.LowBandEdge, t.Macro.MidBandEdge);
        if (t.RoughnessCrossover > 0.0)
            hp = hp.WithRoughness(t.FinePersistence, t.RoughnessCrossover);
        if (t.Macro.Continents != null)
            hp = hp.WithContinentalCutoff(t.Macro.Continents.DetailCutoff, t.Macro.Continents.CoarseOctaveAmplitude);
        MacroConfig config = t.Macro.ToConfig();
        var mp = MacroParams.Create(s, hp, config);
        var cp = ContinentParams.Create(s, mp, config);
        var ep = ErosionParams.Create(s, hp, mp, config);

        float4[]     texels;
        float2[]     drainage;
        double       buildMs;
        ContinentMap map;
        ErosionStats erosion;
        // Keeping the top level's nearest micro seeds and coast distances, which the continent
        // images draw, rather than running Stages A and B again here.
        using (var maps = MacroMaps.Build(s, roots, mp, cp, hp, ep, keepTopTexels: true))
        {
            texels   = maps.Texels.ToArray();
            drainage = maps.Drainage.ToArray();
            buildMs  = maps.BuildMilliseconds;
            map      = maps.Continents;
            erosion  = maps.Erosion;
        }

        int level = mp.Layout.LevelCount - 1;
        int n     = mp.Layout.Cells(level);
        int tile  = n + 1;
        Tiles(s, out int tilesX, out int tilesY);
        int w = tilesX * tile, h = tilesY * tile;

        // Per pixel: base, detail, total, temperature, moisture, ruggedness. NaN where the cross
        // has no face.
        var px = new double[6][];
        for (int c = 0; c < 6; c++)
        {
            px[c] = new double[w * h];
            Array.Fill(px[c], double.NaN);
        }

        int octave = hp.MaxOctave(level);
        for (int q = 0; q < roots.Length; q++)
        {
            EditorUtility.DisplayProgressBar("Export Macro Maps", $"Root quad {q + 1} of {roots.Length}",
                                             (q + 1f) / roots.Length);
            Place(s, q, out int tx, out int ty);
            for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                float4  m      = texels[mp.Layout.Index(q, level, i, j)];
                double3 p      = SurfaceMath.SurfacePoint(s, roots[q], i / (double)n, j / (double)n);
                double  detail = TerrainHeightFunction.Height(p, octave, MacroSample.Zero, hp);
                int at = (ty * tile + j) * w + tx * tile + i;
                px[0][at] = m.x;
                px[1][at] = detail;
                px[2][at] = m.x + detail;
                px[3][at] = m.y;
                px[4][at] = m.z;
                px[5][at] = m.w;
            }
        }

        // One scale for the three heights, and one exaggeration for their hillshades, taken from
        // the total so all three are lit alike.
        double heightMax = 1e-6;
        foreach (double v in px[2]) if (!double.IsNaN(v)) heightMax = math.max(heightMax, math.abs(v));
        // Nominal texel spacing: a quad's edge is its diagonal S0 over root 2.
        double spacing = ContinentParams.TopTexelSpacing(hp.S0, mp.Layout);
        double exaggeration = Exaggeration(px[2], w, h, tile, spacing);

        string scene  = SceneManager.GetActiveScene().name;
        string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "MacroMaps");
        Directory.CreateDirectory(folder);

        string[] names = { "base", "detail", "total", "temperature", "moisture", "ruggedness" };
        for (int c = 0; c < 6; c++)
        {
            var colours = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int    at = y * w + x;
                double v  = px[c][at];
                if (double.IsNaN(v)) { colours[at] = new Color32(40, 40, 40, 255); continue; }

                float3 rgb = c switch
                {
                    < HeightSlots => Hypsometric(v / heightMax)
                                     * Shade(px[c], w, tile, x, y, spacing, exaggeration),
                    3 => Temperature((v - TempMin) / (TempMax - TempMin)),
                    4 => Moisture(v),
                    _ => new float3((float)math.saturate(v)),
                };
                rgb = math.saturate(rgb) * 255f;
                colours[at] = new Color32((byte)rgb.x, (byte)rgb.y, (byte)rgb.z, 255);
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(colours);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(folder, $"{scene}_{names[c]}.png"), tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        WriteBiomes(s, roots, mp, t.Macro, texels, level, tile, w, h, Path.Combine(folder, $"{scene}_biomes.png"));
        if (map != null)
        {
            WriteContinents(s, roots, mp.Layout, map, tile, w, h, cp.MicroSpacing, folder, scene);
            ContinentStats st = map.Stats;
            Debug.Log($"[MacroMapExport] {scene} continents: {st.MacroCells} macro and {st.MicroCells:N0} micro cells, " +
                      $"{st.LandCells:N0} land, {st.LandFraction:P1} of the map by area; micro spacing " +
                      $"{cp.MicroSpacing / 1000.0:F1} km, {cp.MicroCellTexels(hp.S0, mp.Layout):F1} texels; " +
                      $"saturation {st.SaturationRadius:F2} ({st.WarpedSaturation:F2} warped) micro spacings; detail " +
                      $"octaves from {hp.FirstOctave}.");
        }
        if (erosion != null)
        {
            WriteErosion(s, roots, mp.Layout, drainage, tile, w, h, folder, scene, out string dRange, out string wetRange);
            Debug.Log($"[MacroMapExport] {scene} erosion: {erosion.NodeCount:N0} nodes, {ep.Iterations} iterations, " +
                      $"eroding the base height and detail octaves to {ErosionChannels.ErodedOctave(hp, mp)}; D {dRange} m, " +
                      $"wetness {wetRange}; deepest cut {erosion.MaxCut:F0} m; base height within " +
                      $"+-{erosion.MaxAbsBaseHeight:F0} m; {erosion.TotalMilliseconds:F1} ms of the build.");
        }

        Debug.Log($"[MacroMapExport] {scene}: level {level} of {mp.Layout.LevelCount}, {tile} x {tile} " +
                  $"texels a quad, built in {buildMs:F1} ms. " +
                  $"Base {Range(px[0])} m, detail to octave {octave} {Range(px[1])} m, total {Range(px[2])} m " +
                  $"(colour scale +-{heightMax:F0} m, hillshade x{exaggeration:F1}); " +
                  $"temperature {Range(px[3])} C, moisture {Range(px[4])}, ruggedness {Range(px[5])}. " +
                  $"Written to {folder}");
        EditorUtility.RevealInFinder(Path.Combine(folder, $"{scene}_total.png"));
    }

    // ── Biomes ───────────────────────────────────────────────────────────────

    // <scene>_biomes.png: what Biomes.Classify makes of each texel's climate, the weights
    // blending the BiomeDefinitions' colours as the shader does, unlit. The same maps and the same
    // classifier the sample jobs use, so a colour here that differs from the scene's is a
    // rendering question, not a climate one. And the climate and weights at each pole of a cube
    // sphere, in the console.
    static void WriteBiomes(in SurfaceDef s, RootQuad[] roots, in MacroParams mp,
                            Sonoma.Systems.Configuration.MacroLayerSettings settings, float4[] texels,
                            int level, int tile, int w, int h, string path)
    {
        if (mp.BiomeCount == 0) return;

        var colour = new float3[Biomes.MaxBiomes];
        for (int b = 0; b < mp.BiomeCount; b++)
        {
            Color c = settings.Biomes[b].Color;
            colour[b] = new float3(c.r, c.g, c.b);
        }

        int n = tile - 1;
        var pixels = new Color32[w * h];
        Array.Fill(pixels, new Color32(40, 40, 40, 255));
        for (int q = 0; q < roots.Length; q++)
        {
            Place(s, q, out int tx, out int ty);
            for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                float4 m = texels[mp.Layout.Index(q, level, i, j)];
                float4 wt = Biomes.Classify(new MacroSample { BaseHeight = m.x, Temperature = m.y,
                                                              Moisture = m.z, Ruggedness = m.w }, mp);
                float3 rgb = wt.x * colour[0] + wt.y * colour[1] + wt.z * colour[2] + wt.w * colour[3];
                rgb = math.saturate(rgb) * 255f;
                pixels[(ty * tile + j) * w + tx * tile + i] = new Color32((byte)rgb.x, (byte)rgb.y, (byte)rgb.z, 255);
            }
        }

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        try
        {
            tex.SetPixels32(pixels);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(tex);
        }

        if (s.Type != SurfaceType.CubeSphere) return;
        // The poles' log reads climate only, never the drainage maps.
        var reader = new ArrayMacroTexels(texels, null);
        foreach (var (label, dir) in new[] { ("north pole (+Y)", new double3(0, 1, 0)), ("south pole (-Y)", new double3(0, -1, 0)) })
        {
            SurfaceMath.Locate(s, dir * s.Radius, out int q, out double u, out double v);
            var    m  = MacroSampler.Read(reader, mp, q, level, u, v);
            float4 wt = Biomes.Classify(m, mp);
            int top = 0;
            for (int b = 1; b < mp.BiomeCount; b++) if (wt[b] > wt[top]) top = b;
            Debug.Log($"[MacroMapExport] {label}: quad {q}, {m.Temperature:F1} C, moisture {m.Moisture:F2}, " +
                      $"base {m.BaseHeight:F0} m -> {settings.Biomes[top].name} ({wt[top]:P0}); weights {wt}");
        }
    }

    // ── Continents ───────────────────────────────────────────────────────────

    // <scene>_continents.png and <scene>_coast.png, at every texel of the top level: the micro cell
    // each texel's point falls in, unwarped, and the field's signed distance there, warped, as the
    // build computed them and kept (ContinentMap.TopMicro and TopCoast).
    static void WriteContinents(in SurfaceDef s, RootQuad[] roots, in MacroLayout layout, ContinentMap map,
                                int tile, int w, int h, double microSpacing, string folder, string scene)
    {
        int n = tile - 1, width = layout.Width(layout.LevelCount - 1);
        var micro  = new int[w * h];
        var signed = new double[w * h];
        Array.Fill(micro, -1);
        Array.Fill(signed, double.NaN);

        for (int q = 0; q < roots.Length; q++)
        {
            Place(s, q, out int tx, out int ty);
            for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                int at = (ty * tile + j) * w + tx * tile + i, k = ContinentMap.ScanIndex(q, i, j, width);
                micro[at]  = map.TopMicro[k];
                signed[at] = map.TopCoast[k];
            }
        }

        var cells = new Color32[w * h];
        var coast = new Color32[w * h];
        double cap = 2.0 * microSpacing;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int at = y * w + x;
            if (micro[at] < 0)
            {
                cells[at] = coast[at] = new Color32(40, 40, 40, 255);
                continue;
            }

            int    m      = micro[at], parent = map.MicroParent[m];
            float  e      = (float)map.MacroElevation[parent];
            float3 rgb    = map.MicroLand[m] != 0 ? new float3(40f + 120f * e, 110f + 120f * e, 40f + 40f * e)
                                                  : new float3(20f + 60f * e, 40f + 90f * e, 110f + 120f * e);
            if (map.MacroCentre[parent]) rgb.x += 40f;

            // Edges against the right and upper neighbours within the tile.
            bool microEdge = false, macroEdge = false, coastLine = false;
            if ((x + 1) % tile != 0)
                Edges(map, micro, signed, at, at + 1, ref microEdge, ref macroEdge, ref coastLine);
            if ((y + 1) % tile != 0 && at + w < micro.Length)
                Edges(map, micro, signed, at, at + w, ref microEdge, ref macroEdge, ref coastLine);
            if (microEdge) rgb *= 0.8f;
            if (macroEdge) rgb = 0f;
            rgb = math.clamp(rgb, 0f, 255f);
            cells[at] = new Color32((byte)rgb.x, (byte)rgb.y, (byte)rgb.z, 255);

            float  u    = (float)math.clamp(signed[at] / cap, -1.0, 1.0);
            float3 pale = new float3(235f, 235f, 225f);
            float3 c    = u >= 0f ? math.lerp(pale, new float3(30f, 120f, 40f), u) : math.lerp(pale, new float3(20f, 50f, 140f), -u);
            if (coastLine) c = 0f;
            coast[at] = new Color32((byte)c.x, (byte)c.y, (byte)c.z, 255);
        }

        foreach (var (name, pixels) in new[] { ("continents", cells), ("coast", coast) })
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(pixels);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(folder, $"{scene}_{name}.png"), tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }

    // Which edges pixel `at` shares with its neighbour `nb`: a micro cell's, a macro cell's (its
    // parent's), and the coast's.
    static void Edges(ContinentMap map, int[] micro, double[] signed, int at, int nb,
                      ref bool microEdge, ref bool macroEdge, ref bool coastLine)
    {
        if (micro[nb] < 0) return;
        if (micro[nb] != micro[at]) microEdge = true;
        if (map.MicroParent[micro[nb]] != map.MicroParent[micro[at]]) macroEdge = true;
        if ((signed[nb] >= 0.0) != (signed[at] >= 0.0)) coastLine = true;
    }

    // ── Erosion ──────────────────────────────────────────────────────────────

    // <scene>_erosion.png and <scene>_wetness.png at every texel of the top level, from the drainage
    // maps as the build left them: D in y, wetness in x.
    static void WriteErosion(in SurfaceDef s, RootQuad[] roots, in MacroLayout layout, float2[] drainage,
                             int tile, int w, int h, string folder, string scene,
                             out string dRange, out string wetRange)
    {
        int n = tile - 1, level = layout.LevelCount - 1;
        var d   = new double[w * h];
        var wet = new double[w * h];
        Array.Fill(d,   double.NaN);
        Array.Fill(wet, double.NaN);
        for (int q = 0; q < roots.Length; q++)
        {
            Place(s, q, out int tx, out int ty);
            for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                float2 v  = drainage[layout.Index(q, level, i, j)];
                int    at = (ty * tile + j) * w + tx * tile + i;
                d[at]   = v.y;
                wet[at] = v.x;
            }
        }
        dRange   = Range(d);
        wetRange = Range(wet);

        double scale = 1e-6;
        foreach (double v in d) if (!double.IsNaN(v)) scale = math.max(scale, math.abs(v));

        var cut   = new Color32[w * h];
        var water = new Color32[w * h];
        var none  = new Color32(40, 40, 40, 255);
        for (int at = 0; at < w * h; at++)
        {
            if (double.IsNaN(d[at])) { cut[at] = water[at] = none; continue; }
            cut[at]   = Pixel(Diverging(math.sign(d[at]) * math.sqrt(math.abs(d[at]) / scale)));
            water[at] = Pixel(Wetness(wet[at]));
        }

        foreach (var (name, pixels) in new[] { ("erosion", cut), ("wetness", water) })
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(pixels);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(folder, $"{scene}_{name}.png"), tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }

    static Color32 Pixel(float3 rgb)
    {
        rgb = math.saturate(rgb) * 255f;
        return new Color32((byte)rgb.x, (byte)rgb.y, (byte)rgb.z, 255);
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    static void Tiles(in SurfaceDef s, out int x, out int y)
    {
        if (s.Type == SurfaceType.CubeSphere) { x = 4; y = 3; return; }
        x = s.Cols; y = s.Rows;
    }

    // Each quad's tile. The cube's middle row runs +Z, +X, -Z, -X, each one's East edge meeting
    // the next one's West unreversed (SurfaceMath.CubeEdgeLink), and +Y and -Y meet +Z's North
    // and South edges unreversed, so the cross has no flipped joins.
    static void Place(in SurfaceDef s, int quad, out int x, out int y)
    {
        if (s.Type == SurfaceType.CubeSphere)
        {
            (x, y) = quad switch
            {
                4 => (0, 1), 0 => (1, 1), 5 => (2, 1), 1 => (3, 1),
                2 => (0, 2), _ => (0, 0),   // 3, -Y
            };
            return;
        }
        x = quad / s.Rows;
        y = quad % s.Rows;
    }

    // ── Shading ──────────────────────────────────────────────────────────────

    // A vertical exaggeration that puts the mean slope of the total at about 0.5, so relief
    // reads whatever the planet's size.
    static double Exaggeration(double[] heights, int w, int h, int tile, double spacing)
    {
        double sum = 0.0;
        int    count = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x + 1 < w; x++)
        {
            if ((x + 1) % tile == 0) continue;   // across a tile seam
            double a = heights[y * w + x], b = heights[y * w + x + 1];
            if (double.IsNaN(a) || double.IsNaN(b)) continue;
            sum += math.abs(b - a) / spacing;
            count++;
        }
        double mean = count > 0 ? sum / count : 0.0;
        return mean > 0.0 ? 0.5 / mean : 1.0;
    }

    // Lambert from the upper left, by central differences within the pixel's own tile.
    static float Shade(double[] heights, int w, int tile, int x, int y, double spacing, double exaggeration)
    {
        int tx0 = x - x % tile, ty0 = y - y % tile;
        int xl = math.max(x - 1, tx0), xr = math.min(x + 1, tx0 + tile - 1);
        int yd = math.max(y - 1, ty0), yu = math.min(y + 1, ty0 + tile - 1);

        double dx = (heights[y * w + xr] - heights[y * w + xl]) / ((xr - xl) * spacing);
        double dy = (heights[yu * w + x] - heights[yd * w + x]) / ((yu - yd) * spacing);
        var normal = math.normalize(new double3(-dx * exaggeration, -dy * exaggeration, 1.0));
        var light  = math.normalize(new double3(-1.0, 1.0, 1.2));
        return (float)(0.35 + 0.65 * math.saturate(math.dot(normal, light)));
    }

    // ── Colour ramps ─────────────────────────────────────────────────────────

    // h in -1..1: deep to shallow blue below zero, green through brown to white above.
    static float3 Hypsometric(double h)
    {
        float t = (float)math.clamp(h, -1.0, 1.0);
        if (t < 0f) return math.lerp(new float3(0.55f, 0.75f, 0.95f), new float3(0.05f, 0.15f, 0.45f), -t);
        return t < 0.4f ? math.lerp(new float3(0.25f, 0.55f, 0.20f), new float3(0.65f, 0.55f, 0.30f), t / 0.4f)
             : t < 0.8f ? math.lerp(new float3(0.65f, 0.55f, 0.30f), new float3(0.50f, 0.42f, 0.38f), (t - 0.4f) / 0.4f)
             :            math.lerp(new float3(0.50f, 0.42f, 0.38f), new float3(1.00f, 1.00f, 1.00f), (t - 0.8f) / 0.2f);
    }

    static float3 Temperature(double t)
    {
        float u = (float)math.saturate(t);
        return u < 0.5f ? math.lerp(new float3(0.10f, 0.25f, 0.85f), new float3(1f, 1f, 1f), u / 0.5f)
                        : math.lerp(new float3(1f, 1f, 1f), new float3(0.85f, 0.15f, 0.10f), (u - 0.5f) / 0.5f);
    }

    static float3 Moisture(double m)
    {
        float u = (float)math.saturate(m);
        return u < 0.5f ? math.lerp(new float3(0.85f, 0.75f, 0.50f), new float3(0.25f, 0.60f, 0.20f), u / 0.5f)
                        : math.lerp(new float3(0.25f, 0.60f, 0.20f), new float3(0.10f, 0.30f, 0.80f), (u - 0.5f) / 0.5f);
    }

    // u in -1..1: dark red through orange to white at zero, cuts below; white to blue above, where
    // ground was raised, by diffusion or by filling a basin the breach could not drain.
    static float3 Diverging(double u)
    {
        float t = (float)math.clamp(u, -1.0, 1.0);
        if (t >= 0f) return math.lerp(new float3(1f, 1f, 1f), new float3(0.10f, 0.30f, 0.80f), t);
        float c = -t;
        return c < 0.5f ? math.lerp(new float3(1f, 1f, 1f), new float3(0.95f, 0.55f, 0.15f), c / 0.5f)
                        : math.lerp(new float3(0.95f, 0.55f, 0.15f), new float3(0.45f, 0.05f, 0.05f), (c - 0.5f) / 0.5f);
    }

    // 0..1: dark brown ground through blue to near white, so the wettest channels stand out.
    static float3 Wetness(double v)
    {
        float u = (float)math.saturate(v);
        return u < 0.6f ? math.lerp(new float3(0.12f, 0.10f, 0.08f), new float3(0.15f, 0.40f, 0.80f), u / 0.6f)
                        : math.lerp(new float3(0.15f, 0.40f, 0.80f), new float3(0.90f, 0.97f, 1.00f), (u - 0.6f) / 0.4f);
    }

    static string Range(double[] values)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (double v in values)
            if (!double.IsNaN(v)) { lo = math.min(lo, v); hi = math.max(hi, v); }
        return $"{lo:F2}..{hi:F2}";
    }
}
