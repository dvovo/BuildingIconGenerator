// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Integration
{
    using System;
    using System.Collections.Generic;
    using Game.Prefabs;

    /// <summary>
    /// Shows generated icons in the game's own UI, reversibly.
    ///
    /// The game resolves a prefab's icon at the moment it is displayed (<c>ImageSystem.GetIcon</c>
    /// reads <see cref="UIObject.m_Icon"/>; without a UIObject it falls back to the prefab's
    /// thumbnail). Replacing that value in memory is therefore enough, and every change is recorded
    /// so it can be undone exactly: prefabs that already had a UIObject get their original icon
    /// back, and the UIObject added to prefabs that had none is removed again. No game file is
    /// touched, and nothing is applied in the editor, where prefabs may be saved.
    /// </summary>
    internal sealed class GameIconOverrides
    {
        /// <summary>Prefabs whose existing UIObject icon was replaced, with the original icon.</summary>
        private readonly Dictionary<PrefabBase, string> m_Replaced = new Dictionary<PrefabBase, string>();

        /// <summary>Prefabs that received a UIObject from this class.</summary>
        private readonly HashSet<PrefabBase> m_Added = new HashSet<PrefabBase>();

        /// <summary>Number of prefabs currently showing a generated icon.</summary>
        public int Count => m_Replaced.Count + m_Added.Count;

        /// <summary>Points the prefab's UI icon to <paramref name="uri"/>, remembering how to undo it.</summary>
        public void Apply(PrefabBase prefab, string uri)
        {
            if (prefab == null || string.IsNullOrEmpty(uri))
            {
                return;
            }

            try
            {
                if (prefab.TryGet(out UIObject ui))
                {
                    if (!m_Replaced.ContainsKey(prefab) && !m_Added.Contains(prefab))
                    {
                        m_Replaced[prefab] = ui.m_Icon;
                    }

                    ui.m_Icon = uri;
                    return;
                }

                ui = prefab.AddComponent<UIObject>();
                ui.m_Icon = uri;
                m_Added.Add(prefab);
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Icon of '{prefab.name}' not replaced in the game UI: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Undoes the change made to one prefab, if any.</summary>
        public void Restore(PrefabBase prefab)
        {
            if (prefab == null)
            {
                return;
            }

            try
            {
                if (m_Replaced.TryGetValue(prefab, out string original))
                {
                    m_Replaced.Remove(prefab);

                    if (prefab.TryGet(out UIObject ui))
                    {
                        ui.m_Icon = original;
                    }
                }
                else if (m_Added.Remove(prefab))
                {
                    prefab.Remove<UIObject>();
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Icon of '{prefab.name}' not restored in the game UI: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Undoes every change.</summary>
        public void RestoreAll()
        {
            foreach (var prefab in new List<PrefabBase>(m_Replaced.Keys))
            {
                Restore(prefab);
            }

            foreach (var prefab in new List<PrefabBase>(m_Added))
            {
                Restore(prefab);
            }

            m_Replaced.Clear();
            m_Added.Clear();
        }
    }
}
