// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Scheduling
{
    using System.Collections.Generic;

    /// <summary>
    /// Eviction order of a cache: entries sorted by how many buildings used them (fewest first),
    /// then by the last building that used them (oldest first), then by insertion. The next victim
    /// is the first entry, found in O(log n) instead of scanning the cache.
    ///
    /// <see cref="TryLeastUsed"/> skips entries used by recent buildings, so the assets of one
    /// building get a chance to be reused by the next (consecutive levels of a building share most
    /// of their textures) before they count as used once.
    /// </summary>
    internal sealed class UsageHeap<TKey>
    {
        private struct Node
        {
            public int Uses;
            public int Building;
            public long Sequence;
            public TKey Key;
        }

        private sealed class Order : IComparer<Node>
        {
            public int Compare(Node a, Node b)
            {
                int c = a.Uses.CompareTo(b.Uses);

                if (c != 0)
                {
                    return c;
                }

                c = a.Building.CompareTo(b.Building);
                return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
            }
        }

        private readonly SortedSet<Node> m_Order = new SortedSet<Node>(new Order());
        private readonly Dictionary<TKey, Node> m_Nodes;
        private long m_Sequence;

        public UsageHeap(IEqualityComparer<TKey> comparer = null)
        {
            m_Nodes = new Dictionary<TKey, Node>(comparer ?? EqualityComparer<TKey>.Default);
        }

        public int Count => m_Nodes.Count;

        /// <summary>Inserts <paramref name="key"/> or moves it to its place for (uses, building).</summary>
        public void Set(TKey key, int uses, int building)
        {
            if (m_Nodes.TryGetValue(key, out var old))
            {
                if (old.Uses == uses && old.Building == building)
                {
                    return;
                }

                m_Order.Remove(old);
            }

            var node = new Node { Uses = uses, Building = building, Sequence = ++m_Sequence, Key = key };
            m_Nodes[key] = node;
            m_Order.Add(node);
        }

        public void Remove(TKey key)
        {
            if (m_Nodes.TryGetValue(key, out var node))
            {
                m_Order.Remove(node);
                m_Nodes.Remove(key);
            }
        }

        public void Clear()
        {
            m_Order.Clear();
            m_Nodes.Clear();
        }

        /// <summary>
        /// The least used entry whose last building is before <paramref name="protectFrom"/>.
        /// False when every entry is protected or the heap is empty.
        /// </summary>
        public bool TryLeastUsed(int protectFrom, out TKey key, out int uses)
        {
            foreach (var node in m_Order)
            {
                if (node.Building < protectFrom)
                {
                    key = node.Key;
                    uses = node.Uses;
                    return true;
                }
            }

            key = default;
            uses = 0;
            return false;
        }
    }
}
