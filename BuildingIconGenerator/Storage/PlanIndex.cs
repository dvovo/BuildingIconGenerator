// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Storage
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using BuildingIconGenerator.Textures;
    using UnityEngine;
    using UnityEngine.Experimental.Rendering;

    /// <summary>
    /// Persistent index of texture read plans (<c>cache/vtplans.bin</c>): surface name + mip target
    /// -> where its tiles are in the game's packages (file, offset, length, position in the level)
    /// and how to assemble them.
    ///
    /// Planning a surface means opening its asset stream and reading header, footer and tile table
    /// on the main thread. The answer only changes when the game files do, so plans are kept across
    /// sessions and validated against each package's size and modification time: an update to the
    /// game or to a content pack invalidates exactly the plans that point into the changed files.
    /// Only addresses are stored (plus the raw bytes of very small textures), so the file stays small.
    /// Main thread only.
    /// </summary>
    internal static class PlanIndex
    {
        private const uint kMagic = 0x50474942; // "BIGP"
        // 2: keys carry the surface's content stamp, so plans of updated surfaces are not reused.
        private const int kVersion = 2;

        /// <summary>Largest raw (untiled) texture whose bytes are stored inside a plan.</summary>
        private const int kMaxInlineBytes = 256 * 1024;

        private static readonly Dictionary<string, FastVtLoader.Job> s_Plans = new Dictionary<string, FastVtLoader.Job>(StringComparer.Ordinal);
        private static bool s_Loaded;
        private static bool s_Dirty;
        private static readonly HashSet<string> s_Touched = new HashSet<string>(StringComparer.Ordinal);
        private static string s_File;

        public static int Count => s_Plans.Count;

        /// <summary>Reads the index once per session and drops plans whose package file changed.</summary>
        public static void EnsureLoaded()
        {
            if (s_Loaded)
            {
                return;
            }

            s_Loaded = true;

            try
            {
                s_File = ModPaths.CacheFile("vtplans.bin");
                BinaryStore.Read(s_File, kMagic, kVersion, reader =>
                {
                    int pathCount = reader.ReadInt32();
                    var paths = new string[pathCount];
                    var valid = new bool[pathCount];

                    for (int i = 0; i < pathCount; i++)
                    {
                        paths[i] = reader.ReadString();
                        long length = reader.ReadInt64();
                        long ticks = reader.ReadInt64();

                        try
                        {
                            var info = new FileInfo(paths[i]);
                            valid[i] = info.Exists && info.Length == length && info.LastWriteTimeUtc.Ticks == ticks;
                        }
                        catch
                        {
                            valid[i] = false;
                        }
                    }

                    int count = reader.ReadInt32();

                    for (int e = 0; e < count; e++)
                    {
                        string key = reader.ReadString();
                        var job = ReadJob(reader, paths, valid, out bool usable);

                        if (usable)
                        {
                            s_Plans[key] = job;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                s_Plans.Clear();
                Mod.Log.Warn($"Plan index unreadable, rebuilding it: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>A fresh copy of the stored plan, ready to queue.</summary>
        public static bool TryGet(string key, out FastVtLoader.Job job)
        {
            job = null;

            if (!s_Plans.TryGetValue(key, out var stored))
            {
                return false;
            }

            job = FastVtLoader.Clone(stored);
            s_Touched.Add(key);
            return true;
        }

        /// <summary>Remembers a plan whose tiles are read straight from package files by the workers.</summary>
        public static void Put(string key, FastVtLoader.Job job)
        {
            if (job == null || job.Slow.Count > 0 || !FastVtLoader.Cacheable(job))
            {
                return;
            }

            foreach (var layer in job.Direct)
            {
                if (layer.Pixels == null || layer.Pixels.Length > kMaxInlineBytes)
                {
                    return;
                }
            }

            s_Plans[key] = FastVtLoader.Clone(job);
            s_Touched.Add(key);
            s_Dirty = true;
        }

        /// <summary>
        /// Drops the entries recorded for an earlier version of an asset: when several keys are the
        /// same up to the content stamp (<c>|s:</c>) and one of them was used this session
        /// (<paramref name="touched"/>), the others are outdated. Keeps the files from growing with
        /// every game or mod update. One pass over the keys.
        /// </summary>
        internal static void RemoveOutdated<T>(Dictionary<string, T> entries, HashSet<string> touched)
        {
            if (touched.Count == 0)
            {
                return;
            }

            var current = new HashSet<string>(StringComparer.Ordinal);

            foreach (var key in touched)
            {
                int stamp = key.IndexOf("|s:", StringComparison.Ordinal);

                if (stamp >= 0)
                {
                    current.Add(key.Substring(0, stamp));
                }
            }

            var outdated = new List<string>();

            foreach (var key in entries.Keys)
            {
                int stamp = key.IndexOf("|s:", StringComparison.Ordinal);

                if (stamp >= 0 && !touched.Contains(key) && current.Contains(key.Substring(0, stamp)))
                {
                    outdated.Add(key);
                }
            }

            foreach (var key in outdated)
            {
                entries.Remove(key);
            }
        }

        /// <summary>Writes the index when plans were added since the last save.</summary>
        public static void Save()
        {
            if (!s_Dirty || s_File == null)
            {
                return;
            }

            RemoveOutdated(s_Plans, s_Touched);

            try
            {
                var pathIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                var paths = new List<string>();

                foreach (var job in s_Plans.Values)
                {
                    foreach (var group in job.Groups)
                    {
                        if (group.Path != null && !pathIndex.ContainsKey(group.Path))
                        {
                            pathIndex[group.Path] = paths.Count;
                            paths.Add(group.Path);
                        }
                    }
                }

                BinaryStore.Write(s_File, kMagic, kVersion, writer =>
                {
                    writer.Write(paths.Count);

                    foreach (var path in paths)
                    {
                        var info = new FileInfo(path);
                        writer.Write(path);
                        writer.Write(info.Exists ? info.Length : -1L);
                        writer.Write(info.Exists ? info.LastWriteTimeUtc.Ticks : -1L);
                    }

                    writer.Write(s_Plans.Count);

                    foreach (var pair in s_Plans)
                    {
                        writer.Write(pair.Key);
                        WriteJob(writer, pair.Value, pathIndex);
                    }
                });

                s_Dirty = false;
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Plan index not saved: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void WriteJob(BinaryWriter w, FastVtLoader.Job job, Dictionary<string, int> pathIndex)
        {
            w.Write(job.Name ?? string.Empty);
            w.Write(job.Target);
            w.Write(job.CompressedBytes);
            w.Write(job.TileCount);
            w.Write(job.MissingTextures);
            w.Write(job.Groups.Count);

            foreach (var g in job.Groups)
            {
                w.Write(g.Path != null ? pathIndex[g.Path] : -1);
                w.Write(g.TileSize);
                w.Write(g.BlockWidth);
                w.Write(g.BlockHeight);
                w.Write(g.BlockBytes);
                w.Write(g.ChunkStride);
                w.Write(g.Width);
                w.Write(g.Height);
                w.Write(g.Tiles.Count);

                foreach (var t in g.Tiles)
                {
                    w.Write(t.Offset);
                    w.Write(t.Length);
                    w.Write(t.Row);
                    w.Write(t.Col);
                }

                w.Write(g.Layers.Count);

                foreach (var l in g.Layers)
                {
                    WriteLayer(w, l, false);
                }
            }

            w.Write(job.Direct.Count);

            foreach (var l in job.Direct)
            {
                WriteLayer(w, l, true);
            }
        }

        private static void WriteLayer(BinaryWriter w, FastVtLoader.Layer l, bool pixels)
        {
            w.Write(l.Property ?? string.Empty);
            w.Write((int)l.Format);
            w.Write(l.Slot);
            w.Write(l.Width);
            w.Write(l.Height);
            w.Write(l.MipChain);
            w.Write((int)l.Wrap);
            w.Write(l.Key ?? string.Empty);

            if (pixels)
            {
                w.Write(l.Pixels.Length);
                w.Write(l.Pixels);
            }
        }

        private static FastVtLoader.Job ReadJob(BinaryReader r, string[] paths, bool[] valid, out bool usable)
        {
            usable = true;
            var job = new FastVtLoader.Job
            {
                Name = r.ReadString(),
                Target = r.ReadInt32(),
                CompressedBytes = r.ReadInt64(),
                TileCount = r.ReadInt32(),
                MissingTextures = r.ReadBoolean(),
            };

            int groups = r.ReadInt32();

            for (int gi = 0; gi < groups; gi++)
            {
                int path = r.ReadInt32();
                var g = new FastVtLoader.Group
                {
                    Path = path >= 0 && path < paths.Length ? paths[path] : null,
                    TileSize = r.ReadInt32(),
                    BlockWidth = r.ReadInt32(),
                    BlockHeight = r.ReadInt32(),
                    BlockBytes = r.ReadInt32(),
                    ChunkStride = r.ReadInt32(),
                    Width = r.ReadInt32(),
                    Height = r.ReadInt32(),
                };

                if (path < 0 || path >= valid.Length || !valid[path])
                {
                    usable = false;
                }

                int tiles = r.ReadInt32();

                for (int ti = 0; ti < tiles; ti++)
                {
                    g.Tiles.Add(new FastVtLoader.Tile { Offset = r.ReadInt64(), Length = r.ReadInt32(), Row = r.ReadInt32(), Col = r.ReadInt32() });
                }

                int layers = r.ReadInt32();

                for (int li = 0; li < layers; li++)
                {
                    g.Layers.Add(ReadLayer(r, false));
                }

                job.Groups.Add(g);
            }

            int direct = r.ReadInt32();

            for (int di = 0; di < direct; di++)
            {
                job.Direct.Add(ReadLayer(r, true));
            }

            return job;
        }

        private static FastVtLoader.Layer ReadLayer(BinaryReader r, bool pixels)
        {
            var l = new FastVtLoader.Layer
            {
                Property = r.ReadString(),
                Format = (GraphicsFormat)r.ReadInt32(),
                Slot = r.ReadInt32(),
                Width = r.ReadInt32(),
                Height = r.ReadInt32(),
                MipChain = r.ReadBoolean(),
                Wrap = (TextureWrapMode)r.ReadInt32(),
            };

            string key = r.ReadString();
            l.Key = key.Length > 0 ? key : null;

            if (pixels)
            {
                int n = r.ReadInt32();
                l.Pixels = r.ReadBytes(n);
            }

            return l;
        }
    }
}
