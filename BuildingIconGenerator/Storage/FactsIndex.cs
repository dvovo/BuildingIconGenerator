// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Persistent map of measured facts about assets (key -> number), kept in
    /// <c>cache/facts.bin</c>. Used for values that are expensive to measure and never change for a
    /// given asset - asset names carry their content hash - such as:
    /// <list type="bullet">
    /// <item><c>alpha:&lt;surface&gt;</c>: mean albedo alpha of a prop surface (needs a GPU readback,
    /// which stalls the main thread until the GPU has drained its queue);</item>
    /// <item><c>uvd:&lt;render&gt;</c>: texel density of a building mesh (needs a pass over its triangles).</item>
    /// </list>
    /// Main thread only.
    /// </summary>
    internal static class FactsIndex
    {
        private const uint kMagic = 0x46474942; // "BIGF"
        // 2: keys carry the content stamp of the measured model or surface.
        private const int kVersion = 2;

        private static readonly Dictionary<string, float> s_Facts = new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>Keys read or written this session (see <see cref="PlanIndex.RemoveOutdated{T}"/>).</summary>
        private static readonly HashSet<string> s_Touched = new HashSet<string>(StringComparer.Ordinal);
        private static bool s_Loaded;
        private static bool s_Dirty;
        private static string s_File;

        public static int Count => s_Facts.Count;

        public static bool TryGet(string key, out float value)
        {
            EnsureLoaded();

            if (s_Facts.TryGetValue(key, out value))
            {
                s_Touched.Add(key);
                return true;
            }

            return false;
        }

        public static void Put(string key, float value)
        {
            EnsureLoaded();
            s_Facts[key] = value;
            s_Touched.Add(key);
            s_Dirty = true;
        }

        /// <summary>Writes the file when something changed since the last save.</summary>
        public static void Save()
        {
            if (!s_Dirty || s_File == null)
            {
                return;
            }

            PlanIndex.RemoveOutdated(s_Facts, s_Touched);

            try
            {
                BinaryStore.Write(s_File, kMagic, kVersion, writer =>
                {
                    writer.Write(s_Facts.Count);

                    foreach (var pair in s_Facts)
                    {
                        writer.Write(pair.Key);
                        writer.Write(pair.Value);
                    }
                });

                s_Dirty = false;
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Facts index not saved: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void EnsureLoaded()
        {
            if (s_Loaded)
            {
                return;
            }

            s_Loaded = true;

            try
            {
                s_File = ModPaths.CacheFile("facts.bin");
                BinaryStore.Read(s_File, kMagic, kVersion, reader =>
                {
                    int count = reader.ReadInt32();

                    for (int i = 0; i < count; i++)
                    {
                        string key = reader.ReadString();
                        s_Facts[key] = reader.ReadSingle();
                    }
                });
            }
            catch (Exception ex)
            {
                s_Facts.Clear();
                Mod.Log.Warn($"Facts index unreadable, rebuilding it: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
