// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator
{
    using System;
    using System.IO;
    using BuildingIconGenerator.Api;
    using BuildingIconGenerator.Localization;
    using BuildingIconGenerator.Settings;
    using BuildingIconGenerator.Systems;
    using Colossal.IO.AssetDatabase;
    using Colossal.Logging;
    using Game;
    using Game.Modding;
    using Game.SceneFlow;

    /// <summary>
    /// Entry point of Building Icon Generator.
    ///
    /// The mod renders a 3D icon of every spawnable building off-screen, straight from the game's
    /// packaged assets (meshes, virtual-texturing data, colour variations), without placing anything
    /// in a city. Icons are generated incrementally at the main menu, recorded in a registry and
    /// exposed to other mods (and optionally to the game's own UI) through <see cref="BuildingIcons"/>.
    /// </summary>
    public sealed class Mod : IMod
    {
        /// <summary>Identifier used for the log, the settings file and the data folder.</summary>
        public const string Id = "BuildingIconGenerator";

        /// <summary>Shared logger. Errors are not surfaced as in-game popups.</summary>
        public static ILog Log { get; } = LogManager.GetLogger(Id).SetShowsErrorsInUI(false);

        /// <summary>The mod's options, or null before <see cref="OnLoad"/> / after <see cref="OnDispose"/>.</summary>
        public static ModSettings Settings { get; private set; }

        /// <summary>
        /// Folder the mod was loaded from (where <c>Bundles/</c> ships). Resolved through the mod
        /// manager first, because <c>Assembly.Location</c> is empty for assemblies loaded from memory.
        /// </summary>
        public static string Folder { get; private set; }

        /// <inheritdoc/>
        public void OnLoad(UpdateSystem updateSystem)
        {
            Folder = ResolveModFolder();
            Log.Info($"Loading from {Folder ?? "(unknown folder)"}");

            Settings = new ModSettings(this);
            Settings.RegisterInOptionsUI();
            AssetDatabase.global.LoadSettings(Id, Settings, new ModSettings(this));
            ModLocale.Register(Settings);

            // The registry is opened now (one folder listing) so the public API answers from the
            // start, before any generation run.
            BuildingIcons.Initialize(ModPaths.IconsFolder);
            BuildingIcons.TryRegisterUriHost();

            // UpdateAt creates the system immediately. Its update phase is not used for the work
            // itself: generation is driven by GameManager's updater (see IconGeneratorSystem), which
            // runs at the main menu as well, where MainLoop is not guaranteed to tick.
            updateSystem.UpdateAt<IconGeneratorSystem>(SystemUpdatePhase.MainLoop);
        }

        /// <inheritdoc/>
        public void OnDispose()
        {
            IconGeneratorSystem.Instance?.RestoreGameIcons();

            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }

        private string ResolveModFolder()
        {
            try
            {
                if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                {
                    return Path.GetDirectoryName(asset.path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Mod folder lookup through the mod manager failed: {ex.Message}");
            }

            try
            {
                string location = typeof(Mod).Assembly.Location;
                return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
            }
            catch
            {
                return null;
            }
        }
    }
}
