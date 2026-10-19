// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using BuildingIconGenerator.Rendering;
    using BuildingIconGenerator.Storage;
    using BuildingIconGenerator.Textures;
    using Colossal.IO.AssetDatabase;
    using Game.Prefabs;
    using UnityEngine;
    using Hash128 = Colossal.Hash128;

    /// <content>
    /// Texture reads. Three routes, fastest first:
    /// <list type="number">
    /// <item>Fast VT read (<see cref="FastVtLoader"/>): one mip level of a virtual-texturing
    /// surface, decoded on worker threads from a plan (cached in <see cref="PlanIndex"/>). Used for
    /// building atlases and VT props, prefetched ahead of use.</item>
    /// <item>Plain textures of non-VT surfaces (most props and vegetation), read from the surface's
    /// own properties.</item>
    /// <item>Fallbacks: the full-resolution VT surface material, or the per-texture assets.</item>
    /// </list>
    /// Per-texture reads that several surfaces share are decoded once and reference-counted.
    /// </content>
    public sealed partial class IconGeneratorSystem
    {
        /// <summary>Most cached slow-route results kept at once (they hold full-resolution textures).</summary>
        private const int kMaxSlowRouteEntries = 512;

        /// <summary>Surfaces of the run that referenced a texture the asset database does not know (content not loaded).</summary>
        private readonly HashSet<string> m_SurfacesMissingTextures = new HashSet<string>(StringComparer.Ordinal);

        private readonly Dictionary<string, Dictionary<string, Texture>> m_NonVt = new Dictionary<string, Dictionary<string, Texture>>();
        private readonly Dictionary<string, SurfaceProps> m_NonVtProps = new Dictionary<string, SurfaceProps>();
        private readonly Dictionary<string, Dictionary<string, Texture>> m_SurfaceTextures = new Dictionary<string, Dictionary<string, Texture>>();
        private readonly Dictionary<string, FastEntry> m_Fast = new Dictionary<string, FastEntry>();
        private readonly Dictionary<string, FastVtLoader.Job> m_FastPending = new Dictionary<string, FastVtLoader.Job>();
        private readonly HashSet<string> m_FastFailed = new HashSet<string>();
        private readonly Dictionary<string, SharedTexture> m_Shared = new Dictionary<string, SharedTexture>(StringComparer.Ordinal);
        private readonly Dictionary<int, bool> m_DecalTemplate = new Dictionary<int, bool>();
        private readonly Dictionary<int, Dictionary<int, string>> m_TemplateNames = new Dictionary<int, Dictionary<int, string>>();
        private long m_FastBytes;
        private long m_SharedBytes;

        /// <summary>The non-texture values of a non-VT surface, copied while its properties are loaded.</summary>
        private sealed class SurfaceProps
        {
            public readonly Dictionary<string, float> Floats = new Dictionary<string, float>();
            public readonly List<string> Keywords = new List<string>();
        }

        /// <summary>A surface read through the fast route, kept across buildings.</summary>
        private sealed class FastEntry
        {
            public Dictionary<string, Texture> Textures;
            public List<Texture2D> Owned;
            public long Bytes;
            public int Target;
            public long Stamp;
            public int Building;

            /// <summary>Content keys of the shared textures this surface holds a reference on.</summary>
            public List<string> SharedKeys;
        }

        /// <summary>One decoded per-texture read (texture GUID + level), shared by every surface using it.</summary>
        private sealed class SharedTexture
        {
            public Texture2D Texture;
            public int Refs;
            public long Bytes;
        }

        /// <summary>
        /// The textures of a surface, by shader property. Fast-read entries are reused even at a lower
        /// level than asked for: materials of the building being built may already use them (upgrades
        /// happen in <see cref="Prefetch"/>, before the building's own materials exist).
        /// </summary>
        private Dictionary<string, Texture> LoadSurfaceTextures(SurfaceAsset surface, MaterialLibrary library)
        {
            string name = surface.name;

            if (m_Fast.TryGetValue(name, out var entry))
            {
                entry.Stamp = ++m_Tick;
                entry.Building = m_BuildingSerial;
                OrderFast(name);
                m_BuildingVt.Add(name);
                return entry.Textures;
            }

            if (m_SurfaceTextures.TryGetValue(name, out var known))
            {
                return known;
            }

            var fast = LoadFast(surface, library, TextureTarget(surface, library, m_CaptureSize));

            if (fast != null)
            {
                return fast;
            }

            var result = LoadSurfaceTexturesSlow(surface, library);

            if (m_SurfaceTextures.Count >= kMaxSlowRouteEntries)
            {
                m_SurfaceTextures.Clear();
            }

            m_SurfaceTextures[name] = result;
            return result;
        }

        /// <summary>Texture level to read: decals keep up to 4096 (their paint is read from the atlas), the rest one level.</summary>
        private int TextureTarget(SurfaceAsset surface, MaterialLibrary library, int captureSize)
        {
            if (IsDecalTemplate(surface, library))
            {
                return 4096;
            }

            return captureSize > 1024 ? 2048 : (captureSize > 512 ? 1024 : 512);
        }

        private bool IsDecalTemplate(SurfaceAsset surface, MaterialLibrary library)
        {
            try
            {
                int hash = surface.materialTemplateHash;

                if (!m_DecalTemplate.TryGetValue(hash, out bool decal))
                {
                    var template = library?.GetMaterial(hash);
                    decal = (template?.shader?.name ?? string.Empty).IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0;
                    m_DecalTemplate[hash] = decal;
                }

                return decal;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Queues the decode of a surface a building will need: the main thread plans, the workers
        /// decode. <paramref name="allowUpgrade"/>: a resident lower level is dropped and read again
        /// at the higher one (only before the building's own materials exist).
        /// </summary>
        private void Prefetch(SurfaceAsset surface, MaterialLibrary library, int captureSize, bool allowUpgrade)
        {
            if (surface == null)
            {
                return;
            }

            string name = surface.name;

            if (m_FastFailed.Contains(name) || m_SurfaceTextures.ContainsKey(name))
            {
                return;
            }

            int target = TextureTarget(surface, library, captureSize);

            if (m_Fast.TryGetValue(name, out var have))
            {
                if (have.Target >= target || !allowUpgrade)
                {
                    return;
                }

                DropFast(name);
            }

            if (m_FastPending.TryGetValue(name, out var pending))
            {
                if (pending.Target >= target)
                {
                    return;
                }

                FastVtLoader.Cancel(pending);
                m_FastPending.Remove(name);
            }

            var job = PlanFast(surface, library, target);

            if (job == null)
            {
                m_FastFailed.Add(name);
                return;
            }

            FastVtLoader.Start(job);
            m_FastPending[name] = job;
        }

        /// <summary>
        /// The surfaces of the NEXT building in the queue, with the texture size its icon will use
        /// (its framing is predicted from the same bounds). Queued while the current building is on
        /// the GPU, so CPU decoding overlaps GPU rendering.
        /// </summary>
        private List<(SurfaceAsset surface, int capture)> UpcomingSurfaces()
        {
            var result = new List<(SurfaceAsset, int)>();

            try
            {
                if (m_Queue == null || m_QueueIndex >= m_Queue.Count || !(m_Queue[m_QueueIndex] is ObjectGeometryPrefab next) || next.m_Meshes == null)
                {
                    return result;
                }

                var placements = new List<Placement>();
                CollectPlacements(next, next, Vector3.zero, Quaternion.identity, 0, placements, new CollectStats());
                FrameBounds(placements, next, out var nextMin, out var nextMax);
                float nextMpp = CaptureRig.PredictMetresPerOutputPixel(nextMin, nextMax);
                var seen = new HashSet<string>();

                foreach (var placement in placements)
                {
                    foreach (var surface in placement.Render.surfaceAssets)
                    {
                        if (surface != null && seen.Add(surface.name))
                        {
                            result.Add((surface, placement.IsBuilding ? BuildingCapture(placement.Render, nextMpp) : 512));
                        }
                    }
                }
            }
            catch
            {
                // Prefetching is an optimisation: the next building loads what it needs itself.
            }

            return result;
        }

        /// <summary>Identity, size and date of a surface asset (see <see cref="AppendStamp"/>), read once per run.</summary>
        private string SurfaceStamp(SurfaceAsset surface)
        {
            if (!m_SurfaceStamps.TryGetValue(surface, out string stamp))
            {
                var text = new System.Text.StringBuilder(64);
                AppendStamp(text, surface);
                stamp = text.ToString();
                m_SurfaceStamps[surface] = stamp;
            }

            return stamp;
        }

        /// <summary>A queued plan for the surface, from the persistent plan index when possible.</summary>
        private FastVtLoader.Job PlanFast(SurfaceAsset surface, MaterialLibrary library, int target)
        {
            PlanIndex.EnsureLoaded();

            // The content stamp keeps a plan from outliving an update of the surface's textures.
            string key = surface.name + "|" + target + "|" + SurfaceStamp(surface);

            if (PlanIndex.TryGet(key, out var known))
            {
                if (known.MissingTextures)
                {
                    m_SurfacesMissingTextures.Add(surface.name);
                }

                return StripResident(known);
            }

            var job = PlanFastCore(surface, library, target);

            if (job != null)
            {
                PlanIndex.Put(key, job);
            }

            return StripResident(job);
        }

        /// <summary>
        /// Main thread: header and tile table of ONE mip level of the surface (a VT surface asset),
        /// or of each of its textures (VT props without a surface asset). Null means the slow route.
        /// </summary>
        private FastVtLoader.Job PlanFastCore(SurfaceAsset surface, MaterialLibrary library, int target)
        {
            if ((!surface.hasVTSurfaceAsset && !surface.isVTMaterial) || !FastVtLoader.Available)
            {
                return null;
            }

            try
            {
                int hash = surface.materialTemplateHash;
                var description = library?.GetMaterialDescription(hash);

                if (description == null || !description.IsValid())
                {
                    return null;
                }

                var names = TemplateNames(library, hash, description);
                var job = new FastVtLoader.Job { Name = surface.name, Target = target };

                if (surface.hasVTSurfaceAsset && surface.vtSurfaceAsset != null)
                {
                    if (!FastVtLoader.PlanSurface(surface.vtSurfaceAsset, (s, l) => names.TryGetValue((s * 4) + l, out var n) ? n : null, target, job, out _))
                    {
                        return null;
                    }

                    if (!job.IsEmpty)
                    {
                        return job;
                    }
                }

                // VT props: one VT texture asset per property, found through the surface's
                // pre-processed GUIDs.
                try
                {
                    surface.LoadHeader();
                }
                catch
                {
                    // The GUID enumeration below reports the failure.
                }

                var guids = new List<Hash128>();

                try
                {
                    var enumerable = surface.preProcessedTextureGuids;

                    if (enumerable != null)
                    {
                        guids.AddRange(enumerable);
                    }
                }
                catch
                {
                    return null;
                }

                foreach (var pair in names)
                {
                    if (pair.Key >= guids.Count || !guids[pair.Key].isValid)
                    {
                        continue;
                    }

                    var asset = FindTextureAsset(surface, guids[pair.Key]);

                    if (asset == null)
                    {
                        // A texture the database does not know: its pack is not loaded.
                        m_SurfacesMissingTextures.Add(surface.name);
                        job.MissingTextures = true;
                        continue;
                    }

                    bool planned;
                    string guidKey = guids[pair.Key].ToString() + "|" + target;

                    try
                    {
                        planned = FastVtLoader.PlanTexture(asset, pair.Value, target, job, out _, guidKey);
                    }
                    catch
                    {
                        planned = false;
                    }

                    if (!planned)
                    {
                        // Unusual layout: loaded with the asset's own loader when the job finishes.
                        job.Slow.Add(new KeyValuePair<string, VTTextureAsset>(pair.Value, asset));
                    }
                }

                return job.IsEmpty ? null : job;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A VT texture asset by GUID: the surface's own database first, then the global one (as the game does).</summary>
        private static VTTextureAsset FindTextureAsset(SurfaceAsset surface, Hash128 guid)
        {
            if (surface.database is IAssetDatabase local && local.TryGetAsset<VTTextureAsset>(guid, out var localAsset))
            {
                return localAsset;
            }

            return AssetDatabase.global.TryGetAsset<VTTextureAsset>(guid, out var globalAsset) ? globalAsset : null;
        }

        /// <summary>(stack * 4 + layer) -> shader property for a material template, cached per template.</summary>
        private Dictionary<int, string> TemplateNames(MaterialLibrary library, int hash, MaterialLibrary.MaterialDescription description)
        {
            if (m_TemplateNames.TryGetValue(hash, out var names))
            {
                return names;
            }

            names = new Dictionary<int, string>();

            foreach (var property in TexturePropertyNames(library, hash))
            {
                int stack = description.GetStackConfigIndex(property);

                if (stack < 0)
                {
                    continue;
                }

                int layer = description.GetLayerIndexInStack(stack, property);

                if (layer >= 0)
                {
                    names[(stack * 4) + layer] = property;
                }
            }

            m_TemplateNames[hash] = names;
            return names;
        }

        /// <summary>
        /// The texture property names of a material template, from its shader. The surface's own
        /// texture dictionary is only filled by <c>LoadProperties</c>, which changes the surface's
        /// state; the shader knows its property list and changes nothing.
        /// </summary>
        private static List<string> TexturePropertyNames(MaterialLibrary library, int templateHash)
        {
            var names = new List<string>();

            try
            {
                var shader = library?.GetMaterial(templateHash)?.shader;

                if (shader == null)
                {
                    return names;
                }

                int count = shader.GetPropertyCount();

                for (int i = 0; i < count; i++)
                {
                    if (shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture)
                    {
                        names.Add(shader.GetPropertyName(i));
                    }
                }
            }
            catch
            {
                // No names: the surface is read through the slow route.
            }

            return names;
        }

        /// <summary>The prefetched job (or a new one), waited for and turned into textures. Null means the slow route.</summary>
        private Dictionary<string, Texture> LoadFast(SurfaceAsset surface, MaterialLibrary library, int target)
        {
            string name = surface.name;

            if (m_FastFailed.Contains(name))
            {
                return null;
            }

            if (m_FastPending.TryGetValue(name, out var job))
            {
                m_FastPending.Remove(name);
            }
            else
            {
                job = PlanFast(surface, library, target);

                if (job != null)
                {
                    FastVtLoader.Start(job);
                }
            }

            if (job != null)
            {
                FastVtLoader.Wait(job);

                if (job.Error == null)
                {
                    var entry = new FastEntry { Target = job.Target, Owned = new List<Texture2D>(), Stamp = ++m_Tick, Building = m_BuildingSerial };

                    try
                    {
                        entry.Textures = FastVtLoader.Finish(job, entry.Owned, out entry.Bytes);

                        // Textures the fast path could not read were loaded at full resolution.
                        foreach (var slow in job.Slow)
                        {
                            m_LoadedVtTextures.Add(slow.Value);
                        }
                    }
                    catch
                    {
                        entry.Textures = new Dictionary<string, Texture>();
                    }

                    try
                    {
                        SettleShared(job, entry);
                    }
                    catch
                    {
                        // The surface keeps its own copies.
                    }

                    if (entry.Textures.Count > 0)
                    {
                        m_Fast[name] = entry;
                        OrderFast(name);
                        m_FastBytes += entry.Bytes;
                        m_BuildingVt.Add(name);
                        return entry.Textures;
                    }

                    foreach (var texture in entry.Owned)
                    {
                        if (texture != null)
                        {
                            UnityEngine.Object.DestroyImmediate(texture);
                        }
                    }

                    foreach (var key in entry.SharedKeys ?? new List<string>())
                    {
                        ReleaseShared(key);
                    }
                }
            }

            m_FastFailed.Add(name);
            return null;
        }

        /// <summary>Reads already resident as shared textures are taken out of the job, reserving a reference.</summary>
        private FastVtLoader.Job StripResident(FastVtLoader.Job job)
        {
            if (job == null)
            {
                return null;
            }

            for (int g = job.Groups.Count - 1; g >= 0; g--)
            {
                var group = job.Groups[g];

                if (group.Layers.Count == 1 && group.Layers[0].Key != null
                    && m_Shared.TryGetValue(group.Layers[0].Key, out var shared) && shared.Texture != null)
                {
                    shared.Refs++;
                    job.Shared.Add(new KeyValuePair<string, string>(group.Layers[0].Property, group.Layers[0].Key));
                    job.TileCount -= group.Tiles.Count;

                    foreach (var tile in group.Tiles)
                    {
                        job.CompressedBytes -= tile.Length;
                    }

                    job.Groups.RemoveAt(g);
                }
            }

            for (int d = job.Direct.Count - 1; d >= 0; d--)
            {
                var layer = job.Direct[d];

                if (layer.Key != null && m_Shared.TryGetValue(layer.Key, out var shared) && shared.Texture != null)
                {
                    shared.Refs++;
                    job.Shared.Add(new KeyValuePair<string, string>(layer.Property, layer.Key));
                    job.Direct.RemoveAt(d);
                }
            }

            return job;
        }

        /// <summary>After a job finishes: hands out the reserved shared textures and publishes the new shareable ones.</summary>
        private void SettleShared(FastVtLoader.Job job, FastEntry entry)
        {
            entry.SharedKeys = new List<string>();

            foreach (var pair in job.Shared)
            {
                if (m_Shared.TryGetValue(pair.Value, out var shared) && shared.Texture != null)
                {
                    entry.Textures[pair.Key] = shared.Texture;
                    entry.SharedKeys.Add(pair.Value);
                }
            }

            foreach (var layer in job.Groups.SelectMany(g => g.Layers).Concat(job.Direct))
            {
                if (layer.Key == null || layer.Texture == null || m_Shared.ContainsKey(layer.Key))
                {
                    continue;
                }

                m_Shared[layer.Key] = new SharedTexture { Texture = layer.Texture, Refs = 1, Bytes = layer.Bytes };
                m_SharedBytes += layer.Bytes;
                entry.Owned.Remove(layer.Texture);
                entry.Bytes -= layer.Bytes;
                entry.SharedKeys.Add(layer.Key);
            }
        }

        private void ReleaseShared(string key)
        {
            if (key == null || !m_Shared.TryGetValue(key, out var shared) || --shared.Refs > 0)
            {
                return;
            }

            if (shared.Texture != null)
            {
                UnityEngine.Object.DestroyImmediate(shared.Texture);
            }

            m_SharedBytes -= shared.Bytes;
            m_Shared.Remove(key);
        }

        /// <summary>Frees a fast-read surface, after evicting the materials that bind its textures.</summary>
        private void DropFast(string name)
        {
            if (!m_Fast.TryGetValue(name, out var entry))
            {
                return;
            }

            foreach (var key in MaterialsUsing(name))
            {
                Evict(key);
            }

            m_GpuSources.Remove(name);

            foreach (var texture in entry.Owned)
            {
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }

            foreach (var key in entry.SharedKeys ?? new List<string>())
            {
                ReleaseShared(key);
            }

            m_FastBytes -= entry.Bytes;
            m_Fast.Remove(name);
            m_FastOrder.Remove(name);
        }

        /// <summary>End of a run: cancels pending decodes and frees every fast-read and shared texture.</summary>
        private void ReleaseFast()
        {
            foreach (var job in m_FastPending.Values)
            {
                FastVtLoader.Cancel(job);
            }

            foreach (var job in m_FastPending.Values)
            {
                job.Done.Wait(5000);
            }

            m_FastPending.Clear();

            foreach (var entry in m_Fast.Values)
            {
                foreach (var texture in entry.Owned)
                {
                    if (texture != null)
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                    }
                }
            }

            m_Fast.Clear();
            m_FastOrder.Clear();
            m_FastFailed.Clear();
            m_FastBytes = 0;

            foreach (var shared in m_Shared.Values)
            {
                if (shared.Texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(shared.Texture);
                }
            }

            m_Shared.Clear();
            m_SharedBytes = 0;
            m_TemplateNames.Clear();
        }

        /// <summary>
        /// Slow route: plain textures for non-VT surfaces; for VT surfaces the material that the VT
        /// surface asset loads with every texture bound, else the per-texture assets.
        /// </summary>
        private Dictionary<string, Texture> LoadSurfaceTexturesSlow(SurfaceAsset surface, MaterialLibrary library)
        {
            if (!surface.hasVTSurfaceAsset && !surface.isVTMaterial)
            {
                return LoadNonVtTextures(surface);
            }

            if (surface.hasVTSurfaceAsset && surface.vtSurfaceAsset != null)
            {
                try
                {
                    var material = LoadVt(surface);

                    if (material != null && material.shader != null)
                    {
                        var all = BoundTextures(material, null);

                        if (all.Count > 0)
                        {
                            return all;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn($"Textures of '{surface.name}' not loaded: {ex.GetType().Name}: {ex.Message}");
                }
            }

            var result = new Dictionary<string, Texture>();

            try
            {
                surface.LoadHeader();
            }
            catch
            {
                // Reported through the GUID enumeration below.
            }

            int templateHash;
            var guids = new List<Hash128>();

            try
            {
                templateHash = surface.materialTemplateHash;
                var enumerable = surface.preProcessedTextureGuids;

                if (enumerable != null)
                {
                    guids.AddRange(enumerable);
                }
            }
            catch
            {
                return result;
            }

            var description = library?.GetMaterialDescription(templateHash);

            if (description == null || !description.IsValid())
            {
                return result;
            }

            foreach (var property in TexturePropertyNames(library, templateHash))
            {
                try
                {
                    int stack = description.GetStackConfigIndex(property);
                    int layer = stack >= 0 ? description.GetLayerIndexInStack(stack, property) : -1;

                    // Header index is stack * 4 + layer.
                    int index = (stack * 4) + layer;

                    if (stack < 0 || layer < 0 || index >= guids.Count || !guids[index].isValid)
                    {
                        continue;
                    }

                    var texture = LoadTracked(FindTextureAsset(surface, guids[index]));

                    if (texture != null)
                    {
                        result[property] = texture;
                    }
                }
                catch
                {
                    // That property stays unbound.
                }
            }

            return result;
        }

        /// <summary>
        /// The textures bound on a material: the given properties, or every texture property of its
        /// shader when <paramref name="properties"/> is null.
        /// </summary>
        private static Dictionary<string, Texture> BoundTextures(Material material, params string[] properties)
        {
            var result = new Dictionary<string, Texture>();
            var shader = material.shader;

            if (properties == null)
            {
                var list = new List<string>();
                int count = shader != null ? shader.GetPropertyCount() : 0;

                for (int i = 0; i < count; i++)
                {
                    if (shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture)
                    {
                        list.Add(shader.GetPropertyName(i));
                    }
                }

                properties = list.ToArray();
            }

            foreach (var property in properties)
            {
                try
                {
                    var texture = material.HasProperty(property) ? material.GetTexture(property) : null;

                    if (texture != null)
                    {
                        result[property] = texture;
                    }
                }
                catch
                {
                    // Unbound.
                }
            }

            return result;
        }

        /// <summary>
        /// The plain textures of a non-VT surface, leaving no trace on it: its properties are loaded
        /// (<c>LoadProperties(false)</c>), the texture assets loaded and the non-texture values copied,
        /// then the properties are released and the header re-read, which is the state the game left
        /// the surface in.
        /// </summary>
        private Dictionary<string, Texture> LoadNonVtTextures(SurfaceAsset surface)
        {
            if (m_NonVt.TryGetValue(surface.name, out var cached))
            {
                return cached;
            }

            var result = new Dictionary<string, Texture>();
            m_NonVt[surface.name] = result;

            if (surface.isVTMaterial)
            {
                return result;
            }

            bool loaded = false;

            try
            {
                surface.LoadProperties(false);
                loaded = true;

                var props = new SurfaceProps();
                m_NonVtProps[surface.name] = props;

                if (surface.floats != null)
                {
                    foreach (var e in surface.floats)
                    {
                        props.Floats[e.Key] = e.Value;
                    }
                }

                if (surface.keywords != null)
                {
                    props.Keywords.AddRange(surface.keywords);
                }

                if (surface.textures != null)
                {
                    foreach (var entry in surface.textures)
                    {
                        try
                        {
                            var texture = LoadTracked(entry.Value);

                            if (texture != null)
                            {
                                result[entry.Key] = texture;
                            }
                        }
                        catch
                        {
                            // That property stays unbound.
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Textures of '{surface.name}' not loaded: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (loaded)
                {
                    try
                    {
                        if (surface.UnloadProperties(false))
                        {
                            surface.LoadHeader();
                        }
                    }
                    catch (Exception ex)
                    {
                        Mod.Log.Warn($"Surface '{surface.name}' not restored: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            return result;
        }

        /// <summary>Loads the full material of a VT surface asset (all textures decoded at full resolution), tracked for eviction.</summary>
        private Material LoadVt(SurfaceAsset surface)
        {
            var asset = surface.vtSurfaceAsset;

            if (asset == null)
            {
                return null;
            }

            m_BuildingVt.Add(surface.name);
            m_VtLoaded[surface.name] = (asset, ++m_Tick);
            return asset.Load();
        }
    }
}
