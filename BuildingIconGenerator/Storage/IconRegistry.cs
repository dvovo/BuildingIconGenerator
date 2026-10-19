// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Storage
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Registry of generated icons: which buildings have one, what each was rendered from, and where
    /// it is.
    ///
    /// <para>In memory: a concurrent map building name -> <see cref="Entry"/> (O(1), lock-free
    /// reads, safe from any thread) and the reverse map file -> building. Both maps are replaced as a
    /// whole on <see cref="Load"/>, so readers never observe a half-filled state.</para>
    ///
    /// <para>On disk: <c>icons/index.tsv</c>, one tab-separated line per icon (name, fingerprint,
    /// renderer version, file, size, write time). Lines are appended as icons are written - a run
    /// that is cut short keeps everything already done, and the last line for a name wins - and the
    /// file is rewritten compactly when a run ends.</para>
    ///
    /// <para>Consistency with the folder is checked with a single directory listing (size and date
    /// come with it): entries whose PNG is missing, empty or of a different size are dropped and
    /// reported as vanished, so they are generated again. While the game runs, a
    /// <see cref="FileSystemWatcher"/> reports PNGs deleted or renamed away. When <c>index.tsv</c>
    /// itself is missing it is rebuilt from the PNGs present.</para>
    ///
    /// <para>Every icon has a small copy for the game's UI in <c>icons/thumbnails</c>, under the
    /// same file name. An icon whose thumbnail is missing counts as missing and is rendered again.</para>
    /// </summary>
    internal sealed class IconRegistry
    {
        /// <summary>Subfolder of the icons folder holding the UI thumbnails.</summary>
        public const string ThumbnailFolderName = "thumbnails";

        private const string kHeader = "#building-icon-generator\t1";
        private static readonly Encoding s_Utf8 = new UTF8Encoding(false);

        /// <summary>One recorded icon.</summary>
        public struct Entry
        {
            /// <summary>Hash of the data the icon was rendered from (see the catalogue fingerprint).</summary>
            public ulong Fingerprint;

            /// <summary>Renderer version that produced it.</summary>
            public int Version;

            /// <summary>File name inside the icons folder.</summary>
            public string File;

            /// <summary>Size of the PNG when recorded; -1 when unknown.</summary>
            public long Bytes;

            /// <summary>UTC write time (ticks) of the PNG when recorded; 0 when unknown.</summary>
            public long WrittenTicks;
        }

        private volatile ConcurrentDictionary<string, Entry> m_Entries = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
        private volatile ConcurrentDictionary<string, string> m_ByFile = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> m_Vanished = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> m_Written = new ConcurrentQueue<string>();
        private readonly object m_FileLock = new object();
        private readonly string m_IndexPath;
        private FileSystemWatcher m_Watcher;
        private bool m_WatchWanted = true;

        private IconRegistry(string folder)
        {
            Folder = folder;
            ThumbnailFolder = Path.Combine(folder, ThumbnailFolderName);
            m_IndexPath = Path.Combine(folder, "index.tsv");
        }

        /// <summary>The registry of the icons folder, shared by the generator and the public API.</summary>
        public static IconRegistry Shared { get; private set; }

        /// <summary>Folder holding the icons.</summary>
        public string Folder { get; }

        /// <summary>Folder holding the UI thumbnails (same file names as the icons).</summary>
        public string ThumbnailFolder { get; }

        /// <summary>Number of icons recorded.</summary>
        public int Count => m_Entries.Count;

        /// <summary>Returns the shared registry for <paramref name="folder"/>, creating it if needed.</summary>
        public static IconRegistry OpenShared(string folder)
        {
            if (Shared == null || !string.Equals(Shared.Folder, folder, StringComparison.OrdinalIgnoreCase))
            {
                Shared?.StopWatching();
                Shared = new IconRegistry(folder);
            }

            return Shared;
        }

        /// <summary>
        /// Loads the index (or rebuilds it from the PNGs when it is missing) and validates every
        /// entry against one listing of the folder. <paramref name="catalogue"/>,
        /// <paramref name="fileFor"/> and <paramref name="fingerprintFor"/> are only used to rebuild
        /// a missing index; pass null to load without a catalogue (at mod load, for the API).
        /// Returns a one-line summary for the log.
        /// </summary>
        public string Load(IEnumerable<string> catalogue, Func<string, string> fileFor, Func<string, ulong> fingerprintFor, int version)
        {
            Directory.CreateDirectory(Folder);
            Directory.CreateDirectory(ThumbnailFolder);
            DeleteLeftoverTemporaries();
            var files = new Dictionary<string, (long bytes, long ticks)>(StringComparer.OrdinalIgnoreCase);

            foreach (var info in new DirectoryInfo(Folder).EnumerateFiles("*.png"))
            {
                files[info.Name] = (info.Length, info.LastWriteTimeUtc.Ticks);
            }

            var thumbnails = ListThumbnails();

            string summary;
            bool indexExists;
            var loaded = new Dictionary<string, Entry>(StringComparer.Ordinal);

            lock (m_FileLock)
            {
                indexExists = File.Exists(m_IndexPath);

                if (indexExists)
                {
                    foreach (var raw in File.ReadLines(m_IndexPath, s_Utf8))
                    {
                        string line = raw.TrimStart('﻿');

                        if (line.Length == 0 || line[0] == '#')
                        {
                            continue;
                        }

                        var parts = line.Split('\t');

                        if (parts.Length < 4 || parts[0].Length == 0)
                        {
                            continue;
                        }

                        if (ulong.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong fingerprint)
                            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                        {
                            long bytes = parts.Length > 4 && long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long b) ? b : -1;
                            long ticks = parts.Length > 5 && long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) ? t : 0;
                            loaded[parts[0]] = new Entry { Fingerprint = fingerprint, Version = v, File = parts[3], Bytes = bytes, WrittenTicks = ticks };
                        }
                    }

                    summary = $"index read ({loaded.Count} icon(s))";
                }
                else if (catalogue != null && fileFor != null && fingerprintFor != null)
                {
                    foreach (var name in catalogue)
                    {
                        string file = fileFor(name);

                        if (files.TryGetValue(file, out var found))
                        {
                            loaded[name] = new Entry { Fingerprint = fingerprintFor(name), Version = version, File = file, Bytes = found.bytes, WrittenTicks = found.ticks };
                        }
                    }

                    summary = $"index rebuilt from {files.Count} PNG(s), {loaded.Count} matched the catalogue";
                }
                else
                {
                    summary = "no index yet";
                }
            }

            int gone = 0;
            int withoutThumbnail = 0;
            var entries = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
            var byFile = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in loaded)
            {
                // Missing, empty or altered while the game was closed: reported like a live deletion,
                // so it is regenerated even when no catalogue run follows.
                if (!files.TryGetValue(pair.Value.File, out var found) || found.bytes <= 0
                    || (pair.Value.Bytes >= 0 && pair.Value.Bytes != found.bytes))
                {
                    gone++;
                    m_Vanished.Enqueue(pair.Key);
                    continue;
                }

                // Icons written before thumbnails existed, or whose thumbnail was deleted.
                if (!thumbnails.Contains(pair.Value.File))
                {
                    withoutThumbnail++;
                    m_Vanished.Enqueue(pair.Key);
                    continue;
                }

                var entry = pair.Value;
                entry.Bytes = found.bytes;
                entry.WrittenTicks = found.ticks;
                entries[pair.Key] = entry;
                byFile[entry.File] = pair.Key;
            }

            m_Entries = entries;
            m_ByFile = byFile;

            if (gone > 0)
            {
                summary += $", {gone} missing or altered PNG(s) queued for regeneration";
            }

            if (withoutThumbnail > 0)
            {
                summary += $", {withoutThumbnail} icon(s) without a UI thumbnail queued for regeneration";
            }

            // Without an index and without a catalogue to rebuild it from, nothing is written: the
            // next load with a catalogue still rebuilds the index from the PNGs present.
            if (indexExists || loaded.Count > 0)
            {
                Save();
            }

            StartWatching();
            return summary;
        }

        /// <summary>Looks up the entry of a building.</summary>
        public bool TryGet(string name, out Entry entry)
        {
            entry = default;
            return name != null && m_Entries.TryGetValue(name, out entry);
        }

        /// <summary>Full path of the icon of a building, or null.</summary>
        public string PathOf(string name)
        {
            return TryGet(name, out var entry) ? Path.Combine(Folder, entry.File) : null;
        }

        /// <summary>Full path of the UI thumbnail of a building, or null.</summary>
        public string ThumbnailPathOf(string name)
        {
            return TryGet(name, out var entry) ? Path.Combine(ThumbnailFolder, entry.File) : null;
        }

        /// <summary>Where the UI thumbnail of the icon file <paramref name="file"/> is written.</summary>
        public string ThumbnailPathFor(string file)
        {
            return Path.Combine(ThumbnailFolder, file);
        }

        /// <summary>True when the icon exists and was rendered from the same data by the same renderer.</summary>
        public bool IsCurrent(string name, ulong fingerprint, int version)
        {
            return TryGet(name, out var entry) && entry.Fingerprint == fingerprint && entry.Version == version;
        }

        /// <summary>Snapshot of every building with an icon.</summary>
        public List<string> Names()
        {
            return new List<string>(m_Entries.Keys);
        }

        /// <summary>Records a finished icon. Thread-safe: called from the PNG writer task.</summary>
        public void Record(string name, ulong fingerprint, int version, string file)
        {
            long bytes = -1;
            long ticks = 0;

            try
            {
                var info = new FileInfo(Path.Combine(Folder, file));

                if (info.Exists)
                {
                    bytes = info.Length;
                    ticks = info.LastWriteTimeUtc.Ticks;
                }
            }
            catch
            {
                // Size and date stay unknown; the entry is still valid.
            }

            var entry = new Entry { Fingerprint = fingerprint, Version = version, File = file, Bytes = bytes, WrittenTicks = ticks };

            // A building whose file name changed (naming rules of an earlier version) leaves no stray copy behind.
            // Only a file this building still owns: another building may have taken that name since.
            if (m_Entries.TryGetValue(name, out var previous) && previous.File != null
                && !string.Equals(previous.File, file, StringComparison.OrdinalIgnoreCase)
                && RemoveFileOwner(previous.File, name))
            {
                TryDeleteIcon(previous.File);
            }

            m_Entries[name] = entry;
            m_ByFile[file] = name;
            m_Written.Enqueue(name);

            lock (m_FileLock)
            {
                try
                {
                    if (!File.Exists(m_IndexPath))
                    {
                        File.AppendAllText(m_IndexPath, kHeader + "\n", s_Utf8);
                    }

                    File.AppendAllText(m_IndexPath, Line(name, entry), s_Utf8);
                }
                catch
                {
                    // The PNG is on disk; a lost line is recovered by the rebuild path on next load.
                }
            }
        }

        /// <summary>
        /// Deletes the icons of every building not in <paramref name="installed"/>, and every PNG of
        /// the folder that belongs to no building. Files named in <paramref name="protectedFiles"/>
        /// (the file names of the installed buildings) are never deleted, even when the index does
        /// not list them yet. Returns the number of files deleted.
        /// </summary>
        public int Prune(ICollection<string> installed, ICollection<string> protectedFiles)
        {
            int deleted = 0;

            foreach (var pair in m_Entries)
            {
                if (installed.Contains(pair.Key))
                {
                    continue;
                }

                m_Entries.TryRemove(pair.Key, out _);

                // The file may belong to an installed building as well (same name apart from case).
                if (RemoveFileOwner(pair.Value.File, pair.Key)
                    && !protectedFiles.Contains(pair.Value.File) && TryDeleteIcon(pair.Value.File))
                {
                    deleted++;
                }
            }

            try
            {
                foreach (var info in new DirectoryInfo(Folder).EnumerateFiles("*.png"))
                {
                    if (!m_ByFile.ContainsKey(info.Name) && !protectedFiles.Contains(info.Name) && TryDeleteIcon(info.Name))
                    {
                        deleted++;
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Stray icons not checked: {ex.GetType().Name}: {ex.Message}");
            }

            // Thumbnails are not counted: each one goes with an icon counted above.
            foreach (var file in ListThumbnails())
            {
                if (!m_ByFile.ContainsKey(file) && !protectedFiles.Contains(file))
                {
                    TryDeleteFile(ThumbnailFolder, file);
                }
            }

            Save();
            return deleted;
        }

        /// <summary>Takes the next building whose PNG disappeared (drained by the generator).</summary>
        public bool TryTakeVanished(out string name)
        {
            return m_Vanished.TryDequeue(out name);
        }

        /// <summary>Takes the next building whose icon was written (drained on the main thread).</summary>
        public bool TryTakeWritten(out string name)
        {
            return m_Written.TryDequeue(out name);
        }

        /// <summary>Rewrites the index with one line per icon, replacing the old file atomically.</summary>
        public void Save()
        {
            lock (m_FileLock)
            {
                try
                {
                    var builder = new StringBuilder(kHeader.Length + (m_Entries.Count * 120));
                    builder.Append(kHeader).Append('\n');

                    foreach (var pair in m_Entries)
                    {
                        builder.Append(Line(pair.Key, pair.Value));
                    }

                    string temporary = m_IndexPath + ".tmp";
                    File.WriteAllText(temporary, builder.ToString(), s_Utf8);

                    if (File.Exists(m_IndexPath))
                    {
                        File.Replace(temporary, m_IndexPath, null);
                    }
                    else
                    {
                        File.Move(temporary, m_IndexPath);
                    }
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn($"Icon index not saved: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>FNV-1a 64-bit hash of a string (fingerprints).</summary>
        public static ulong Hash(string text)
        {
            ulong hash = 14695981039346656037UL;

            foreach (char c in text)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }

            return hash;
        }

        /// <summary>
        /// Watches the folder for deleted icons while <paramref name="on"/> (at the main menu, where
        /// they are healed) and stops otherwise: on Windows the watcher polls the whole folder, which
        /// is not worth paying for during gameplay. Turning it back on checks the folder once, on a
        /// worker thread, for icons deleted meanwhile.
        /// </summary>
        public void Watch(bool on)
        {
            if (on == m_WatchWanted)
            {
                return;
            }

            m_WatchWanted = on;

            if (!on)
            {
                StopWatching();
                return;
            }

            StartWatching();
            Task.Run(() =>
            {
                try
                {
                    var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var info in new DirectoryInfo(Folder).EnumerateFiles("*.png"))
                    {
                        present.Add(info.Name);
                    }

                    var thumbnails = ListThumbnails();

                    foreach (var file in m_ByFile.Keys)
                    {
                        if (!present.Contains(file))
                        {
                            OnVanished(file);
                        }
                        else if (!thumbnails.Contains(file))
                        {
                            Forget(file);
                        }
                    }
                }
                catch
                {
                    // The validation on the next load still catches deletions.
                }
            });
        }

        private void StartWatching()
        {
            if (m_Watcher != null)
            {
                return;
            }

            try
            {
                m_Watcher = new FileSystemWatcher(Folder, "*.png")
                {
                    NotifyFilter = NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                };
                m_Watcher.Deleted += (sender, e) => OnVanished(e.Name);
                m_Watcher.Renamed += (sender, e) => OnVanished(e.OldName);
                m_Watcher.EnableRaisingEvents = true;
            }
            catch
            {
                // No watcher on this platform: the validation on the next load still catches deletions.
                m_Watcher = null;
            }
        }

        private void StopWatching()
        {
            try
            {
                m_Watcher?.Dispose();
            }
            catch
            {
                // Disposal failure leaves nothing to clean up.
            }

            m_Watcher = null;
        }

        private void OnVanished(string file)
        {
            // An icon being replaced by a newer one is reported by some watchers as deleted; it is not gone.
            if (file == null || File.Exists(Path.Combine(Folder, file)))
            {
                return;
            }

            Forget(file);
        }

        /// <summary>Drops the entry owning <paramref name="file"/> and queues the building for rendering.</summary>
        private void Forget(string file)
        {
            if (m_ByFile.TryRemove(file, out string name))
            {
                m_Entries.TryRemove(name, out _);
                m_Vanished.Enqueue(name);
            }
        }

        /// <summary>File names of the thumbnails present (empty when the folder cannot be read).</summary>
        private HashSet<string> ListThumbnails()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (Directory.Exists(ThumbnailFolder))
                {
                    foreach (var info in new DirectoryInfo(ThumbnailFolder).EnumerateFiles("*.png"))
                    {
                        if (info.Length > 0)
                        {
                            names.Add(info.Name);
                        }
                    }
                }
            }
            catch
            {
                // Treated as no thumbnails: the icons are rendered again, which writes them.
            }

            return names;
        }

        /// <summary>Unmaps <paramref name="file"/> only when it still belongs to <paramref name="name"/> (atomic).</summary>
        private bool RemoveFileOwner(string file, string name)
        {
            return ((ICollection<KeyValuePair<string, string>>)m_ByFile).Remove(new KeyValuePair<string, string>(file, name));
        }

        /// <summary>Deletes one icon and its thumbnail. False when the icon could not be deleted (or was not there).</summary>
        private bool TryDeleteIcon(string file)
        {
            TryDeleteFile(ThumbnailFolder, file);
            return TryDeleteFile(Folder, file);
        }

        /// <summary>Deletes one PNG of <paramref name="folder"/>. False when it could not be deleted (or was not there).</summary>
        private static bool TryDeleteFile(string folder, string file)
        {
            try
            {
                string path = Path.Combine(folder, file);

                if (!file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file) != file || !File.Exists(path))
                {
                    return false;
                }

                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Icon '{file}' not deleted: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Removes temporary files left by a write cut short (the game was killed mid-write).</summary>
        private void DeleteLeftoverTemporaries()
        {
            foreach (var folder in new[] { Folder, ThumbnailFolder })
            {
                DeleteLeftoverTemporaries(folder);
            }
        }

        private static void DeleteLeftoverTemporaries(string folder)
        {
            try
            {
                foreach (var info in new DirectoryInfo(folder).EnumerateFiles("*.png.tmp"))
                {
                    try
                    {
                        info.Delete();
                    }
                    catch
                    {
                        // Still being written by another instance, or locked: tried again next time.
                    }
                }
            }
            catch
            {
                // Nothing to clean up.
            }
        }

        private static string Line(string name, Entry entry)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}\t{1:x16}\t{2}\t{3}\t{4}\t{5}\n",
                name,
                entry.Fingerprint,
                entry.Version,
                entry.File,
                entry.Bytes,
                entry.WrittenTicks);
        }
    }
}
