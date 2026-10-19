// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Settings
{
    using System;
    using System.IO;
    using BuildingIconGenerator.Systems;
    using Colossal.IO.AssetDatabase;
    using Game.Modding;
    using Game.Settings;
    using UnityEngine;

    /// <summary>Which buildings show their generated icon in the game's UI.</summary>
    public enum GameIconScope
    {
        /// <summary>The game's own icons everywhere.</summary>
        Off,

        /// <summary>Zoned and signature buildings (the buildings that spawn in zones).</summary>
        ZonedBuildings,

        /// <summary>Every building: also services, transport, parks, unique buildings and upgrades.</summary>
        AllBuildings,
    }

    /// <summary>
    /// Options page of the mod.
    ///
    /// Defaults are assigned in the constructor as well as in <see cref="SetDefaults"/>: the settings
    /// loader only overwrites the values present in the settings file, so on a first run the
    /// constructor values are the ones in effect.
    /// </summary>
    [FileLocation("ModsSettings/" + Mod.Id + "/" + Mod.Id)]
    [SettingsUIGroupOrder(kGenerationGroup, kIntegrationGroup, kMaintenanceGroup)]
    [SettingsUIShowGroupName(kGenerationGroup, kIntegrationGroup, kMaintenanceGroup)]
    public sealed class ModSettings : ModSetting
    {
        public const string kSection = "Main";
        public const string kGenerationGroup = "Generation";
        public const string kIntegrationGroup = "Integration";
        public const string kMaintenanceGroup = "Maintenance";

        private GameIconScope m_GameIcons;

        public ModSettings(IMod mod)
            : base(mod)
        {
            SetDefaults();
        }

        /// <summary>
        /// Generate missing and out-of-date icons by themselves: once per session after the game
        /// has loaded, and again whenever icons disappear from the folder or another mod asks for one.
        /// </summary>
        [SettingsUISection(kSection, kGenerationGroup)]
        public bool GenerateAutomatically { get; set; }

        /// <summary>
        /// Shows the generated icons in the game's own UI, in place of the building thumbnails:
        /// for zoned and signature buildings only, or for every building. Off restores the
        /// original icons immediately.
        /// </summary>
        [SettingsUISection(kSection, kIntegrationGroup)]
        public GameIconScope GameIcons
        {
            get => m_GameIcons;
            set
            {
                if (m_GameIcons == value)
                {
                    return;
                }

                m_GameIcons = value;
                IconGeneratorSystem.Instance?.RefreshGameIcons();
            }
        }

        /// <summary>
        /// Setting of earlier versions (on/off), kept so a saved "on" still means "zoned buildings".
        /// Never shown and always saved as off.
        /// </summary>
        [SettingsUIHidden]
        public bool ReplaceGameIcons
        {
            get => false;
            set
            {
                if (value && m_GameIcons == GameIconScope.Off)
                {
                    GameIcons = GameIconScope.ZonedBuildings;
                }
            }
        }

        /// <summary>Generates the missing and out-of-date icons now.</summary>
        [SettingsUIButton]
        [SettingsUISection(kSection, kMaintenanceGroup)]
        public bool GenerateNow
        {
            set => IconGeneratorSystem.Instance?.RequestRun(regenerateAll: false);
        }

        /// <summary>Renders every icon again, current or not.</summary>
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kMaintenanceGroup)]
        public bool RegenerateAll
        {
            set => IconGeneratorSystem.Instance?.RequestRun(regenerateAll: true);
        }

        /// <summary>
        /// Deletes the icons of buildings that are not installed (not loaded in this session), and
        /// any PNG in the icons folder that belongs to no building.
        /// </summary>
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kMaintenanceGroup)]
        public bool DeleteUnusedIcons
        {
            set => IconGeneratorSystem.Instance?.RequestCleanup();
        }

        /// <summary>Opens the icons folder in the system's file browser.</summary>
        [SettingsUIButton]
        [SettingsUISection(kSection, kMaintenanceGroup)]
        public bool OpenIconsFolder
        {
            set
            {
                try
                {
                    Directory.CreateDirectory(ModPaths.IconsFolder);
                    Application.OpenURL(new Uri(ModPaths.IconsFolder).AbsoluteUri);
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn($"Could not open the icons folder: {ex.Message}");
                }
            }
        }

        /// <inheritdoc/>
        public override void SetDefaults()
        {
            GenerateAutomatically = true;
            m_GameIcons = GameIconScope.Off;
        }
    }
}
