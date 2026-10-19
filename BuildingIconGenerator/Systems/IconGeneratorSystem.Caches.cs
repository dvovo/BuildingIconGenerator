// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using BuildingIconGenerator.Rendering;
    using BuildingIconGenerator.Scheduling;
    using BuildingIconGenerator.Storage;
    using Colossal.IO.AssetDatabase;
    using Game.Prefabs;
    using UnityEngine;

    /// <content>
    /// Run-wide caches: materials (with their baked colours), decoded textures and loaded geometry
    /// are kept across buildings, because trees, benches, lamps and air conditioners repeat from one
    /// building to the next. Each cache is bounded and ordered by popularity
    /// (<see cref="UsageIndex"/>) and then by last use (<see cref="UsageHeap{TKey}"/>), so over
    /// budget the least used entry goes first without scanning; the entries of the last
    /// <see cref="kProtectBuildings"/> buildings wait their turn. Everything is released when the
    /// run ends.
    /// </content>
    public sealed partial class IconGeneratorSystem
    {
        private const int kMaxCachedMaterials = 600;
        private const long kMaxBakedBytes = 640L * 1024 * 1024;
        private const long kMaxFastBytes = 1280L * 1024 * 1024;
        private const int kMaxVtLoaded = 16;
        private const int kMaxHeldGeometry = 800;
        private const long kMaxHeldGeometryBytes = 384L * 1024 * 1024;

        /// <summary>Buildings whose cache entries are protected from eviction (the current one and the previous).</summary>
        private const int kProtectBuildings = 2;

        private readonly Dictionary<string, Material[]> m_MaterialCache = new Dictionary<string, Material[]>();
        private readonly Dictionary<string, CacheMeta> m_CacheMeta = new Dictionary<string, CacheMeta>();
        private readonly Dictionary<string, HashSet<string>> m_MaterialsBySurface = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly List<Material> m_RunMaterials = new List<Material>();
        private readonly List<Texture2D> m_Baked = new List<Texture2D>();
        private readonly HashSet<string> m_BuildingVt = new HashSet<string>();
        private readonly Dictionary<string, (VTSurfaceAsset asset, long stamp)> m_VtLoaded = new Dictionary<string, (VTSurfaceAsset, long)>();

        // Texture assets loaded during the run, released when it ends. A TextureAsset counts its
        // loads (one Unload per Load, so a texture the game also uses stays loaded); a
        // VTTextureAsset keeps a single full-resolution instance (one Unload).
        private readonly List<TextureAsset> m_LoadedTextureAssets = new List<TextureAsset>();
        private readonly HashSet<VTTextureAsset> m_LoadedVtTextures = new HashSet<VTTextureAsset>();

        /// <summary>Content stamps of the surfaces planned during the run (plan index keys).</summary>
        private readonly Dictionary<SurfaceAsset, string> m_SurfaceStamps = new Dictionary<SurfaceAsset, string>();

        /// <summary>Content stamps of the models measured during the run (facts index keys).</summary>
        private readonly Dictionary<RenderPrefab, string> m_RenderStamps = new Dictionary<RenderPrefab, string>();
        private readonly Dictionary<RenderPrefab, HeldGeometry> m_HeldGeometry = new Dictionary<RenderPrefab, HeldGeometry>();
        private readonly UsageHeap<string> m_MaterialOrder = new UsageHeap<string>(StringComparer.Ordinal);
        private readonly UsageHeap<string> m_FastOrder = new UsageHeap<string>(StringComparer.Ordinal);
        private readonly UsageHeap<RenderPrefab> m_GeometryOrder = new UsageHeap<RenderPrefab>();
        private long m_HeldGeometryBytes;
        private long m_Tick;

        /// <summary>Bookkeeping of one cached material set.</summary>
        private sealed class CacheMeta
        {
            public long Stamp;

            /// <summary>Surfaces whose decoded textures the materials bind directly.</summary>
            public List<string> VtSurfaces;

            /// <summary>Serial of the last building that used it.</summary>
            public int Building;

            /// <summary>Usage-index key of its geometry.</summary>
            public string HeatKey;
        }

        /// <summary>A render prefab whose meshes are kept loaded across buildings (one extra obtain held).</summary>
        private sealed class HeldGeometry
        {
            public int Building;
            public long Bytes;
        }

        private void IndexMaterial(string key, CacheMeta meta)
        {
            foreach (var name in meta.VtSurfaces)
            {
                if (!m_MaterialsBySurface.TryGetValue(name, out var keys))
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    m_MaterialsBySurface[name] = keys;
                }

                keys.Add(key);
            }
        }

        /// <summary>Cached material sets binding textures of <paramref name="surface"/> (reverse index).</summary>
        private List<string> MaterialsUsing(string surface)
        {
            return m_MaterialsBySurface.TryGetValue(surface, out var keys) ? keys.ToList() : new List<string>();
        }

        private void OrderFast(string name)
        {
            UsageIndex.Note("s:" + name);
            m_FastOrder.Set(name, UsageIndex.Heat("s:" + name), m_BuildingSerial);
        }

        private static void NoteSurfaces(CacheMeta meta)
        {
            foreach (var name in meta.VtSurfaces)
            {
                UsageIndex.Note("s:" + name);
            }
        }

        /// <summary>Marks a cached material set and the surfaces it binds as used by the current building.</summary>
        private void Touch(string key)
        {
            if (!m_CacheMeta.TryGetValue(key, out var meta))
            {
                return;
            }

            meta.Stamp = ++m_Tick;
            meta.Building = m_BuildingSerial;
            NoteSurfaces(meta);
            m_MaterialOrder.Set(key, UsageIndex.Heat(meta.HeatKey), m_BuildingSerial);

            foreach (var name in meta.VtSurfaces)
            {
                if (m_VtLoaded.TryGetValue(name, out var vt))
                {
                    m_VtLoaded[name] = (vt.asset, m_Tick);
                }

                if (m_Fast.TryGetValue(name, out var fast))
                {
                    fast.Stamp = m_Tick;
                    fast.Building = m_BuildingSerial;
                    OrderFast(name);
                }
            }
        }

        /// <summary>
        /// End of a capture: releases each obtain of the building's meshes (ObtainMeshes counts
        /// references), except popular geometry - used by two or more buildings - whose first obtain
        /// is kept as a hold, so the next building finds its meshes loaded instead of read again.
        /// </summary>
        private void ReleaseObtainedMeshes(List<RenderPrefab> obtained, Dictionary<RenderPrefab, Mesh[]> obtainedMeshes)
        {
            foreach (var render in obtained)
            {
                try
                {
                    if (render != null && !m_HeldGeometry.ContainsKey(render)
                        && obtainedMeshes.TryGetValue(render, out var meshes)
                        && UsageIndex.Heat(GeometryKey(render)) >= 2)
                    {
                        long bytes = 0;

                        foreach (var mesh in meshes)
                        {
                            if (mesh != null)
                            {
                                bytes += (mesh.vertexCount * 48L) + (mesh.subMeshCount > 0 ? (long)mesh.GetIndexCount(0) * 4L : 0L);
                            }
                        }

                        m_HeldGeometry[render] = new HeldGeometry { Building = m_BuildingSerial, Bytes = bytes };
                        m_GeometryOrder.Set(render, UsageIndex.Heat(GeometryKey(render)), m_BuildingSerial);
                        m_HeldGeometryBytes += bytes;
                        continue;
                    }

                    render.ReleaseMeshes();
                }
                catch
                {
                    // Already released by the game.
                }
            }
        }

        /// <summary>Between buildings: evicts the least used materials, textures, VT surfaces and geometry over budget.</summary>
        private void EnforceBudgets()
        {
            int protectFrom = m_BuildingSerial - kProtectBuildings + 1;

            try
            {
                while (m_MaterialCache.Count > kMaxCachedMaterials || (m_Baker.BakedBytes > kMaxBakedBytes && m_MaterialCache.Count > 0))
                {
                    if (!m_MaterialOrder.TryLeastUsed(protectFrom, out var victim, out _)
                        && !m_MaterialOrder.TryLeastUsed(int.MaxValue, out victim, out _))
                    {
                        break;
                    }

                    Evict(victim);
                }

                while (m_FastBytes + m_SharedBytes > kMaxFastBytes && m_Fast.Count > 0)
                {
                    if (!m_FastOrder.TryLeastUsed(protectFrom, out var victim, out _)
                        && !m_FastOrder.TryLeastUsed(int.MaxValue, out victim, out _))
                    {
                        break;
                    }

                    if (!m_Fast.ContainsKey(victim))
                    {
                        m_FastOrder.Remove(victim);
                        continue;
                    }

                    DropFast(victim);
                }

                while (m_VtLoaded.Count > kMaxVtLoaded)
                {
                    string oldest = null;
                    long stamp = long.MaxValue;

                    foreach (var pair in m_VtLoaded)
                    {
                        if (pair.Value.stamp < stamp)
                        {
                            stamp = pair.Value.stamp;
                            oldest = pair.Key;
                        }
                    }

                    if (oldest == null)
                    {
                        break;
                    }

                    // Materials may bind this surface's decoded textures directly: evicted first.
                    foreach (var key in MaterialsUsing(oldest))
                    {
                        Evict(key);
                    }

                    try
                    {
                        m_VtLoaded[oldest].asset.Unload(false);
                    }
                    catch (Exception ex)
                    {
                        Mod.Log.Warn($"Unloading '{oldest}' failed: {ex.Message}");
                    }

                    m_VtLoaded.Remove(oldest);

                    // Its decoded textures are gone: the cached lookup must not hand them out again.
                    m_SurfaceTextures.Remove(oldest);
                }

                while (m_HeldGeometry.Count > kMaxHeldGeometry || (m_HeldGeometryBytes > kMaxHeldGeometryBytes && m_HeldGeometry.Count > 0))
                {
                    if (!m_GeometryOrder.TryLeastUsed(protectFrom, out var victim, out _)
                        && !m_GeometryOrder.TryLeastUsed(int.MaxValue, out victim, out _))
                    {
                        break;
                    }

                    if (victim is null)
                    {
                        break;
                    }

                    if (!m_HeldGeometry.ContainsKey(victim))
                    {
                        m_GeometryOrder.Remove(victim);
                        continue;
                    }

                    ReleaseHeld(victim);
                }

                if (m_RunFixed.Count > kMaxRepairedMeshes)
                {
                    DestroyRepairedMeshes();
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Cache eviction failed: {ex.Message}");
            }
        }

        private void ReleaseHeld(RenderPrefab render)
        {
            if (!m_HeldGeometry.TryGetValue(render, out var held))
            {
                return;
            }

            try
            {
                // A prefab destroyed meanwhile (Unity null) has nothing left to release.
                if (render != null)
                {
                    render.ReleaseMeshes();
                }
            }
            catch
            {
                // Already released.
            }

            m_HeldGeometryBytes -= held.Bytes;
            m_HeldGeometry.Remove(render);
            m_GeometryOrder.Remove(render);
        }

        private void ReleaseAllHeld()
        {
            foreach (var render in m_HeldGeometry.Keys.ToList())
            {
                try
                {
                    if (render != null)
                    {
                        render.ReleaseMeshes();
                    }
                }
                catch
                {
                    // Already released.
                }
            }

            m_HeldGeometry.Clear();
            m_GeometryOrder.Clear();
            m_HeldGeometryBytes = 0;
        }

        /// <summary>Destroys a cached material set and its baked textures, and removes it from every index.</summary>
        private void Evict(string key)
        {
            if (m_MaterialCache.TryGetValue(key, out var materials) && materials != null)
            {
                foreach (var material in materials.Distinct())
                {
                    if (material == null)
                    {
                        continue;
                    }

                    var texture = material.HasProperty("_BaseColorMap") ? material.GetTexture("_BaseColorMap") : null;

                    if (texture != null && !m_Baker.Release(texture) && texture is Texture2D baked && m_Baked.Remove(baked))
                    {
                        UnityEngine.Object.DestroyImmediate(baked);
                    }

                    m_RunMaterials.Remove(material);
                    UnityEngine.Object.DestroyImmediate(material);
                }
            }

            if (m_CacheMeta.TryGetValue(key, out var meta))
            {
                foreach (var name in meta.VtSurfaces)
                {
                    if (m_MaterialsBySurface.TryGetValue(name, out var keys) && keys.Remove(key) && keys.Count == 0)
                    {
                        m_MaterialsBySurface.Remove(name);
                    }
                }
            }

            m_MaterialOrder.Remove(key);
            m_MaterialCache.Remove(key);
            m_CacheMeta.Remove(key);
        }

        private void DestroyRepairedMeshes()
        {
            foreach (var mesh in m_RunMeshes)
            {
                if (mesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }

            m_RunMeshes.Clear();
            m_RunFixed.Clear();
        }

        /// <summary>Loads a plain texture asset and records the load, so the run releases it at the end.</summary>
        private Texture LoadTracked(TextureAsset asset)
        {
            if (asset == null)
            {
                return null;
            }

            var texture = asset.Load(0);
            m_LoadedTextureAssets.Add(asset);
            return texture;
        }

        /// <summary>Loads a full-resolution VT texture and records it, so the run releases it at the end.</summary>
        private Texture LoadTracked(VTTextureAsset asset)
        {
            if (asset == null)
            {
                return null;
            }

            var texture = asset.Load();
            m_LoadedVtTextures.Add(asset);
            return texture;
        }

        private void ReleaseLoadedTextures()
        {
            foreach (var asset in m_LoadedTextureAssets)
            {
                try
                {
                    asset.Unload(false);
                }
                catch
                {
                    // Already released by its owner.
                }
            }

            foreach (var asset in m_LoadedVtTextures)
            {
                try
                {
                    asset.Unload(false);
                }
                catch
                {
                    // Already released by its owner.
                }
            }

            m_LoadedTextureAssets.Clear();
            m_LoadedVtTextures.Clear();
        }

        /// <summary>
        /// End of a run: frees everything kept across buildings, saves the persistent indexes and
        /// waits for the last icons to reach the disk.
        /// </summary>
        private void ReleaseRunCaches()
        {
            foreach (var material in m_RunMaterials)
            {
                if (material != null)
                {
                    UnityEngine.Object.DestroyImmediate(material);
                }
            }

            foreach (var baked in m_Baked)
            {
                if (baked != null)
                {
                    UnityEngine.Object.DestroyImmediate(baked);
                }
            }

            m_Baker?.ReleaseAll();
            m_RunMaterials.Clear();
            m_Baked.Clear();
            m_MaterialCache.Clear();
            m_CacheMeta.Clear();
            m_MaterialOrder.Clear();
            m_MaterialsBySurface.Clear();

            foreach (var vt in m_VtLoaded.Values)
            {
                try
                {
                    vt.asset.Unload(false);
                }
                catch
                {
                    // Already unloaded.
                }
            }

            m_VtLoaded.Clear();
            m_SurfaceStamps.Clear();
            m_RenderStamps.Clear();
            ReleaseLoadedTextures();
            ReleaseFast();
            ReleaseAllHeld();
            PlanIndex.Save();
            UsageIndex.Save();
            FactsIndex.Save();
            m_AtlasPixels.Clear();
            m_SurfaceTextures.Clear();
            m_DecalPaint.Clear();
            m_AlphaCut.Clear();
            DestroyRepairedMeshes();
            CaptureRig.WaitForWrites();
            m_NonVt.Clear();
            m_NonVtProps.Clear();
        }
    }
}
