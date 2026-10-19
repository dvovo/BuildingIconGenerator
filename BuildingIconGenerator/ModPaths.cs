// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator
{
    using System.IO;
    using Colossal.PSI.Environment;

    /// <summary>
    /// Locations of the mod's data under the game's user-data folder:
    /// <c>ModsData/BuildingIconGenerator/icons</c> (the PNGs and their registry) and
    /// <c>ModsData/BuildingIconGenerator/cache</c> (persistent indexes that speed up later runs and
    /// can be deleted at any time).
    /// </summary>
    internal static class ModPaths
    {
        /// <summary>Root data folder of the mod.</summary>
        public static string DataFolder => Path.Combine(EnvPath.kUserDataPath, "ModsData", Mod.Id);

        /// <summary>Generated icons (one PNG per building) and <c>index.tsv</c>.</summary>
        public static string IconsFolder => Path.Combine(DataFolder, "icons");

        /// <summary>Disposable acceleration data (read plans, measured facts, usage statistics).</summary>
        public static string CacheFolder => Path.Combine(DataFolder, "cache");

        /// <summary>A file in the cache folder; the folder is created on demand.</summary>
        public static string CacheFile(string name)
        {
            Directory.CreateDirectory(CacheFolder);
            return Path.Combine(CacheFolder, name);
        }
    }
}
