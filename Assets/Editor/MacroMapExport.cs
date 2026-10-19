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
        var hp = HeightParams.Create(s, t.ChunkResolution, t.OctaveWavelength0, t.OctaveCount,
                                     t.HeightScale, t.Persistence, t.Lacunarity, t.Seed);
        var mp = MacroParams.Create(s, hp, t.Macro.ToConfig());

        float4[] texels;
        double   buildMs;
        using (var maps = MacroMaps.Build(s, roots, mp))
        {
            texels  = maps.Texels.ToArray();
            buildMs = maps.BuildMilliseconds;
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
        double spacing = hp.S0 / math.SQRT2_DBL / n;
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

        Debug.Log($"[MacroMapExport] {scene}: level {level} of {mp.Layout.LevelCount}, {tile} x {tile} " +
                  $"texels a quad, built in {buildMs:F1} ms. " +
                  $"Base {Range(px[0])} m, detail to octave {octave} {Range(px[1])} m, total {Range(px[2])} m " +
                  $"(colour scale +-{heightMax:F0} m, hillshade x{exaggeration:F1}); " +
                  $"temperature {Range(px[3])} C, moisture {Range(px[4])}, ruggedness {Range(px[5])}. " +
                  $"Written to {folder}");
        EditorUtility.RevealInFinder(Path.Combine(folder, $"{scene}_total.png"));
    }

    // ── Biomes ───────────────────────────────────────────────────────────────

    readonly struct ArrayTexels : IMacroTexels
    {
        readonly float4[] _texels;
        public ArrayTexels(float4[] texels) { _texels = texels; }
        public float4 Texel(int index) => _texels[index];
    }

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
        var reader = new ArrayTexels(texels);
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

    static string Range(double[] values)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (double v in values)
            if (!double.IsNaN(v)) { lo = math.min(lo, v); hi = math.max(hi, v); }
        return $"{lo:F2}..{hi:F2}";
    }
}
