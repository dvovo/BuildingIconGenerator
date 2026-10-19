// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds the AssetBundle with the mod's shaders and copies it into the mod project
/// (<c>../BuildingIconGenerator/Bundles/building_icon_shaders</c>), from where the mod build ships it.
/// Menu: <b>Building Icon Generator &gt; Build shader bundle</b>. Use the Unity version the game is
/// built with (see ProjectSettings/ProjectVersion.txt).
/// </summary>
public static class BuildShaderBundle
{
    private const string BundleName = "building_icon_shaders";
    private const string ShaderFolder = "Assets/BuildingIconGenerator/Shaders";

    [MenuItem("Building Icon Generator/Build shader bundle")]
    public static void Build()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Shader", new[] { ShaderFolder }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            AssetImporter.GetAtPath(path).assetBundleName = BundleName;
        }

        // Every graphics API the game can run on.
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,
            new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Vulkan });

        var projectRoot = Path.GetDirectoryName(Application.dataPath);
        var output = Path.Combine(projectRoot, "AssetBundles");
        Directory.CreateDirectory(output);

        var manifest = BuildPipeline.BuildAssetBundles(output,
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
            BuildTarget.StandaloneWindows64);

        if (manifest == null)
        {
            Debug.LogError("Building Icon Generator: shader bundle build failed (see the console).");
            return;
        }

        var destination = Path.GetFullPath(Path.Combine(projectRoot, "..", "BuildingIconGenerator", "Bundles"));
        Directory.CreateDirectory(destination);
        File.Copy(Path.Combine(output, BundleName), Path.Combine(destination, BundleName), true);

        Debug.Log($"Building Icon Generator: '{BundleName}' built for D3D11/D3D12/Vulkan and copied to {destination}");
        EditorUtility.DisplayDialog("Building Icon Generator", $"Shader bundle built and copied to\n{destination}", "OK");
    }
}
