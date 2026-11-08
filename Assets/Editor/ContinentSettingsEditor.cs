using System;
using UnityEditor;
using UnityEngine;
using Sonoma.Core.Generation;
using Sonoma.Core.Quadtree;
using Sonoma.Systems.Configuration;

// The continent designer's inspector: TargetLand and Fragmentation, the Regenerate button that
// turns them into the fields below (ContinentDesigner), the last result's land and sea and what
// it measured, and the low-level fields under a foldout.
[CustomEditor(typeof(ContinentSettings))]
class ContinentSettingsEditor : Editor
{
    const int TooFewMicroCells = 30;   // a continent smaller reads as a blob

    static bool _advanced = true;
    TerrainRoot _root;

    void OnEnable()
    {
        FindRoot();
        EditorApplication.hierarchyChanged += FindRoot;
    }

    void OnDisable() => EditorApplication.hierarchyChanged -= FindRoot;

    void FindRoot() => _root = ContinentDesigner.FindRoot((ContinentSettings)target);

    public override void OnInspectorGUI()
    {
        var asset = (ContinentSettings)target;
        serializedObject.Update();

        EditorGUILayout.LabelField("Design", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(ContinentSettings.TargetLand)));
        EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(ContinentSettings.Fragmentation)));
        serializedObject.ApplyModifiedProperties();

        int centres = ContinentDesign.ContinentCountFor(asset.Fragmentation);
        EditorGUILayout.LabelField(" ", $"{centres} continent centre{(centres == 1 ? "" : "s")}, " +
                                        $"about {ContinentDesign.MacroCellsFor(centres, asset.TargetLand)} macro cells",
                                   EditorStyles.miniLabel);
        Status(asset);
        RegenerateButton(asset);
        LastResult(asset);

        EditorGUILayout.Space();
        _advanced = EditorGUILayout.Foldout(_advanced, "Map settings", true);
        if (_advanced)
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script", nameof(ContinentSettings.TargetLand),
                                    nameof(ContinentSettings.Fragmentation));
            serializedObject.ApplyModifiedProperties();
        }
    }

    // Whether the fields below are what the settings above last made.
    static void Status(ContinentSettings asset)
    {
        if (asset.RegeneratedTargetLand < 0f)
        {
            EditorGUILayout.HelpBox("Never regenerated: the map settings below are set by hand.", MessageType.None);
            return;
        }
        ContinentConfig k = asset.ToConfig();
        ContinentConfig d = ContinentDesign.Derive(k, asset.Fragmentation, asset.LandFraction);
        bool same = asset.RegeneratedTargetLand == asset.TargetLand &&
                    asset.RegeneratedFragmentation == asset.Fragmentation &&
                    d.ContinentCount == k.ContinentCount && d.MacroCells == k.MacroCells && d.Separation == k.Separation;
        if (same)
            EditorGUILayout.HelpBox($"Regenerated: the map makes {asset.MeasuredLand:P1} land against a target of " +
                                    $"{asset.TargetLand:P1}.", MessageType.Info);
        else
            EditorGUILayout.HelpBox("The settings or the map settings have changed since the last Regenerate.",
                                    MessageType.Warning);
    }

    void RegenerateButton(ContinentSettings asset)
    {
        string path = AssetDatabase.GetAssetPath(asset);
        bool pinned = Array.IndexOf(ContinentDesigner.PinnedAssets, path) >= 0;
        if (pinned)
            EditorGUILayout.HelpBox("The tests copy this asset's fields and pin figures measured from its map. " +
                                    "Regenerating it is a retune: rerun the measuring tools and re-pin MacroTests." +
                                    "StressMacros, or the suite fails. Experiment in ContinentSandbox instead.",
                                    MessageType.Warning);

        string why = EditorApplication.isPlayingOrWillChangePlaymode ? "Leave play mode to regenerate: the maps are built once, at startup."
                   : _root == null ? "Open a scene whose TerrainRoot uses this asset (ContinentSandbox does) to regenerate: " +
                                     "the map is built on that scene's surface."
                   : null;
        if (why != null) EditorGUILayout.HelpBox(why, MessageType.None);

        using (new EditorGUI.DisabledScope(why != null))
        {
            if (GUILayout.Button(pinned ? "Regenerate (retune a pinned scene)" : "Regenerate", GUILayout.Height(26)))
            {
                if (!pinned || EditorUtility.DisplayDialog("Regenerate a pinned asset?",
                        $"{asset.name} is pinned by the tests. Regenerating it will fail the suite until its " +
                        "measured figures are re-pinned.", "Regenerate", "Cancel"))
                {
                    ContinentDesigner.Regenerate(_root, asset);
                    serializedObject.Update();
                    GUIUtility.ExitGUI();
                }
            }
        }
    }

    static void LastResult(ContinentSettings asset)
    {
        if (!ContinentDesigner.Last.TryGetValue(asset.GetInstanceID(), out var r)) return;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Last Regenerate", EditorStyles.boldLabel);
        if (r.Error != null)
        {
            EditorGUILayout.HelpBox(r.Error, MessageType.Error);
            return;
        }

        if (r.Preview != null)
        {
            float width  = EditorGUIUtility.currentViewWidth - 40f;
            float height = width * r.Preview.height / r.Preview.width;
            Rect  rect   = GUILayoutUtility.GetRect(width, height, GUILayout.ExpandWidth(false));
            EditorGUI.DrawPreviewTexture(rect, r.Preview, null, ScaleMode.ScaleToFit);
        }

        var st = r.Map;
        Line("Land", $"{r.FinalLand:P2} of the surface (target {r.Target:P1}), the map {st.LandFraction:P1}");
        Line("Agreement", $"{r.Agreement:P1} of the area on the map's side of the sea");
        Line("Continents", $"{st.Continents}, largest {st.LargestContinent:P0} of the land");
        if (r.Pieces != null && r.Pieces.Length > 1)
        {
            var shares = new string[Math.Min(r.Pieces.Length, 12)];
            for (int i = 0; i < shares.Length; i++) shares[i] = (r.Pieces[i] * 100.0).ToString("F0");
            Line(" ", string.Join(", ", shares) + (r.Pieces.Length > shares.Length ? ", ..." : "") + " %");
        }
        Line("Islands", $"{st.Islands} under {ContinentMap.ContinentShare:P0} of the land");
        Line("Micro cells", $"{st.LandCells:N0} land, {(st.Continents > 0 ? st.LandCells / (double)st.Continents : 0):F0} a continent");
        Line("Saturation", $"{st.SaturationRadius:F2} micro spacings ({st.WarpedSaturation:F2} warped)");
        if (r.Eroded) Line("Erosion", $"deepest cut {r.MaxCut:F0} m");
        Line("Envelope", $"±{r.Envelope / 1000.0:F1} km");
        Line("Builds", $"{r.Builds}, {r.BuildMilliseconds:F0} ms the last: {string.Join("; ", r.Tries)}");

        if (!r.Converged)
            EditorGUILayout.HelpBox($"The land did not reach the target within half a point in {r.Builds} builds; the " +
                                    "closest was kept. Regenerate again to start from it.", MessageType.Warning);
        if (r.Agreement < 0.9)
            EditorGUILayout.HelpBox("Under 90% of the area agrees with the map: the detail noise is drawing the coasts " +
                                    "rather than the continents. ContinentTests holds the stress scenes to 90%.",
                                    MessageType.Warning);
        if (st.Continents > 0 && st.LandCells / (double)st.Continents < TooFewMicroCells)
            EditorGUILayout.HelpBox($"Under {TooFewMicroCells} micro cells a continent: they read as blobs. Raising " +
                                    "MicroCells sharpens them, and steepens the coasts the LOD pays for.",
                                    MessageType.Warning);
    }

    static void Line(string label, string value) => EditorGUILayout.LabelField(label, value, EditorStyles.wordWrappedLabel);
}
