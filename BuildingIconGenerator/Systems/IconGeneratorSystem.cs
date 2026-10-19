// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Systems
{
    using System;
    using System.Collections.Generic;
    using BuildingIconGenerator.Api;
    using BuildingIconGenerator.Integration;
    using BuildingIconGenerator.Rendering;
    using BuildingIconGenerator.Settings;
    using BuildingIconGenerator.Storage;
    using Colossal.Core;
    using Colossal.Serialization.Entities;
    using Game;
    using Game.Prefabs;
    using Unity.Entities;

    /// <summary>
    /// Generates the building icons. The system is split by concern into partial files:
    /// <list type="table">
    /// <item><term>IconGeneratorSystem.cs</term><description>lifecycle, scheduling, maintenance and requests;</description></item>
    /// <item><term>.Run.cs</term><description>catalogue, fingerprints, run queue, frame budget and progress;</description></item>
    /// <item><term>.Capture.cs</term><description>building one icon, step by step, across frames;</description></item>
    /// <item><term>.Scene.cs</term><description>what to draw: meshes, sub-objects, lanes, placeholders, colour slots;</description></item>
    /// <item><term>.Materials.cs</term><description>HDRP materials from the game's surfaces, colour bake, alpha handling;</description></item>
    /// <item><term>.Textures.cs</term><description>texture reads: fast VT decode, plain textures, shared textures;</description></item>
    /// <item><term>.Ground.cs</term><description>the lot: ground surfaces and ground decals composed on the GPU;</description></item>
    /// <item><term>.Decals.cs</term><description>decals off the ground, drawn as paint geometry;</description></item>
    /// <item><term>.Meshes.cs</term><description>normal repair, basement clipping, texel density;</description></item>
    /// <item><term>.Caches.cs</term><description>run-wide caches, their budgets and eviction order.</description></item>
    /// </list>
    ///
    /// <para>Nothing runs from the ECS update phase. Work is driven by updaters registered with the
    /// main-thread dispatcher, which tick every frame whatever the world is doing (including at the
    /// main menu): <see cref="Maintain"/> is always registered once the game has loaded, and
    /// <see cref="Tick"/> only while a run is starting or in progress.</para>
    ///
    /// <para>The system only reads game assets. Everything it creates (materials, meshes, textures,
    /// game objects) lives on a private layer, is flagged HideAndDontSave and is destroyed by the
    /// system itself.</para>
    /// </summary>
    public sealed partial class IconGeneratorSystem : GameSystemBase
    {
        /// <summary>Frames to wait after the first load before reading the catalogue: the game keeps initialising for a while.</summary>
        private const int kStartDelayFrames = 300;

        /// <summary>Retry interval while the catalogue is still empty.</summary>
        private const int kCatalogueRetryFrames = 60;

        /// <summary>Stop waiting for a catalogue after roughly ten minutes.</summary>
        private const int kGiveUpFrames = 36000;

        /// <summary>How often the maintenance updater looks at deletions and requests.</summary>
        private const int kMaintenanceIntervalFrames = 120;

        private readonly HashSet<string> m_Heal = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> m_ForceNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly GameIconOverrides m_Overrides = new GameIconOverrides();

        private EntityQuery m_BuildingQuery;
        private PrefabSystem m_PrefabSystem;
        private GameMode m_GameMode;

        private bool m_Maintaining;
        private int m_MaintenanceFrames;
        private bool m_Ticking;

        private bool m_StartupArmed;
        private bool m_StartupPending;
        private int m_StartupFrames;

        private bool m_ManualRunPending;
        private bool m_ManualRegenerateAll;
        private bool m_CleanupPending;
        private bool m_Destroyed;

        /// <summary>The live instance, once the mod has created it.</summary>
        public static IconGeneratorSystem Instance { get; private set; }

        private static bool AutomaticGeneration => Mod.Settings == null || Mod.Settings.GenerateAutomatically;

        private static GameIconScope IconScope => Mod.Settings?.GameIcons ?? GameIconScope.Off;

        /// <summary>
        /// True while the main menu is shown and nothing is loading. Icons are only rendered then:
        /// a run in progress when the player loads a city is suspended and finishes on the next
        /// visit to the menu, so gameplay never shares the GPU or the frame pacing with it.
        /// </summary>
        private static bool AtMainMenu
        {
            get
            {
                var manager = Game.SceneFlow.GameManager.instance;
                return manager != null && manager.gameMode == GameMode.MainMenu && !manager.isGameLoading;
            }
        }

        /// <summary>
        /// Queues a run from the options page: the missing and out-of-date icons, or every icon when
        /// <paramref name="regenerateAll"/> is set. It starts as soon as no other run is in progress.
        /// </summary>
        public void RequestRun(bool regenerateAll)
        {
            m_ManualRunPending = true;
            m_ManualRegenerateAll |= regenerateAll;
        }

        /// <summary>
        /// Queues the removal of the icons of buildings that are not installed (see
        /// <see cref="IconRegistry.Prune"/>). It runs at the main menu, when no run is in progress.
        /// </summary>
        public void RequestCleanup()
        {
            m_CleanupPending = true;
        }

        /// <summary>Applies or removes the generated icons in the game's UI, following the settings and the game mode.</summary>
        public void RefreshGameIcons()
        {
            try
            {
                // Restoring first lets a narrower scope give the other buildings their own icons back.
                m_Overrides.RestoreAll();

                if (IconScope != GameIconScope.Off && m_GameMode != GameMode.Editor)
                {
                    foreach (var prefab in Catalogue())
                    {
                        if (ShowsGeneratedIcon(prefab))
                        {
                            ApplyGameIcon(prefab);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Game icons not refreshed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Puts every icon replaced in the game's UI back to the original.</summary>
        public void RestoreGameIcons()
        {
            m_Overrides.RestoreAll();
        }

        /// <inheritdoc/>
        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;

            // Buildings and building upgrades, without the placeholders that only stand for a
            // random choice among other buildings.
            m_BuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<BuildingData>(), ComponentType.ReadOnly<BuildingExtensionData>() },
                All = new[] { ComponentType.ReadOnly<PrefabData>() },
                None = new[] { ComponentType.ReadOnly<PlaceholderBuildingData>() },
            });

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // The work is driven by Tick and Maintain, not by the update phase.
            Enabled = false;
        }

        /// <inheritdoc/>
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_GameMode = mode;
            BuildingIcons.TryRegisterUriHost();

            if (!m_Maintaining)
            {
                m_Maintaining = true;
                MainThreadDispatcher.RegisterUpdater(Maintain);
            }

            RefreshGameIcons();

            // One automatic pass per session, never in the editor.
            if (!m_StartupArmed && AutomaticGeneration && mode != GameMode.Editor)
            {
                m_StartupArmed = true;
                m_StartupPending = true;
                m_StartupFrames = 0;
                EnsureTicking();
            }
        }

        /// <inheritdoc/>
        protected override void OnUpdate()
        {
            Enabled = false;
        }

        /// <inheritdoc/>
        protected override void OnDestroy()
        {
            // Quitting mid-run: let the icons already queued reach the disk. The updaters see the
            // flag and unregister themselves on their next call.
            m_Destroyed = true;
            CaptureRig.WaitForWrites();
            IconRegistry.Shared?.Watch(false);
            BuildingIcons.IsGenerating = false;
            m_Overrides.RestoreAll();
            m_Baker?.ReleaseAll();
            m_Shaders?.Dispose();
            m_Shaders = null;
            m_Rig?.Dispose();
            m_Rig = null;

            if (Instance == this)
            {
                Instance = null;
            }

            base.OnDestroy();
        }

        private void EnsureTicking()
        {
            if (!m_Ticking)
            {
                m_Ticking = true;
                MainThreadDispatcher.RegisterUpdater(Tick);
            }
        }

        /// <summary>Per-frame updater while a run is starting or in progress. Returning true unregisters it.</summary>
        private bool Tick()
        {
            if (m_Destroyed)
            {
                return true;
            }

            try
            {
                if (m_Queue != null)
                {
                    if (AtMainMenu)
                    {
                        ProcessNext();
                    }
                    else
                    {
                        SuspendRun();
                    }
                }
                else if (m_StartupPending && AtMainMenu)
                {
                    PollStartup();
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Error($"Icon generation stopped: {ex}");
                m_StartupPending = false;

                try
                {
                    AbortRun();
                }
                catch (Exception cleanup)
                {
                    Mod.Log.Error($"Cleanup after the failure also failed: {cleanup}");
                    m_Queue = null;
                    m_Current = null;
                }
            }

            if (m_Queue != null || m_StartupPending)
            {
                return false;
            }

            m_Ticking = false;
            return true;
        }

        /// <summary>Waits for the game to settle and for the catalogue to be loaded, then starts the automatic run.</summary>
        private void PollStartup()
        {
            m_StartupFrames++;

            if (m_StartupFrames < kStartDelayFrames || m_StartupFrames % kCatalogueRetryFrames != 0)
            {
                return;
            }

            var catalogue = Catalogue();

            if (catalogue.Count == 0)
            {
                if (m_StartupFrames >= kGiveUpFrames)
                {
                    Mod.Log.Warn("No building catalogue became available; automatic generation waits for the next load.");
                    m_StartupPending = false;
                    m_StartupArmed = false;
                }

                return;
            }

            // Retried at the next interval while the game's material library is not available.
            m_StartupPending = !StartRun(catalogue, regenerateAll: false, "start-up");
        }

        /// <summary>
        /// Always-on updater: publishes "icon written" events on the main thread, and keeps the icons
        /// folder whole - PNGs deleted while the game runs, icons requested through the API and runs
        /// requested from the options page are handled as soon as no run is in progress.
        /// </summary>
        private bool Maintain()
        {
            if (m_Destroyed)
            {
                return true;
            }

            try
            {
                var registry = IconRegistry.Shared;

                if (registry == null)
                {
                    return false;
                }

                while (registry.TryTakeWritten(out string written))
                {
                    BuildingIcons.RaiseUpdated(written);

                    if (m_GameMode != GameMode.Editor && m_CatalogueByName.TryGetValue(written, out var prefab) && ShowsGeneratedIcon(prefab))
                    {
                        ApplyGameIcon(prefab);
                    }
                }

                if (++m_MaintenanceFrames % kMaintenanceIntervalFrames != 0)
                {
                    return false;
                }

                while (registry.TryTakeVanished(out string vanished))
                {
                    m_Heal.Add(vanished);

                    if (m_CatalogueByName.TryGetValue(vanished, out var prefab))
                    {
                        m_Overrides.Restore(prefab);
                    }
                }

                while (BuildingIcons.TryTakeRequest(out string requested))
                {
                    m_Heal.Add(requested);
                    m_ForceNames.Add(requested);
                }

                // Deletions are only watched for at the main menu, where they are healed (see IconRegistry.Watch).
                registry.Watch(AtMainMenu && AutomaticGeneration);

                if (m_Queue != null || m_StartupPending || m_GameMode == GameMode.Editor || !AtMainMenu)
                {
                    return false;
                }

                if (m_ManualRunPending)
                {
                    bool all = m_ManualRegenerateAll;

                    string reason = all ? "regenerate all" : m_ResumePending ? "resumed" : "generate now";

                    if (StartRun(Catalogue(), all, reason))
                    {
                        m_ManualRunPending = false;
                        m_ManualRegenerateAll = false;
                        m_RunManual = !m_ResumePending;
                        m_ResumePending = false;
                    }
                }
                else if (m_CleanupPending)
                {
                    m_CleanupPending = false;
                    DeleteUnusedIcons(registry);
                }
                else if (m_Heal.Count > 0 && m_GameMode == GameMode.MainMenu && AutomaticGeneration)
                {
                    StartHealRun();
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Icon maintenance failed: {ex.GetType().Name}: {ex.Message}");
                m_Heal.Clear();
            }

            return false;
        }

        /// <summary>Regenerates the icons that vanished from the folder or were requested through the API.</summary>
        private void StartHealRun()
        {
            var registry = IconRegistry.Shared;
            var wanted = new List<PrefabBase>();

            foreach (var prefab in Catalogue())
            {
                // Skipped when already back meanwhile (e.g. regenerated by the start-up run), unless
                // it was requested explicitly.
                if (m_Heal.Contains(prefab.name)
                    && (m_ForceNames.Contains(prefab.name) || registry == null || !registry.TryGet(prefab.name, out _)))
                {
                    wanted.Add(prefab);
                }
            }

            m_Heal.Clear();

            if (wanted.Count == 0)
            {
                m_ForceNames.Clear();
                return;
            }

            if (!StartRun(wanted, regenerateAll: false, "missing or requested icons"))
            {
                // Retried at the next maintenance interval.
                foreach (var prefab in wanted)
                {
                    m_Heal.Add(prefab.name);
                }
            }
        }

        /// <summary>True when the settings show this building's generated icon in the game's UI.</summary>
        private bool ShowsGeneratedIcon(PrefabBase prefab)
        {
            switch (IconScope)
            {
                case GameIconScope.AllBuildings:
                    return true;
                case GameIconScope.ZonedBuildings:
                    return prefab != null && m_SpawnableNames.Contains(prefab.name);
                default:
                    return false;
            }
        }

        /// <summary>Deletes the icons of the buildings that are not in the catalogue, and stray PNGs.</summary>
        private void DeleteUnusedIcons(IconRegistry registry)
        {
            var installed = new HashSet<string>(StringComparer.Ordinal);
            var protectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prefab in Catalogue())
            {
                installed.Add(prefab.name);
                protectedFiles.Add(IconFileName(prefab.name));
            }

            if (installed.Count == 0)
            {
                Mod.Log.Warn("Unused icons not deleted: no building is loaded yet.");
                return;
            }

            int deleted = registry.Prune(installed, protectedFiles);
            Mod.Log.Info($"Unused icons deleted: {deleted} ({registry.Count} icon(s) kept)");
            ShowCleanupResult(deleted);
        }

        private void ApplyGameIcon(PrefabBase prefab)
        {
            if (BuildingIcons.UriAvailable && BuildingIcons.TryGetThumbnailUri(prefab, out string uri))
            {
                m_Overrides.Apply(prefab, uri);
            }
        }
    }
}
