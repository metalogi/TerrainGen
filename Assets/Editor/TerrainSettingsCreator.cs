using UnityEditor;
using UnityEngine;
using Sonoma.Systems.Configuration;
using System.IO;

public static class TerrainSettingsCreator
{
    [MenuItem("Assets/Create/Sonoma/Terrain Settings (Default)")]
    public static void CreateDefaultTerrainSettings()
    {
        if (!Directory.Exists("Assets/Settings"))
        {
            Directory.CreateDirectory("Assets/Settings");
            AssetDatabase.ImportAsset("Assets/Settings");
        }

        var path = "Assets/Settings/TerrainSettings.asset";
        var existing = AssetDatabase.LoadAssetAtPath<TerrainSettings>(path);
        if (existing != null)
        {
            Selection.activeObject = existing;
            EditorUtility.DisplayDialog("Terrain Settings", "TerrainSettings.asset already exists. Selecting existing asset.", "OK");
            return;
        }

        var asset = ScriptableObject.CreateInstance<TerrainSettings>();
        AssetDatabase.CreateAsset(asset, path);
        AssetDatabase.SaveAssets();
        EditorUtility.FocusProjectWindow();
        Selection.activeObject = asset;
    }
}
