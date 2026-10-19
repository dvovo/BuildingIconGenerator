// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Systems
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using BuildingIconGenerator.Api;
    using BuildingIconGenerator.Localization;
    using BuildingIconGenerator.Rendering;
    using BuildingIconGenerator.Scheduling;
    using BuildingIconGenerator.Storage;
    using Colossal.IO.AssetDatabase;
    using Game;
    using Game.Prefabs;
    using Game.UI.Localization;
    using Game.UI.Menu;
    using Unity.Collections;
    using UnityEngine;
    using ProgressState = Colossal.PSI.Common.ProgressState;

    /// <content>Catalogue, fingerprints, the run queue, the frame budget and progress reporting.</content>
    public sealed partial class IconGeneratorSystem
    {
        /// <summary>
        /// Version of the icon renderer. Raise it whenever the look of the icons changes (lighting,
        /// colour, ground, framing...): every icon recorded with an older version is regenerated.
        /// </summary>
        private const int kRendererVersion = 1;

        /// <summary>Buildings whose assets the run caches roughly hold at once (generation-order window).</summary>
        private const int kPlanWindow = 24;

        /// <summary>Main-thread milliseconds per frame while a city is loaded.</summary>
        private const long kGameFrameBudgetMs = 8;

        /// <summary>Main-thread milliseconds per frame at the main menu while the user interacts.</summary>
        private const long kMenuFrameBudgetMs = 20;

        /// <summary>Main-thread milliseconds per frame at the main menu after <see cref="kIdleSeconds"/> without input.</summary>
        private const long kMenuIdleFrameBudgetMs = 100;

        /// <summary>Seconds without mouse or keyboard input after which the main menu counts as idle.</summary>
        private const float kIdleSeconds = 2f;

        /// <summary>A run stops after this many PNG writes failed in a row (folder read-only, disk full).</summary>
        private const int kMaxConsecutiveWriteFailures = 20;

        /// <summary>Icons composed but not yet written above which the run waits for the disk.</summary>
        private static readonly int s_MaxPendingWork = Math.Max(8, Environment.ProcessorCount * 2);

        private const string kNotificationId = Mod.Id + ".Progress";

        // The game sets a notification's title only when it is created, so the "finished" message
        // replaces the progress notification with one of its own instead of updating it.
        private const string kFinishedNotificationId = Mod.Id + ".Finished";
        private const string kCleanupNotificationId = Mod.Id + ".Cleanup";

        /// <summary>Yielded by a capture step that must end the current frame (one GPU pass per frame).</summary>
        private static readonly object s_EndFrame = new object();

        private readonly Dictionary<string, ulong> m_Fingerprints = new Dictionary<string, ulong>(StringComparer.Ordinal);
        private readonly Dictionary<string, PrefabBase> m_CatalogueByName = new Dictionary<string, PrefabBase>(StringComparer.Ordinal);

        /// <summary>Content stamps of the assets read for the fingerprints of one run start (see <see cref="AppendStamp"/>).</summary>
        private static readonly Dictionary<AssetData, string> s_Stamps = new Dictionary<AssetData, string>();

        /// <summary>Catalogue buildings that spawn in zones (zoned and signature buildings).</summary>
        private readonly HashSet<string> m_SpawnableNames = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Buildings whose file name would collide with another one's (see <see cref="IconFileName"/>).</summary>
        private readonly HashSet<string> m_FileNameClashes = new HashSet<string>(StringComparer.Ordinal);
        private readonly Stopwatch m_FrameWatch = new Stopwatch();

        private ShaderBundle m_Shaders;
        private SurfaceBaker m_Baker;
        private CaptureRig m_Rig;
        private bool m_ShaderErrorLogged;

        private List<PrefabBase> m_Queue;
        private int m_QueueIndex;
        private MaterialLibrary m_Library;
        private IconRegistry m_Registry;
        private string m_RunReason;

        /// <summary>Icons written by the run in progress (incremented by the PNG writers).</summary>
        private int m_RunWritten;

        /// <summary>Buildings of the run in progress that could not be drawn.</summary>
        private int m_RunFailed;

        /// <summary>True when the run in progress was started from the options page.</summary>
        private bool m_RunManual;

        /// <summary>True when the pending run continues an automatic run suspended outside the main menu.</summary>
        private bool m_ResumePending;
        private Stopwatch m_RunWatch;
        private IEnumerator m_Current;
        private ulong m_ContentHash;

        private long m_FrameBudgetMs = kMenuFrameBudgetMs;
        private float m_LastInputTime;
        private int m_SavedVSync = -1;
        private int m_SavedTargetFrameRate;
        private bool m_PacingLifted;
        private bool m_ProgressUnavailable;

        /// <summary>
        /// Every building with geometry currently loaded, sorted by name: zoned and signature
        /// buildings (spawnable), city services, transport, parks, unique buildings and building
        /// upgrades (extensions). Placeholders, which only stand for a random choice of other
        /// buildings, are left out. Spawnable buildings are also listed in <see cref="m_SpawnableNames"/>.
        /// </summary>
        private List<PrefabBase> Catalogue()
        {
            var result = new List<PrefabBase>();
            m_SpawnableNames.Clear();
            var entities = m_BuildingQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    try
                    {
                        var prefab = m_PrefabSystem.GetPrefab<PrefabBase>(entities[i]);

                        if (prefab is ObjectGeometryPrefab geometry && geometry.m_Meshes != null && geometry.m_Meshes.Length > 0)
                        {
                            result.Add(prefab);

                            if (EntityManager.HasComponent<SpawnableBuildingData>(entities[i]))
                            {
                                m_SpawnableNames.Add(prefab.name);
                            }
                        }
                    }
                    catch
                    {
                        // A prefab that cannot be resolved is simply not part of the catalogue.
                    }
                }
            }
            finally
            {
                entities.Dispose();
            }

            result.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            m_CatalogueByName.Clear();

            foreach (var prefab in result)
            {
                m_CatalogueByName[prefab.name] = prefab;
            }

            FindFileNameClashes(result);
            return result;
        }

        /// <summary>
        /// Lot of a building in 8 m cells: a building's own lot, or the lot size an upgrade
        /// (building extension) overrides. False when the building has no lot to draw.
        /// </summary>
        private static bool TryGetLot(PrefabBase prefab, out int width, out int depth)
        {
            width = depth = 0;

            if (prefab is BuildingPrefab building)
            {
                width = building.m_LotWidth;
                depth = building.m_LotDepth;
            }
            else if (prefab is BuildingExtensionPrefab extension)
            {
                width = extension.m_OverrideLotSize.x;
                depth = extension.m_OverrideLotSize.y;
            }

            return width > 0 && depth > 0;
        }

        /// <summary>
        /// Hash (FNV-1a 64) of what an icon is rendered from: meshes and their states, the content of
        /// the building's own assets (models and materials: identity, size and date, see
        /// <see cref="AppendStamp"/>), lot size, sub-objects with their placement, sub-areas and
        /// sub-lanes. A game or mod update that changes a building - its composition or the content
        /// of its models and materials - changes its fingerprint, and only that icon is regenerated.
        /// </summary>
        private static ulong Fingerprint(PrefabBase prefab)
        {
            var text = new StringBuilder(256);
            text.Append(prefab.GetType().Name).Append('|').Append(prefab.name);

            try
            {
                AppendStamp(text, prefab.asset);

                if (prefab is ObjectGeometryPrefab geometry && geometry.m_Meshes != null)
                {
                    foreach (var mesh in geometry.m_Meshes)
                    {
                        text.Append("|m:").Append(mesh?.m_Mesh != null ? mesh.m_Mesh.name : "-").Append(':').Append(mesh?.m_RequireState.ToString());

                        if (mesh?.m_Mesh is RenderPrefab render)
                        {
                            AppendStamp(text, render.asset);
                            AppendStamp(text, render.geometryAsset);

                            foreach (var surface in render.surfaceAssets)
                            {
                                AppendStamp(text, surface);
                            }
                        }
                    }
                }

                if (TryGetLot(prefab, out int lotWidth, out int lotDepth))
                {
                    text.Append("|lot:").Append(lotWidth).Append('x').Append(lotDepth);
                }

                if (prefab.TryGet(out ObjectSubObjects objects) && objects.m_SubObjects != null)
                {
                    foreach (var o in objects.m_SubObjects)
                    {
                        text.Append("|o:").Append(o?.m_Object != null ? o.m_Object.name : "-")
                            .Append('@').Append(o != null ? FormattableString.Invariant($"{o.m_Position.x:F1},{o.m_Position.y:F1},{o.m_Position.z:F1}") : string.Empty);
                    }
                }

                if (prefab.TryGet(out ObjectSubAreas areas) && areas.m_SubAreas != null)
                {
                    foreach (var a in areas.m_SubAreas)
                    {
                        text.Append("|a:").Append(a?.m_AreaPrefab != null ? ((PrefabBase)a.m_AreaPrefab).name : "-")
                            .Append(':').Append(a?.m_NodePositions?.Length ?? 0);
                    }
                }

                if (prefab.TryGet(out ObjectSubLanes lanes) && lanes.m_SubLanes != null)
                {
                    foreach (var l in lanes.m_SubLanes)
                    {
                        text.Append("|l:").Append(l?.m_LanePrefab != null ? ((PrefabBase)l.m_LanePrefab).name : "-");
                    }
                }
            }
            catch
            {
                // A partial description still identifies the building; it is only less precise.
            }

            return IconRegistry.Hash(text.ToString());
        }

        /// <summary>
        /// Appends what identifies the content of an asset: its id, size and modification date, as
        /// recorded by the game's asset database. A model or a texture set re-exported by a mod
        /// update changes at least one of them. Unknown parts are written as '?'.
        /// </summary>
        private static void AppendStamp(StringBuilder text, AssetData asset)
        {
            text.Append("|s:");

            if (asset == null)
            {
                text.Append('-');
                return;
            }

            // Reading an asset's metadata checks its file on disk; many buildings share assets, so
            // each one is read once per run start.
            if (!s_Stamps.TryGetValue(asset, out string stamp))
            {
                try
                {
                    var meta = asset.GetMeta();
                    stamp = ((Colossal.Hash128)asset.id).ToString() + ":" + meta.size.ToString(CultureInfo.InvariantCulture)
                            + ":" + meta.lastWriteTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                }
                catch
                {
                    stamp = "?";
                }

                s_Stamps[asset] = stamp;
            }

            text.Append(stamp);
        }

        /// <summary>
        /// Hash of the installed content: the prerequisites the game reports as available (DLC and
        /// content packs) and the number of loaded prefabs (mods). An icon drawn with some element
        /// missing is recorded against this hash, so it is regenerated when content changes.
        /// </summary>
        private ulong ContentHash()
        {
            if (m_ContentHash != 0)
            {
                return m_ContentHash;
            }

            var text = new StringBuilder();

            try
            {
                var names = m_PrefabSystem.GetAvailablePrerequisitesNames();

                if (names != null)
                {
                    foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
                    {
                        text.Append(name).Append('|');
                    }
                }
            }
            catch
            {
                // Without the prerequisite list the prefab count below still tracks content changes.
            }

            try
            {
                var prefabs = typeof(PrefabSystem).GetField("m_Prefabs", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(m_PrefabSystem) as ICollection;
                text.Append("prefabs:").Append(prefabs?.Count ?? -1);
            }
            catch
            {
                // Same as above.
            }

            m_ContentHash = IconRegistry.Hash(text.ToString()) | 1UL;
            return m_ContentHash;
        }

        /// <summary>Longest prefab name kept as is in a file name; longer ones are shortened.</summary>
        private const int kMaxFileNameLength = 120;

        private static readonly HashSet<string> s_ReservedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// File name of a building's icon: the prefab name followed by <c>.png</c>, so a building's
        /// icon can be found from its name alone. Only names Windows cannot store as they are get a
        /// different file name: names with a character Windows forbids (<c>\ / : * ? " &lt; &gt; |</c>
        /// or a control character, replaced by <c>_</c>), reserved device names (<c>CON</c>,
        /// <c>NUL</c>…) and names longer than <see cref="kMaxFileNameLength"/>; those get a short
        /// hash of the original name appended. Two names that differ only in letter case (Windows
        /// ignores case) also get one, so every building has a file of its own.
        /// </summary>
        private string IconFileName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
            {
                return "unnamed.png";
            }

            string name = BaseFileName(prefabName);

            if (m_FileNameClashes.Contains(prefabName))
            {
                name += "_" + ShortHash(prefabName + "|clash");
            }

            return name + ".png";
        }

        /// <summary>The prefab name made safe for a file name, without extension (see <see cref="IconFileName"/>).</summary>
        private static string BaseFileName(string prefabName)
        {
            var builder = new StringBuilder(prefabName.Length + 9);
            bool changed = false;

            foreach (char c in prefabName)
            {
                bool forbidden = c < ' ' || c == '\\' || c == '/' || c == ':' || c == '*' || c == '?'
                    || c == '"' || c == '<' || c == '>' || c == '|';
                builder.Append(forbidden ? '_' : c);
                changed |= forbidden;
            }

            // Windows refuses device names whatever follows the first dot ("NUL.png", "CON.x.png").
            int dot = prefabName.IndexOf('.');
            string stem = (dot < 0 ? prefabName : prefabName.Substring(0, dot)).TrimEnd(' ');
            changed |= s_ReservedFileNames.Contains(stem);

            if (builder.Length > kMaxFileNameLength)
            {
                builder.Length = kMaxFileNameLength;
                changed = true;
            }

            if (changed)
            {
                builder.Append('_').Append(ShortHash(prefabName));
            }

            return builder.ToString();
        }

        private static string ShortHash(string text)
        {
            return ((uint)IconRegistry.Hash(text)).ToString("x8", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Finds the buildings whose file names would be the same apart from letter case. In each such
        /// group the name that sorts first (ordinal) keeps the plain file name; only the others are
        /// marked, so the plain name stays valid for one of them whatever the loading order.
        /// </summary>
        private void FindFileNameClashes(List<PrefabBase> prefabs)
        {
            m_FileNameClashes.Clear();
            var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prefab in prefabs)
            {
                string name = prefab.name;

                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string file = BaseFileName(name);

                if (!owner.TryGetValue(file, out string current))
                {
                    owner[file] = name;
                }
                else if (string.CompareOrdinal(name, current) < 0)
                {
                    m_FileNameClashes.Add(current);
                    owner[file] = name;
                }
                else if (name != current)
                {
                    m_FileNameClashes.Add(name);
                }
            }
        }

        /// <summary>Loads the shaders and creates the capture rig once. False when icons cannot be rendered.</summary>
        private bool EnsureRenderer()
        {
            if (m_Shaders == null)
            {
                m_Shaders = ShaderBundle.TryLoad(out string error);

                if (m_Shaders == null)
                {
                    if (!m_ShaderErrorLogged)
                    {
                        m_ShaderErrorLogged = true;
                        Mod.Log.Error($"Icons cannot be generated: {error}. Reinstall the mod.");
                    }

                    return false;
                }

                m_Baker = new SurfaceBaker(m_Shaders.Bake);
            }

            m_Rig = m_Rig ?? new CaptureRig();
            return m_Rig.Setup(m_Shaders.Compose);
        }

        /// <summary>
        /// Starts a run over <paramref name="prefabs"/>: every building whose icon is missing or out of
        /// date (or all of them with <paramref name="regenerateAll"/>), in an order that maximises
        /// asset reuse. Buildings requested through the API are always included. Returns false only
        /// when the run should be retried later (the game's material library is not available yet).
        /// </summary>
        private bool StartRun(List<PrefabBase> prefabs, bool regenerateAll, string reason)
        {
            if (prefabs == null || prefabs.Count == 0 || m_Queue != null || !EnsureRenderer())
            {
                return true;
            }

            m_Library = AssetDatabase.global?.resources?.materialLibrary;

            if (m_Library == null)
            {
                return false;
            }

            var forced = new HashSet<string>(m_ForceNames, StringComparer.Ordinal);
            m_ForceNames.Clear();

            var watch = Stopwatch.StartNew();
            m_Fingerprints.Clear();
            m_ContentHash = 0;
            m_SurfacesMissingTextures.Clear();

            s_Stamps.Clear();

            foreach (var prefab in prefabs)
            {
                m_Fingerprints[prefab.name] = Fingerprint(prefab);
            }

            s_Stamps.Clear();
            long fingerprintMs = watch.ElapsedMilliseconds;

            m_Registry = IconRegistry.OpenShared(ModPaths.IconsFolder);
            string summary = m_Registry.Load(
                m_Fingerprints.Keys,
                IconFileName,
                name => m_Fingerprints.TryGetValue(name, out ulong f) ? f : 0UL,
                kRendererVersion);

            var pending = new List<PrefabBase>();

            foreach (var prefab in prefabs)
            {
                ulong fingerprint = m_Fingerprints[prefab.name];
                bool current = m_Registry.IsCurrent(prefab.name, fingerprint, kRendererVersion)
                               || m_Registry.IsCurrent(prefab.name, fingerprint ^ ContentHash(), kRendererVersion);

                if (regenerateAll || forced.Contains(prefab.name) || !current)
                {
                    pending.Add(prefab);
                }
            }

            Mod.Log.Info($"{reason}: {prefabs.Count} building(s), {prefabs.Count - pending.Count} icon(s) up to date, "
                         + $"{pending.Count} to generate ({summary}; catalogue read in {fingerprintMs} ms)");

            if (pending.Count == 0)
            {
                return true;
            }

            CaptureRig.ResetWriteFailures();
            m_RunWritten = 0;
            m_RunFailed = 0;
            m_Queue = PlanOrder(pending);
            m_QueueIndex = 0;
            m_RunReason = reason;
            m_RunManual = m_ManualRunPending;
            m_RunWatch = watch;
            BuildingIcons.IsGenerating = true;
            ShowProgress();
            EnsureTicking();
            return true;
        }

        /// <summary>Generation order (see <see cref="GenerationPlanner"/>); the input order on failure.</summary>
        private static List<PrefabBase> PlanOrder(List<PrefabBase> pending)
        {
            if (pending.Count < 3)
            {
                return pending;
            }

            try
            {
                var items = new List<GenerationPlanner.Item>(pending.Count);

                foreach (var prefab in pending)
                {
                    items.Add(PlanFeatures(prefab));
                }

                var result = new List<PrefabBase>(pending.Count);

                foreach (int index in GenerationPlanner.Order(items, kPlanWindow))
                {
                    result.Add(pending[index]);
                }

                return result;
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Generation order not computed, using catalogue order: {ex.GetType().Name}: {ex.Message}");
                return pending;
            }
        }

        /// <summary>
        /// What a building loads, read cheaply (no meshes, no textures): its own meshes and their
        /// surfaces (large atlases, weight 8) and the objects and lanes it places (weight 1).
        /// </summary>
        private static GenerationPlanner.Item PlanFeatures(PrefabBase prefab)
        {
            var item = new GenerationPlanner.Item
            {
                Name = prefab.name,
                Family = GenerationPlanner.FamilyOf(prefab.name),
                Level = GenerationPlanner.LevelOf(prefab.name),
            };

            if (TryGetLot(prefab, out int lotWidth, out int lotDepth))
            {
                item.LotArea = lotWidth * lotDepth;
            }

            try
            {
                if (prefab is ObjectGeometryPrefab geometry && geometry.m_Meshes != null)
                {
                    foreach (var info in geometry.m_Meshes)
                    {
                        if (info?.m_Mesh is RenderPrefab render)
                        {
                            item.Assets["r:" + render.name] = 8f;

                            foreach (var surface in render.surfaceAssets)
                            {
                                if (surface != null)
                                {
                                    item.Assets["s:" + surface.name] = 8f;
                                }
                            }
                        }
                    }
                }

                if (prefab.TryGet(out ObjectSubObjects subObjects) && subObjects.m_SubObjects != null)
                {
                    foreach (var sub in subObjects.m_SubObjects)
                    {
                        if (sub?.m_Object != null)
                        {
                            item.Assets["o:" + sub.m_Object.name] = 1f;
                        }
                    }
                }

                if (prefab.TryGet(out ObjectSubLanes subLanes) && subLanes.m_SubLanes != null)
                {
                    foreach (var lane in subLanes.m_SubLanes)
                    {
                        if (lane?.m_LanePrefab != null)
                        {
                            item.Assets["l:" + lane.m_LanePrefab.name] = 1f;
                        }
                    }
                }
            }
            catch
            {
                // Fewer known assets only make the ordering less informed.
            }

            return item;
        }

        /// <summary>
        /// Advances the run within this frame's budget. Several small buildings can finish in one
        /// frame; a capture step that needs a fresh frame (a GPU pass) ends it. Steps are never split
        /// below one mesh or one render pass, so a frame can overrun on a large texture read, but the
        /// game never stalls for a whole building.
        /// </summary>
        private void ProcessNext()
        {
            if (CaptureRig.ConsecutiveWriteFailures >= kMaxConsecutiveWriteFailures)
            {
                Mod.Log.Error($"{m_RunReason}: stopped, the icons folder cannot be written ({ModPaths.IconsFolder}).");
                AbortRun();
                return;
            }

            // Back-pressure: let the disk catch up before composing more icons.
            if (CaptureRig.PendingWork >= s_MaxPendingWork)
            {
                return;
            }

            m_FrameWatch.Restart();
            UpdateFrameBudget();
            LiftFramePacing();

            while (true)
            {
                if (m_Current == null)
                {
                    if (m_QueueIndex >= m_Queue.Count)
                    {
                        break;
                    }

                    var prefab = m_Queue[m_QueueIndex++];
                    UsageIndex.EnsureLoaded();
                    UsageIndex.NextBuilding();
                    m_BuildingSerial++;

                    try
                    {
                        m_Current = BeginCapture(prefab);
                    }
                    catch (Exception ex)
                    {
                        m_RunFailed++;
                        Mod.Log.Warn($"'{prefab?.name}' skipped: {ex.GetType().Name}: {ex.Message}");
                    }

                    if (m_Current == null)
                    {
                        if (m_FrameWatch.ElapsedMilliseconds < m_FrameBudgetMs)
                        {
                            continue;
                        }

                        return;
                    }
                }

                bool more = false;
                bool endFrame = false;

                try
                {
                    do
                    {
                        more = m_Current.MoveNext();
                    }
                    while (more && m_Current.Current != s_EndFrame && m_FrameWatch.ElapsedMilliseconds < m_FrameBudgetMs);

                    endFrame = more && m_Current.Current == s_EndFrame;
                }
                catch (Exception ex)
                {
                    m_RunFailed++;
                    Mod.Log.Warn($"Icon of '{m_Queue[m_QueueIndex - 1]?.name}' failed: {ex.GetType().Name}: {ex.Message}");
                    more = false;
                }

                if (more)
                {
                    return;
                }

                (m_Current as IDisposable)?.Dispose();
                m_Current = null;
                ShowProgress();
                EnforceBudgets();

                if (endFrame || m_FrameWatch.ElapsedMilliseconds >= m_FrameBudgetMs)
                {
                    return;
                }
            }

            FinishRun();
        }

        private void FinishRun()
        {
            int total = m_Queue?.Count ?? 0;
            EndRun();

            double seconds = m_RunWatch != null ? m_RunWatch.Elapsed.TotalSeconds : 0;
            int written = System.Threading.Volatile.Read(ref m_RunWritten);
            ShowProgress(finished: true, written, seconds);
            Mod.Log.Info(FormattableString.Invariant(
                $"{m_RunReason}: {written} icon(s) generated in {seconds:F1} s ({total / Math.Max(0.001, seconds) * 60:F0} buildings per minute)")
                + (m_RunFailed > 0 || written + m_RunFailed < total
                    ? $"; {m_RunFailed} failed, {Math.Max(0, total - written - m_RunFailed)} with nothing to draw or not written (see the warnings above)"
                    : string.Empty));
        }

        /// <summary>
        /// The player left the main menu mid-run: rendering stops at once (frame pacing, the capture
        /// in progress and the run caches are released) and the unfinished buildings are queued for
        /// the next visit to the menu, so a "regenerate all" run also resumes where it stopped.
        /// </summary>
        private void SuspendRun()
        {
            if (m_Queue == null)
            {
                return;
            }

            int start = Math.Max(0, m_QueueIndex - (m_Current != null ? 1 : 0));

            for (int i = start; i < m_Queue.Count; i++)
            {
                m_ForceNames.Add(m_Queue[i].name);
            }

            // An automatic run resumes only while automatic generation is still enabled.
            bool resume = m_RunManual || AutomaticGeneration;
            Mod.Log.Info($"{m_RunReason}: suspended outside the main menu, {m_Queue.Count - start} icon(s) left"
                         + (resume ? " for the next visit to the menu" : string.Empty));
            AbortRun();

            if (resume)
            {
                m_ManualRunPending = true;
                m_ResumePending = !m_RunManual;
            }
            else
            {
                m_ForceNames.Clear();
            }
        }

        /// <summary>Stops a run immediately (an unexpected error), keeping every icon already written.</summary>
        private void AbortRun()
        {
            if (m_Queue == null)
            {
                return;
            }

            try
            {
                (m_Current as IDisposable)?.Dispose();
            }
            catch
            {
                // The capture's own cleanup failed; the run caches below are still released.
            }

            m_Current = null;
            EndRun();
            HideProgress();
        }

        private void EndRun()
        {
            m_Queue = null;
            RestoreFramePacing();
            ReleaseRunCaches();
            m_Registry?.Save();
            BuildingIcons.IsGenerating = false;
        }

        /// <summary>This frame's budget: small while a city runs or the user is busy in the menu, large when the menu is idle.</summary>
        private void UpdateFrameBudget()
        {
            if (m_GameMode != GameMode.MainMenu)
            {
                m_FrameBudgetMs = kGameFrameBudgetMs;
                return;
            }

            bool active;

            try
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                active = (mouse != null && (mouse.delta.ReadValue().sqrMagnitude > 0.01f || mouse.scroll.ReadValue().sqrMagnitude > 0.01f
                                            || mouse.leftButton.isPressed || mouse.rightButton.isPressed))
                         || (keyboard != null && keyboard.anyKey.isPressed);
            }
            catch
            {
                active = true;
            }

            float now = UnityEngine.Time.realtimeSinceStartup;

            if (active)
            {
                m_LastInputTime = now;
            }

            m_FrameBudgetMs = now - m_LastInputTime > kIdleSeconds ? kMenuIdleFrameBudgetMs : kMenuFrameBudgetMs;
        }

        /// <summary>
        /// At the main menu, vSync and the frame-rate cap would stretch every frame to a refresh
        /// interval even when the work is done early. Both are lifted for the duration of a run and
        /// restored when it ends; the change is never saved.
        /// </summary>
        private void LiftFramePacing()
        {
            if (m_PacingLifted || !AtMainMenu)
            {
                return;
            }

            try
            {
                m_SavedVSync = QualitySettings.vSyncCount;
                m_SavedTargetFrameRate = Application.targetFrameRate;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                m_PacingLifted = true;
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Frame pacing left unchanged: {ex.Message}");
            }
        }

        private void RestoreFramePacing()
        {
            if (!m_PacingLifted)
            {
                return;
            }

            // Only values still as this mod set them are put back: a setting changed by the player
            // meanwhile is kept, and once a city or the editor is loading the game applies its own
            // frame-rate cap for that mode.
            try
            {
                if (QualitySettings.vSyncCount == 0)
                {
                    QualitySettings.vSyncCount = m_SavedVSync;
                }

                if (AtMainMenu && Application.targetFrameRate == -1)
                {
                    Application.targetFrameRate = m_SavedTargetFrameRate;
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Frame pacing not restored: {ex.Message}");
            }

            m_PacingLifted = false;
        }

        /// <summary>Progress notification in the game's own UI.</summary>
        private void ShowProgress(bool finished = false, int total = 0, double seconds = 0)
        {
            try
            {
                var ui = World.GetOrCreateSystemManaged<NotificationUISystem>();

                if (finished)
                {
                    ui.RemoveNotification(kNotificationId);
                    ui.RemoveNotification(
                        kFinishedNotificationId,
                        6f,
                        LocalizedString.Id(ModLocale.kFinishedTitle),
                        Text(ModLocale.kFinishedText, ("COUNT", total.ToString()), ("SECONDS", ((int)Math.Round(seconds)).ToString())),
                        null,
                        ProgressState.Complete,
                        100,
                        null);
                    return;
                }

                int count = m_Queue?.Count ?? 0;
                int done = Mathf.Clamp(m_QueueIndex - (m_Current != null ? 1 : 0), 0, count);
                ui.AddOrUpdateNotification(
                    kNotificationId,
                    LocalizedString.Id(ModLocale.kProgressTitle),
                    Text(ModLocale.kProgressText, ("DONE", done.ToString()), ("TOTAL", count.ToString())),
                    null,
                    ProgressState.Progressing,
                    count > 0 ? 100 * done / count : 100,
                    null);
            }
            catch (Exception ex)
            {
                if (!m_ProgressUnavailable)
                {
                    m_ProgressUnavailable = true;
                    Mod.Log.Warn($"Progress notification unavailable: {ex.Message}");
                }
            }
        }

        /// <summary>Short notification with the number of icons deleted by "Delete unused icons".</summary>
        private void ShowCleanupResult(int deleted)
        {
            try
            {
                World.GetOrCreateSystemManaged<NotificationUISystem>().RemoveNotification(
                    kCleanupNotificationId,
                    6f,
                    LocalizedString.Id(ModLocale.kCleanupTitle),
                    Text(ModLocale.kCleanupText, ("COUNT", deleted.ToString(CultureInfo.InvariantCulture))),
                    null,
                    ProgressState.Complete,
                    100,
                    null);
            }
            catch
            {
                // The result is in the log as well.
            }
        }

        private void HideProgress()
        {
            try
            {
                World.GetOrCreateSystemManaged<NotificationUISystem>().RemoveNotification(kNotificationId);
            }
            catch
            {
                // No notification to remove.
            }
        }

        private static LocalizedString Text(string id, params (string name, string value)[] args)
        {
            var values = new Dictionary<string, ILocElement>();

            foreach (var (name, value) in args)
            {
                values[name] = LocalizedString.Value(value);
            }

            return new LocalizedString(id, null, values);
        }
    }
}
