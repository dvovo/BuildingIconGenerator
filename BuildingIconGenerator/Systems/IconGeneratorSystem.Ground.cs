// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Systems
{
    using System;
    using System.Collections.Generic;
    using BuildingIconGenerator.Geometry;
    using BuildingIconGenerator.Rendering;
    using Colossal.IO.AssetDatabase;
    using Game.Prefabs;
    using UnityEngine;
    using UnityEngine.Rendering;
    using UnityEngine.Rendering.HighDefinition;

    /// <content>
    /// The lot: a grass base under the whole parcel, the building's sub-areas (asphalt, paving,
    /// grass...) in the game's renderer-priority order, and the decals lying on the ground, all
    /// composed on the GPU into one top-down texture (<see cref="GroundCompositor"/>) laid on a
    /// single plane.
    /// </content>
    public sealed partial class IconGeneratorSystem
    {
        /// <summary>World metres per repeat of an area texture.</summary>
        private const float kAreaTiling = 8f;

        /// <summary>Tint shared by every non-grass ground layer: paved areas and ground decals alike.</summary>
        private static readonly Color kGroundTint = new Color(1.08f, 1.08f, 1.08f, 1f);

        /// <summary>Tint of grass textures, which render dull under the icon lighting.</summary>
        private static readonly Color kGrassTint = new Color(1.12f, 1.3f, 1.0f, 1f);

        /// <summary>Base colour of the lot when no grass texture is available.</summary>
        private static readonly Color kLotColour = new Color(0.36f, 0.45f, 0.22f, 1f);

        private readonly List<RenderTexture> m_GroundTargets = new List<RenderTexture>();
        private GroundCompositor m_Ground;
        private PrefabBase m_GrassPrefab;
        private RenderedArea m_GrassRendered;

        /// <summary>
        /// Adds the lot rectangle (lot size in 8 m cells, see <see cref="TryGetLot"/>) and every sub-area polygon of
        /// the building to the ground composition. Returns the number of layers added.
        /// </summary>
        private int BuildGround(PrefabBase prefab, ref Vector3 min, ref Vector3 max)
        {
            int parts = 0;

            if (TryGetLot(prefab, out int lotWidth, out int lotDepth))
            {
                float hw = lotWidth * 4f;
                float hd = lotDepth * 4f;
                var outline = new[] { new Vector3(-hw, 0f, -hd), new Vector3(hw, 0f, -hd), new Vector3(hw, 0f, hd), new Vector3(-hw, 0f, hd) };
                Texture texture = null;
                var tint = kLotColour;
                float metres = kAreaTiling;

                // The game's own grass surface under everything, so it blends with the lot's grass areas.
                var grass = LotGrass(prefab);

                if (grass.rendered != null)
                {
                    try
                    {
                        var tiling = Vector2.one;
                        TextureAsset grassAsset = grass.rendered.m_BaseColorMap;
                        Texture grassTexture = LoadTracked(grassAsset);

                        if (grassTexture == null && grass.rendered.m_Material != null)
                        {
                            grassTexture = AlbedoFromMaterial(grass.rendered.m_Material, out tiling);
                        }

                        if (grassTexture != null)
                        {
                            texture = grassTexture;
                            tint = kGrassTint;
                            metres = kAreaTiling / Mathf.Max(0.01f, tiling.x);
                        }
                    }
                    catch
                    {
                        // Plain lot colour.
                    }
                }

                if (AddGroundLayer(outline, texture, tint, metres, ref min, ref max))
                {
                    parts++;
                }
            }

            if (!prefab.TryGet(out ObjectSubAreas areas) || areas.m_SubAreas == null)
            {
                return parts;
            }

            // Collected first, then drawn in the game's own order: renderer priority, low to high.
            var layers = new List<(int priority, int index, Vector3[] outline, PrefabBase prefab, RenderedArea rendered, Color? fallback)>();
            int index = 0;

            foreach (var area in areas.m_SubAreas)
            {
                index++;

                if (area?.m_NodePositions == null || area.m_NodePositions.Length < 3)
                {
                    continue;
                }

                var outline = new Vector3[area.m_NodePositions.Length];

                for (int i = 0; i < outline.Length; i++)
                {
                    outline[i] = new Vector3(area.m_NodePositions[i].x, area.m_NodePositions[i].y, area.m_NodePositions[i].z);
                }

                // Surface placeholders are swapped by the game for a real surface; logic areas
                // (walking, hang-around) have no rendered surface and are never drawn.
                var areaPrefab = (PrefabBase)area.m_AreaPrefab;

                if (areaPrefab != null && areaPrefab.TryGet(out PlaceholderArea _))
                {
                    areaPrefab = ResolvePlaceholderPrefab(areaPrefab, candidate => candidate.TryGet(out RenderedArea _)) ?? areaPrefab;
                }

                RenderedArea rendered = null;

                if (areaPrefab == null || !areaPrefab.TryGet(out rendered))
                {
                    rendered = null;
                }

                var fallback = SurfaceColourByName(areaPrefab?.name);

                if (rendered == null && fallback == null)
                {
                    continue;
                }

                layers.Add((rendered != null ? rendered.m_RendererPriority : 0, index, outline, areaPrefab, rendered, fallback));
            }

            layers.Sort((a, b) => a.priority != b.priority ? a.priority.CompareTo(b.priority) : a.index.CompareTo(b.index));

            foreach (var entry in layers)
            {
                var tint = entry.fallback ?? Color.white;
                Texture texture = null;
                var tiling = Vector2.one;

                try
                {
                    if (entry.rendered != null)
                    {
                        TextureAsset asset = entry.rendered.m_BaseColorMap;
                        texture = LoadTracked(asset);

                        // Real surfaces keep their look in the area material rather than in m_BaseColorMap.
                        if (texture == null && entry.rendered.m_Material != null)
                        {
                            texture = AlbedoFromMaterial(entry.rendered.m_Material, out tiling);
                        }

                        if (texture != null)
                        {
                            // One neutral treatment for everything lying on the ground; grass keeps its green lift.
                            bool isGrass = (entry.prefab?.name ?? string.Empty).IndexOf("grass", StringComparison.OrdinalIgnoreCase) >= 0;
                            tint = isGrass ? kGrassTint : kGroundTint;
                        }
                        else if (entry.fallback == null)
                        {
                            tint = entry.rendered.m_BaseColor;
                        }
                    }
                }
                catch
                {
                    // Drawn with its fallback colour.
                }

                if (AddGroundLayer(entry.outline, texture, tint, kAreaTiling / Mathf.Max(0.01f, tiling.x), ref min, ref max))
                {
                    parts++;
                }
            }

            return parts;
        }

        /// <summary>
        /// The game's grass surface for the lot base: <c>Grass Surface 01</c> from the prefab system,
        /// else the first grass area of this building (or of an earlier one), remembered for the session.
        /// </summary>
        private (PrefabBase prefab, RenderedArea rendered) LotGrass(PrefabBase building)
        {
            if (m_GrassRendered != null)
            {
                return (m_GrassPrefab, m_GrassRendered);
            }

            try
            {
                if (m_PrefabSystem.TryGetPrefab(new PrefabID("SurfacePrefab", "Grass Surface 01", default), out PrefabBase named)
                    && named != null && named.TryGet(out RenderedArea namedRendered))
                {
                    m_GrassPrefab = named;
                    m_GrassRendered = namedRendered;
                    return (m_GrassPrefab, m_GrassRendered);
                }
            }
            catch
            {
                // Fall back to the building's own grass areas.
            }

            if (building.TryGet(out ObjectSubAreas areas) && areas.m_SubAreas != null)
            {
                foreach (var area in areas.m_SubAreas)
                {
                    var candidate = (PrefabBase)area?.m_AreaPrefab;

                    if (candidate != null && candidate.TryGet(out PlaceholderArea _))
                    {
                        candidate = ResolvePlaceholderPrefab(candidate, c => c.TryGet(out RenderedArea _)) ?? candidate;
                    }

                    if (candidate != null && candidate.name.IndexOf("grass", StringComparison.OrdinalIgnoreCase) >= 0
                        && candidate.TryGet(out RenderedArea rendered))
                    {
                        m_GrassPrefab = candidate;
                        m_GrassRendered = rendered;
                        break;
                    }
                }
            }

            return (m_GrassPrefab, m_GrassRendered);
        }

        /// <summary>
        /// The colour texture of an area material: the first texture property whose name says
        /// base colour / albedo / diffuse / main texture, else the first bound texture. A texture
        /// array (layered terrain-style surfaces) is reduced to its slice 0.
        /// </summary>
        private Texture AlbedoFromMaterial(Material source, out Vector2 tiling)
        {
            tiling = Vector2.one;
            var shader = source.shader;

            if (shader == null)
            {
                return null;
            }

            string chosen = null;
            string first = null;

            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture)
                {
                    continue;
                }

                string name = shader.GetPropertyName(i);

                if (source.GetTexture(name) == null)
                {
                    continue;
                }

                first = first ?? name;
                string lower = name.ToLowerInvariant();

                if (chosen == null && (lower.Contains("basecolor") || lower.Contains("albedo") || lower.Contains("diffuse") || lower.Contains("maintex")))
                {
                    chosen = name;
                }
            }

            chosen = chosen ?? first;

            if (chosen == null)
            {
                return null;
            }

            tiling = source.GetTextureScale(chosen);

            if (tiling.x <= 0f)
            {
                tiling = Vector2.one;
            }

            var picked = source.GetTexture(chosen);

            if (picked is Texture2DArray array)
            {
                try
                {
                    var slice = new Texture2D(array.width, array.height, array.format, array.mipmapCount > 1)
                    {
                        hideFlags = HideFlags.HideAndDontSave,
                        wrapMode = TextureWrapMode.Repeat,
                    };

                    Graphics.CopyTexture(array, 0, slice, 0);
                    m_Baked.Add(slice);
                    return slice;
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn($"Texture array of '{source.name}' not usable: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }

            return picked as Texture2D;
        }

        /// <summary>Plain colour of a surface by its name (asphalt, paving, grass, gravel), for when no texture loads; null otherwise.</summary>
        private static Color? SurfaceColourByName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            string n = name.ToLowerInvariant();

            if (n.Contains("asphalt") || n.Contains("pavement") || n.Contains("parking"))
            {
                return new Color(0.30f, 0.30f, 0.31f, 1f);
            }

            if (n.Contains("tile") || n.Contains("paving") || n.Contains("concrete"))
            {
                return new Color(0.62f, 0.59f, 0.54f, 1f);
            }

            if (n.Contains("grass") || n.Contains("lawn"))
            {
                return new Color(0.33f, 0.50f, 0.20f, 1f);
            }

            if (n.Contains("gravel") || n.Contains("sand") || n.Contains("dirt"))
            {
                return new Color(0.58f, 0.52f, 0.42f, 1f);
            }

            return null;
        }

        /// <summary>Adds a filled ground surface to the composition and grows the bounds by it.</summary>
        private bool AddGroundLayer(Vector3[] outline, Texture texture, Color tint, float metresPerRepeat, ref Vector3 min, ref Vector3 max)
        {
            var flat = new List<Vector3>();
            var triangles = new List<int>();
            Polygon.ScanlineFill(outline, flat, triangles);

            if (triangles.Count < 3)
            {
                return false;
            }

            m_Ground.AddLayer(flat, triangles, texture, tint, metresPerRepeat);

            foreach (var v in flat)
            {
                var p = new Vector3(v.x, 0f, v.z);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            return true;
        }

        /// <summary>
        /// A decal lying on the ground (near y = 0, projecting downwards): its atlas rectangle goes into
        /// the ground composition with its real shape and alpha. Curved lane decals become a strip of
        /// quads following the curve, the atlas rectangle's v spread along it and u across it, as the
        /// game's curved-decal shader does. Other decals (roofs, walls) return false.
        /// </summary>
        private bool AddGroundDecal(Placement placement, Material material, ref Vector3 min, ref Vector3 max)
        {
            if (m_Ground == null || material == null)
            {
                return false;
            }

            var up = placement.Rotation * Vector3.up;

            if (Mathf.Abs(placement.Position.y) > 0.6f || up.y < 0.9f)
            {
                return false;
            }

            var atlas = material.HasProperty("_BaseColorMap") ? material.GetTexture("_BaseColorMap") : null;

            if (atlas == null)
            {
                return false;
            }

            var render = placement.Render;
            var uv = new Rect(0f, 0f, 1f, 1f);

            if (render.TryGet(out DecalProperties decal))
            {
                uv = Rect.MinMaxRect(decal.m_TextureArea.min.x, decal.m_TextureArea.min.y, decal.m_TextureArea.max.x, decal.m_TextureArea.max.y);
            }

            var b = render.bounds;
            var scale = placement.Scale == Vector3.zero ? Vector3.one : placement.Scale;

            if (placement.Curve != null && placement.Curve.Length >= 2)
            {
                var arc = placement.Curve;
                int n = arc.Length;
                float half = (b.max.x - b.min.x) * 0.5f * scale.x;
                var left = new Vector2[n];
                var right = new Vector2[n];

                for (int i = 0; i < n; i++)
                {
                    var tangent = arc[Mathf.Min(i + 1, n - 1)] - arc[Mathf.Max(i - 1, 0)];
                    tangent.y = 0f;

                    if (tangent.sqrMagnitude < 1e-8f)
                    {
                        tangent = placement.Rotation * Vector3.forward;
                        tangent.y = 0f;
                    }

                    var side = Vector3.Cross(Vector3.up, tangent.normalized) * half;
                    var l = arc[i] - side;
                    var r = arc[i] + side;
                    left[i] = new Vector2(l.x, l.z);
                    right[i] = new Vector2(r.x, r.z);
                    min = Vector3.Min(min, Vector3.Min(l, r));
                    max = Vector3.Max(max, Vector3.Max(l, r));
                }

                for (int i = 0; i < n - 1; i++)
                {
                    float v0 = Mathf.Lerp(uv.yMin, uv.yMax, i / (float)(n - 1));
                    float v1 = Mathf.Lerp(uv.yMin, uv.yMax, (i + 1) / (float)(n - 1));
                    m_Ground.AddDecal(atlas, Rect.MinMaxRect(uv.xMin, v0, uv.xMax, v1), left[i], right[i], right[i + 1], left[i + 1], kGroundTint);
                }

                return true;
            }

            var corners = new Vector3[4];
            corners[0] = placement.Position + (placement.Rotation * Vector3.Scale(new Vector3(b.min.x, 0f, b.min.z), scale));
            corners[1] = placement.Position + (placement.Rotation * Vector3.Scale(new Vector3(b.max.x, 0f, b.min.z), scale));
            corners[2] = placement.Position + (placement.Rotation * Vector3.Scale(new Vector3(b.max.x, 0f, b.max.z), scale));
            corners[3] = placement.Position + (placement.Rotation * Vector3.Scale(new Vector3(b.min.x, 0f, b.max.z), scale));

            foreach (var c in corners)
            {
                min = Vector3.Min(min, c);
                max = Vector3.Max(max, c);
            }

            m_Ground.AddDecal(
                atlas,
                uv,
                new Vector2(corners[0].x, corners[0].z),
                new Vector2(corners[1].x, corners[1].z),
                new Vector2(corners[2].x, corners[2].z),
                new Vector2(corners[3].x, corners[3].z),
                kGroundTint);
            return true;
        }

        /// <summary>
        /// After the final framing: composes the ground texture at one texel per rendered pixel and
        /// lays it on a plane, alpha-cut where nothing was drawn.
        /// </summary>
        private void FinishGround(GameObject root, List<Material> created, List<Mesh> meshes)
        {
            if (m_Ground == null || m_Ground.IsEmpty)
            {
                return;
            }

            try
            {
                // The render is 2x the output: one ground texel per rendered pixel.
                var target = m_Ground.Bake(m_Shaders.Splat, m_Rig.MetresPerOutputPixel * 0.5f);

                if (target == null)
                {
                    return;
                }

                m_GroundTargets.Add(target);

                var material = new Material(Lit) { hideFlags = HideFlags.HideAndDontSave };
                created.Add(material);
                material.SetTexture("_BaseColorMap", target);
                material.SetColor("_BaseColor", Color.white);
                material.SetFloat("_Smoothness", 0f);
                material.SetFloat("_Metallic", 0f);
                material.SetFloat("_AlphaCutoffEnable", 1f);
                material.SetFloat("_AlphaCutoff", 0.5f);
                material.EnableKeyword("_ALPHATEST_ON");
                material.SetFloat("_DoubleSidedEnable", 1f);
                HDMaterial.ValidateMaterial(material);

                var lo = m_Ground.Min;
                var hi = m_Ground.Max;
                var mesh = new Mesh { name = "BuildingIconGenerator_Ground", hideFlags = HideFlags.HideAndDontSave };
                mesh.vertices = new[] { new Vector3(lo.x, 0f, lo.y), new Vector3(hi.x, 0f, lo.y), new Vector3(hi.x, 0f, hi.y), new Vector3(lo.x, 0f, hi.y) };
                mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
                mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
                mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
                mesh.RecalculateBounds();
                meshes.Add(mesh);

                var part = new GameObject("BuildingIconGenerator_Ground") { hideFlags = HideFlags.HideAndDontSave };
                part.transform.SetParent(root.transform, false);
                part.layer = CaptureRig.kLayer;
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = part.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.renderingLayerMask = uint.MaxValue;
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Ground composition failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ReleaseGroundTargets()
        {
            foreach (var target in m_GroundTargets)
            {
                if (target != null)
                {
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                }
            }

            m_GroundTargets.Clear();
        }
    }
}
