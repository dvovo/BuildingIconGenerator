// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Persistent popularity index (<c>cache/usage.bin</c>): for every geometry
    /// (<c>g:&lt;render prefab&gt;</c>, which also keys its materials and baked colours) and surface
    /// (<c>s:&lt;surface asset&gt;</c>, which keys its decoded textures) it records how many DISTINCT
    /// buildings used it.
    ///
    /// The run caches evict by this count rather than by recency alone: a tree, a bench or an air
    /// conditioner seen in hundreds of buildings outlives a facade used once. Popularity is the
    /// largest count reached in any single run - never a sum across runs, so regenerating the same
    /// buildings does not inflate it - and entries unseen for <see cref="kForgetAfterRuns"/> runs are
    /// dropped. Only names are stored, so a game update cannot invalidate anything.
    /// Main thread only.
    /// </summary>
    internal static class UsageIndex
    {
        private const uint kMagic = 0x55474942; // "BIGU"
        private const int kVersion = 1;
        private const int kForgetAfterRuns = 20;

        /// <summary>Entries kept on disk, most used first (the full catalogue needs about 10 000).</summary>
        private const int kMaxEntries = 50000;

        private sealed class Entry
        {
            /// <summary>Most buildings that used it in any one earlier run.</summary>
            public int Uses;

            /// <summary>Buildings that used it in this run.</summary>
            public int RunUses;

            /// <summary>Last run that used it.</summary>
            public int LastRun;

            /// <summary>Serial of the last building that used it (counts a building once).</summary>
            public int LastBuilding;

            public int Heat => Math.Max(Uses, RunUses);
        }

        private static readonly Dictionary<string, Entry> s_Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static bool s_Loaded;
        private static bool s_Dirty;
        private static string s_File;
        private static int s_Run = 1;
        private static int s_Building;

        public static int Count => s_Entries.Count;

        /// <summary>Loads the index once per session; each session counts as a new run.</summary>
        public static void EnsureLoaded()
        {
            if (s_Loaded)
            {
                return;
            }

            s_Loaded = true;

            try
            {
                s_File = ModPaths.CacheFile("usage.bin");
                BinaryStore.Read(s_File, kMagic, kVersion, reader =>
                {
                    s_Run = reader.ReadInt32() + 1;
                    int count = reader.ReadInt32();

                    for (int i = 0; i < count; i++)
                    {
                        string key = reader.ReadString();
                        int uses = reader.ReadInt32();
                        int lastRun = reader.ReadInt32();

                        if (s_Run - lastRun <= kForgetAfterRuns)
                        {
                            s_Entries[key] = new Entry { Uses = uses, LastRun = lastRun, LastBuilding = -1 };
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                s_Entries.Clear();
                s_Run = 1;
                Mod.Log.Warn($"Usage index unreadable, rebuilding it: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Marks the start of a new building: uses are counted once per building.</summary>
        public static void NextBuilding()
        {
            s_Building++;
        }

        /// <summary>Records that the current building uses <paramref name="key"/>.</summary>
        public static void Note(string key)
        {
            if (key == null)
            {
                return;
            }

            if (!s_Entries.TryGetValue(key, out var entry))
            {
                entry = new Entry { LastBuilding = -1 };
                s_Entries[key] = entry;
            }

            if (entry.LastBuilding == s_Building)
            {
                return;
            }

            entry.LastBuilding = s_Building;
            entry.LastRun = s_Run;
            entry.RunUses = Math.Min(entry.RunUses + 1, 100000);
            s_Dirty = true;
        }

        /// <summary>Distinct buildings that used <paramref name="key"/>: max(best earlier run, this run); 0 if unknown.</summary>
        public static int Heat(string key)
        {
            return key != null && s_Entries.TryGetValue(key, out var entry) ? entry.Heat : 0;
        }

        /// <summary>Writes the index, most used entries first, capped at <see cref="kMaxEntries"/>.</summary>
        public static void Save()
        {
            if (!s_Dirty || s_File == null)
            {
                return;
            }

            try
            {
                var ordered = new List<KeyValuePair<string, Entry>>(s_Entries);
                ordered.Sort((a, b) => b.Value.Heat != a.Value.Heat
                    ? b.Value.Heat.CompareTo(a.Value.Heat)
                    : b.Value.LastRun.CompareTo(a.Value.LastRun));

                if (ordered.Count > kMaxEntries)
                {
                    ordered.RemoveRange(kMaxEntries, ordered.Count - kMaxEntries);
                }

                BinaryStore.Write(s_File, kMagic, kVersion, writer =>
                {
                    writer.Write(s_Run);
                    writer.Write(ordered.Count);

                    foreach (var pair in ordered)
                    {
                        writer.Write(pair.Key);
                        writer.Write(pair.Value.Heat);
                        writer.Write(pair.Value.LastRun);
                    }
                });

                s_Dirty = false;
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Usage index not saved: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
