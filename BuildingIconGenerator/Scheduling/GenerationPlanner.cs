// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Scheduling
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Orders the buildings of a generation run so that consecutive buildings share as many assets
    /// as possible.
    ///
    /// Moving from one building to the next costs little for the camera (re-framing is a transform
    /// update) but a lot for whatever has to be read, decoded, baked and uploaded again: atlases,
    /// prop textures, meshes. The run caches hold roughly the assets of the last couple of dozen
    /// buildings, so the order decides how much is found already resident.
    ///
    /// <para>Algorithm (greedy over an inverted index, O(families² + postings)):</para>
    /// <list type="number">
    /// <item>Buildings are grouped into families: the name without level and lot size
    /// (<c>EU_ResidentialLow01</c> for <c>EU_ResidentialLow01_L1_3x6</c>, <c>_L4_2x2</c>...). A family
    /// shares its atlases.</item>
    /// <item>Each family's assets are weighted: its own meshes and surfaces 8 (large atlases), the
    /// props, trees and lanes it places 1.</item>
    /// <item>The next family emitted is the one whose assets are most resident, i.e. used within the
    /// last <c>window</c> buildings. Scores are kept current through an inverted index
    /// asset -> families: an asset becoming resident adds its weight to every family using it, an
    /// asset expiring subtracts it. Ties go to the heavier family, then to the name (deterministic).</item>
    /// <item>Within a family: smallest lot first (framed closest, it needs the largest textures, which
    /// the following levels then reuse), then level, then name.</item>
    /// </list>
    /// </summary>
    internal static class GenerationPlanner
    {
        /// <summary>What a building loads, as seen by the planner.</summary>
        public sealed class Item
        {
            public string Name;
            public string Family;
            public int LotArea;
            public int Level;

            /// <summary>Asset key -> weight.</summary>
            public readonly Dictionary<string, float> Assets = new Dictionary<string, float>(StringComparer.Ordinal);
        }

        private sealed class FamilyNode
        {
            public string Name;
            public readonly List<int> Items = new List<int>();
            public readonly Dictionary<string, float> Assets = new Dictionary<string, float>(StringComparer.Ordinal);
            public float TotalWeight;
            public float Score;
            public bool Done;
        }

        private static readonly Regex s_Suffix = new Regex(@"(_L\d+)?(_\d+x\d+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex s_Level = new Regex(@"_L(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Family of a building name: the name without its <c>_L#</c> level and <c>_WxD</c> lot suffixes.</summary>
        public static string FamilyOf(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            string family = s_Suffix.Replace(name, string.Empty);
            return family.Length > 0 ? family : name;
        }

        /// <summary>Building level parsed from the <c>_L#</c> suffix; 0 when absent.</summary>
        public static int LevelOf(string name)
        {
            var match = name != null ? s_Level.Match(name) : null;
            return match != null && match.Success && int.TryParse(match.Groups[1].Value, out int level) ? level : 0;
        }

        /// <summary>Indices of <paramref name="items"/> in generation order.</summary>
        public static List<int> Order(List<Item> items, int window)
        {
            var families = new List<FamilyNode>();
            var byName = new Dictionary<string, FamilyNode>(StringComparer.Ordinal);

            for (int i = 0; i < items.Count; i++)
            {
                string key = items[i].Family ?? items[i].Name ?? string.Empty;

                if (!byName.TryGetValue(key, out var node))
                {
                    node = new FamilyNode { Name = key };
                    byName[key] = node;
                    families.Add(node);
                }

                node.Items.Add(i);

                foreach (var asset in items[i].Assets)
                {
                    if (!node.Assets.TryGetValue(asset.Key, out float w) || w < asset.Value)
                    {
                        node.Assets[asset.Key] = asset.Value;
                    }
                }
            }

            // Inverted index: asset -> families using it.
            var postings = new Dictionary<string, List<FamilyNode>>(StringComparer.Ordinal);

            foreach (var node in families)
            {
                foreach (var asset in node.Assets)
                {
                    node.TotalWeight += asset.Value;

                    if (!postings.TryGetValue(asset.Key, out var list))
                    {
                        list = new List<FamilyNode>();
                        postings[asset.Key] = list;
                    }

                    list.Add(node);
                }

                node.Items.Sort((a, b) =>
                {
                    int c = items[a].LotArea.CompareTo(items[b].LotArea);

                    if (c == 0)
                    {
                        c = items[a].Level.CompareTo(items[b].Level);
                    }

                    return c != 0 ? c : string.CompareOrdinal(items[a].Name, items[b].Name);
                });
            }

            var order = new List<int>(items.Count);
            var lastUse = new Dictionary<string, int>(StringComparer.Ordinal);

            // FIFO of (step, asset) uses as two lists and a head index. Queue<T> is avoided on
            // purpose: the game ships its own System.dll that also defines it, which makes the type
            // ambiguous when the mod is built against the game's assemblies.
            var expiringSteps = new List<int>();
            var expiringAssets = new List<string>();
            int head = 0;
            int step = 0;

            for (int emitted = 0; emitted < families.Count; emitted++)
            {
                // Expire the assets not used within the window.
                while (head < expiringSteps.Count && expiringSteps[head] <= step - window)
                {
                    int at = expiringSteps[head];
                    string asset = expiringAssets[head];
                    head++;

                    if (lastUse.TryGetValue(asset, out int last) && last == at)
                    {
                        lastUse.Remove(asset);

                        foreach (var node in postings[asset])
                        {
                            if (!node.Done)
                            {
                                node.Score -= node.Assets[asset];
                            }
                        }
                    }
                }

                FamilyNode best = null;

                foreach (var node in families)
                {
                    if (node.Done)
                    {
                        continue;
                    }

                    if (best == null || node.Score > best.Score + 1e-4f
                        || (Math.Abs(node.Score - best.Score) <= 1e-4f
                            && (node.TotalWeight > best.TotalWeight + 1e-4f
                                || (Math.Abs(node.TotalWeight - best.TotalWeight) <= 1e-4f && string.CompareOrdinal(node.Name, best.Name) < 0))))
                    {
                        best = node;
                    }
                }

                best.Done = true;

                foreach (int index in best.Items)
                {
                    order.Add(index);
                    step++;

                    foreach (var asset in items[index].Assets.Keys)
                    {
                        bool wasResident = lastUse.ContainsKey(asset);
                        lastUse[asset] = step;
                        expiringSteps.Add(step);
                        expiringAssets.Add(asset);

                        if (!wasResident)
                        {
                            foreach (var node in postings[asset])
                            {
                                if (!node.Done)
                                {
                                    node.Score += node.Assets[asset];
                                }
                            }
                        }
                    }
                }
            }

            return order;
        }
    }
}
