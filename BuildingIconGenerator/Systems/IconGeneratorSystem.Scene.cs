// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using BuildingIconGenerator.Geometry;
    using Colossal.IO.AssetDatabase;
    using Game.Prefabs;
    using Game.Rendering;
    using Unity.Entities;
    using UnityEngine;
    using ObjectState = Game.Objects.ObjectState;

    /// <content>What to draw: the building, its sub-objects and lanes, placeholders and colour slots.</content>
    public sealed partial class IconGeneratorSystem
    {
        /// <summary>Trees are kept below this share of the building's height (and at least 3 m).</summary>
        private const float kTreeHeightShare = 0.45f;

        /// <summary>Deepest sub-object nesting followed (building -> object -> object -> object).</summary>
        private const int kMaxSubObjectDepth = 3;

        /// <summary>Sub-objects placed with a lower probability are left out of an icon.</summary>
        private const int kMinProbability = 50;

        /// <summary>Trees and plants: a younger growth stage first, so they do not hide the building.</summary>
        private static readonly ObjectState[] kYoungPreference =
        {
            ObjectState.Teen,
            ObjectState.Child,
            ObjectState.Adult,
        };

        /// <summary>Preferred state when an object only has conditional meshes, best first.</summary>
        private static readonly ObjectState[] kStatePreference =
        {
            ObjectState.Adult,
            ObjectState.Teen,
            ObjectState.Full,
            ObjectState.RighthandTraffic,
            ObjectState.Warm,
            ObjectState.Clear,
            ObjectState.Home,
        };

        /// <summary>Lanes that are infrastructure rather than scenery: never drawn in an icon.</summary>
        private static readonly string[] kLaneSkip =
        {
            "pipe", "cable", "utility", "water", "sewage", "electric", "power", "pedestrian", "parking",
            "car", "track", "connection", "path",
        };

        private static Color[] s_BrandColors;
        private bool m_BrandLoaded;

        /// <summary>One mesh to draw, placed in the building's space.</summary>
        private struct Placement
        {
            public RenderPrefab Render;
            public Vector3 Position;
            public Quaternion Rotation;
            public ColorProperties Colours;

            /// <summary>
            /// Resolved colour slots of the object this one belongs to (null for the building itself).
            /// Channels the game lets the parent override take them (see <see cref="ResolveSlots"/>).
            /// </summary>
            public Color[] ParentSlots;

            /// <summary>The owning object is a tree or a plant.</summary>
            public bool IsPlant;

            /// <summary>A projected decal: drawn into the ground or as paint, never as its projector box.</summary>
            public bool IsDecal;

            /// <summary>The building's own meshes (depth 0): never culled, full texture resolution.</summary>
            public bool IsBuilding;

            /// <summary>Local scale: one normally, below one for shrunk trees, stretched along lanes.</summary>
            public Vector3 Scale;

            /// <summary>
            /// For a lane decal on a curved lane: world points of this copy's stretch of the curve,
            /// so the paint follows the curve as the game's curved-decal shader does. Null otherwise.
            /// </summary>
            public Vector3[] Curve;
        }

        /// <summary>Facts gathered while collecting placements.</summary>
        private sealed class CollectStats
        {
            /// <summary>References that resolve to nothing: content (a pack, a mod) that is not loaded.</summary>
            public int Missing;

            /// <summary>Height of the building's own meshes, for limiting tree size.</summary>
            public float BuildingHeight;
        }

        /// <summary>Usage-index key of a render prefab's geometry (also keys its materials).</summary>
        private static string GeometryKey(RenderPrefab render)
        {
            return "g:" + render.name;
        }

        /// <summary>
        /// The meshes of an object and, recursively, of its sub-objects and lanes, placed in the
        /// building's space.
        ///
        /// <para>Conditional meshes: an object keeps its unconditional meshes plus ONE state (a tree
        /// has a mesh per growth stage, a bin one per fill level, a sign one per traffic side).
        /// Sub-objects: <c>m_Probability</c> 0 means 100 (as the game initialises it) and below 50 is
        /// left out; only the first member of each random group is drawn; objects attached to a mesh
        /// of another state are skipped; placeholders are replaced by the first real object the game
        /// lists for them.</para>
        /// </summary>
        private void CollectPlacements(ObjectGeometryPrefab geometry, PrefabBase owner, Vector3 position,
            Quaternion rotation, int depth, List<Placement> output, CollectStats stats, Color[] parentSlots = null)
        {
            if (geometry == null || geometry.m_Meshes == null)
            {
                return;
            }

            ColorProperties colours = null;

            if (owner == null || !owner.TryGet(out colours))
            {
                colours = null;
            }

            // This object's resolved colours: what its sub-objects and lanes inherit.
            var ownColours = colours;

            if (ownColours == null && geometry.m_Meshes.Length > 0 && geometry.m_Meshes[0].m_Mesh != null
                && geometry.m_Meshes[0].m_Mesh.TryGet(out ColorProperties meshColours))
            {
                ownColours = meshColours;
            }

            var ownSlots = ResolveSlots(ownColours, parentSlots);

            // Trees and plants: a young stage and, if still taller than kTreeHeightShare of the
            // building, scaled down uniformly.
            bool isTree = depth > 0 && owner != null && (owner.TryGet(out TreeObject _) || owner.TryGet(out PlantObject _));
            var state = PickState(geometry, isTree);
            var chosen = new List<(RenderPrefab render, Vector3 position, Quaternion rotation, bool decal)>();
            float top = 0f;

            foreach (var info in geometry.m_Meshes)
            {
                if (!(info?.m_Mesh is RenderPrefab render))
                {
                    continue;
                }

                if (info.m_RequireState != ObjectState.None && info.m_RequireState != state)
                {
                    continue;
                }

                var localPosition = new Vector3(info.m_Position.x, info.m_Position.y, info.m_Position.z);
                var localRotation = new Quaternion(info.m_Rotation.value.x, info.m_Rotation.value.y, info.m_Rotation.value.z, info.m_Rotation.value.w);
                bool decal = render.TryGet(out DecalProperties _);

                if (!decal)
                {
                    try
                    {
                        top = Mathf.Max(top, localPosition.y + render.bounds.max.y);
                    }
                    catch
                    {
                        // Bounds unavailable: the mesh does not count towards the height.
                    }
                }

                chosen.Add((render, localPosition, localRotation, decal));
            }

            float shrink = 1f;

            if (isTree && stats.BuildingHeight > 0f && top > 0f)
            {
                float limit = Mathf.Max(3f, stats.BuildingHeight * kTreeHeightShare);

                if (top > limit)
                {
                    shrink = limit / top;
                }
            }

            foreach (var mesh in chosen)
            {
                output.Add(new Placement
                {
                    Render = mesh.render,
                    Position = position + (rotation * (mesh.position * shrink)),
                    Rotation = rotation * mesh.rotation,
                    Colours = colours,
                    ParentSlots = parentSlots,
                    Scale = mesh.decal ? Vector3.one : Vector3.one * shrink,
                    IsPlant = isTree,
                    IsDecal = mesh.decal,
                    IsBuilding = depth == 0,
                });
            }

            if (depth == 0)
            {
                stats.BuildingHeight = top;
            }

            if (owner != null)
            {
                CollectLanes(owner, position, rotation, output, stats, ownSlots);
            }

            if (depth >= kMaxSubObjectDepth || owner == null || !owner.TryGet(out ObjectSubObjects subObjects)
                || subObjects.m_SubObjects == null)
            {
                return;
            }

            var groupsTaken = new HashSet<int>();

            foreach (var sub in subObjects.m_SubObjects)
            {
                if (!(sub?.m_Object is ObjectGeometryPrefab child))
                {
                    if (sub != null && sub.m_Object == null)
                    {
                        stats.Missing++;
                    }

                    continue;
                }

                // A placeholder is an editor stand-in that the game replaces with one of the objects
                // it lists. No candidate: nothing is drawn rather than the marker.
                if (child.TryGet(out PlaceholderObject _))
                {
                    var real = ResolvePlaceholder(child);

                    if (real == null)
                    {
                        stats.Missing++;
                        continue;
                    }

                    child = real;
                }

                int probability = sub.m_Probability == 0 ? 100 : sub.m_Probability;

                if (probability < kMinProbability)
                {
                    continue;
                }

                if (sub.m_GroupIndex != 0 && !groupsTaken.Add(sub.m_GroupIndex))
                {
                    continue;
                }

                if (sub.m_ParentMesh >= 0 && sub.m_ParentMesh < geometry.m_Meshes.Length)
                {
                    var parentMesh = geometry.m_Meshes[sub.m_ParentMesh];

                    if (parentMesh != null && parentMesh.m_RequireState != ObjectState.None && parentMesh.m_RequireState != state)
                    {
                        continue;
                    }
                }

                var childPosition = position + (rotation * new Vector3(sub.m_Position.x, sub.m_Position.y, sub.m_Position.z));
                var childRotation = rotation * new Quaternion(sub.m_Rotation.value.x, sub.m_Rotation.value.y, sub.m_Rotation.value.z, sub.m_Rotation.value.w);
                CollectPlacements(child, child, childPosition, childRotation, depth + 1, output, stats, ownSlots);
            }
        }

        /// <summary>
        /// Lanes of an object (hedges, fences, walls, painted lines): the lane mesh repeated along the
        /// Bézier curve, one copy per mesh length, each stretched so the copies fill the curve exactly.
        /// Lane decals on curved lanes also carry their stretch of the curve (see <see cref="Placement.Curve"/>).
        /// </summary>
        private void CollectLanes(PrefabBase owner, Vector3 position, Quaternion rotation, List<Placement> output, CollectStats stats,
            Color[] ownerSlots)
        {
            if (!owner.TryGet(out ObjectSubLanes subLanes) || subLanes.m_SubLanes == null)
            {
                return;
            }

            const int samples = 48;

            foreach (var lane in subLanes.m_SubLanes)
            {
                PrefabBase prefab = lane?.m_LanePrefab;

                if (prefab == null)
                {
                    if (lane != null)
                    {
                        stats.Missing++;
                    }

                    continue;
                }

                string lower = prefab.name.ToLowerInvariant();
                bool skip = false;

                foreach (var word in kLaneSkip)
                {
                    if (lower.Contains(word))
                    {
                        skip = true;
                        break;
                    }
                }

                // Lanes can be placeholders too (a hedge placeholder swapped for a real hedge).
                if (!skip && prefab.TryGet(out PlaceholderObject _))
                {
                    prefab = ResolvePlaceholderPrefab(prefab, candidate =>
                        candidate is NetLaneGeometryPrefab g && g.m_Meshes != null && g.m_Meshes.Length > 0);
                }

                if (skip || !(prefab is NetLaneGeometryPrefab geometry) || geometry.m_Meshes == null || geometry.m_Meshes.Length == 0)
                {
                    continue;
                }

                var curve = lane.m_BezierCurve;
                var p0 = new Vector3(curve.a.x, curve.a.y, curve.a.z);
                var p1 = new Vector3(curve.b.x, curve.b.y, curve.b.z);
                var p2 = new Vector3(curve.c.x, curve.c.y, curve.c.z);
                var p3 = new Vector3(curve.d.x, curve.d.y, curve.d.z);

                // Underground runs (utilities that slipped past the name filter).
                if ((p0.y + p3.y) * 0.5f < -0.5f)
                {
                    continue;
                }

                if (!prefab.TryGet(out ColorProperties colours))
                {
                    colours = null;
                }

                bool plant = lower.Contains("hedge") || lower.Contains("bush");

                // Arc length, sampled.
                var points = new Vector3[samples + 1];
                var distance = new float[samples + 1];

                for (int i = 0; i <= samples; i++)
                {
                    points[i] = Polygon.Bezier(p0, p1, p2, p3, i / (float)samples);

                    if (i > 0)
                    {
                        distance[i] = distance[i - 1] + Vector3.Distance(points[i - 1], points[i]);
                    }
                }

                float total = distance[samples];

                if (total < 0.1f)
                {
                    continue;
                }

                foreach (var info in geometry.m_Meshes)
                {
                    if (info?.m_Mesh == null || info.m_RequireEditor || info.m_RequireLeftHandTraffic)
                    {
                        continue;
                    }

                    var render = info.m_Mesh;
                    float length;
                    Vector3 centre;

                    try
                    {
                        var bounds = render.bounds;
                        length = bounds.max.z - bounds.min.z;
                        centre = new Vector3((bounds.min.x + bounds.max.x) * 0.5f, 0f, (bounds.min.z + bounds.max.z) * 0.5f);
                    }
                    catch
                    {
                        continue;
                    }

                    if (length < 0.05f)
                    {
                        continue;
                    }

                    int count = Mathf.Clamp(Mathf.RoundToInt(total / length), 1, 400);
                    float stretch = total / count / length;
                    bool laneIsDecal = IsDecalRender(render);

                    // Curved: the arc is measurably longer than its chord, or its middle is off it.
                    var chordMid = (p0 + p3) * 0.5f;
                    bool curved = total - Vector3.Distance(p0, p3) > 0.02f
                                  || Vector3.Distance(new Vector3(chordMid.x, 0f, chordMid.z),
                                      new Vector3(points[samples / 2].x, 0f, points[samples / 2].z)) > 0.05f;

                    for (int k = 0; k < count; k++)
                    {
                        float at = (k + 0.5f) * total / count;
                        int j = 1;

                        while (j < samples && distance[j] < at)
                        {
                            j++;
                        }

                        float f = Mathf.InverseLerp(distance[j - 1], distance[j], at);
                        var point = Vector3.Lerp(points[j - 1], points[j], f);
                        var tangent = points[j] - points[j - 1];
                        tangent.y = 0f;

                        if (tangent.sqrMagnitude < 1e-6f)
                        {
                            tangent = Vector3.forward;
                        }

                        var local = Quaternion.LookRotation(tangent.normalized, Vector3.up);
                        var scale = new Vector3(1f, 1f, stretch);
                        var offset = local * Vector3.Scale(centre, scale);
                        Vector3[] arc = null;

                        if (laneIsDecal && curved)
                        {
                            // One point every ~0.5 m of this copy's stretch of the curve.
                            float from = k * total / count;
                            float span = total / count;
                            int steps = Mathf.Clamp(Mathf.CeilToInt(span / 0.5f), 1, 32);
                            arc = new Vector3[steps + 1];
                            int jj = 1;

                            for (int s = 0; s <= steps; s++)
                            {
                                float d = from + (span * s / steps);

                                while (jj < samples && distance[jj] < d)
                                {
                                    jj++;
                                }

                                float ff = Mathf.InverseLerp(distance[jj - 1], distance[jj], d);
                                arc[s] = position + (rotation * Vector3.Lerp(points[jj - 1], points[jj], ff));
                            }
                        }

                        output.Add(new Placement
                        {
                            Curve = arc,
                            Render = render,
                            Position = position + (rotation * (point - offset)),
                            Rotation = rotation * local,
                            Colours = colours,
                            ParentSlots = ownerSlots,
                            Scale = scale,
                            IsPlant = plant,

                            // Painted lane markings are decals laid along the curve: drawn as paint,
                            // never as their projector box.
                            IsDecal = laneIsDecal,
                        });
                    }
                }
            }
        }

        /// <summary>
        /// The one conditional state to draw: None when any mesh is unconditional, otherwise the
        /// first state of the preference lists the object has, otherwise the state of its first mesh.
        /// </summary>
        private static ObjectState PickState(ObjectGeometryPrefab geometry, bool young)
        {
            var present = new HashSet<ObjectState>();
            var first = ObjectState.None;
            bool any = false;

            foreach (var info in geometry.m_Meshes)
            {
                if (info?.m_Mesh == null)
                {
                    continue;
                }

                if (info.m_RequireState == ObjectState.None)
                {
                    return ObjectState.None;
                }

                if (!any)
                {
                    first = info.m_RequireState;
                    any = true;
                }

                present.Add(info.m_RequireState);
            }

            if (young)
            {
                foreach (var candidate in kYoungPreference)
                {
                    if (present.Contains(candidate))
                    {
                        return candidate;
                    }
                }
            }

            foreach (var candidate in kStatePreference)
            {
                if (present.Contains(candidate))
                {
                    return candidate;
                }
            }

            return first;
        }

        /// <summary>The first drawable, non-placeholder object a placeholder can become; null when none.</summary>
        private ObjectGeometryPrefab ResolvePlaceholder(PrefabBase placeholder)
        {
            return ResolvePlaceholderPrefab(placeholder, candidate =>
                candidate is ObjectGeometryPrefab geometry && geometry.m_Meshes != null
                && geometry.m_Meshes.Length > 0 && !geometry.TryGet(out PlaceholderObject _)) as ObjectGeometryPrefab;
        }

        /// <summary>
        /// The first prefab of a placeholder's <c>PlaceholderObjectElement</c> buffer (filled by the
        /// game on the placeholder's prefab entity) that passes <paramref name="accept"/>. Object and
        /// area placeholders use the same buffer.
        /// </summary>
        private PrefabBase ResolvePlaceholderPrefab(PrefabBase placeholder, Func<PrefabBase, bool> accept)
        {
            try
            {
                var entity = m_PrefabSystem.GetEntity(placeholder);

                if (entity == Entity.Null || !EntityManager.HasComponent<PlaceholderObjectElement>(entity))
                {
                    return null;
                }

                var buffer = EntityManager.GetBuffer<PlaceholderObjectElement>(entity, true);

                for (int i = 0; i < buffer.Length; i++)
                {
                    PrefabBase candidate;

                    try
                    {
                        candidate = m_PrefabSystem.GetPrefab<PrefabBase>(buffer[i].m_Object);
                    }
                    catch
                    {
                        continue;
                    }

                    if (candidate != null && accept(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Placeholder '{placeholder?.name}' not resolved: {ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }

        /// <summary>True for a mesh drawn by a decal shader (DecalProperties, or a decal surface template).</summary>
        private static bool IsDecalRender(RenderPrefab render)
        {
            try
            {
                if (render.TryGet(out DecalProperties _))
                {
                    return true;
                }

                var library = AssetDatabase.global?.resources?.materialLibrary;

                if (library != null && render.surfaceAssets != null)
                {
                    foreach (var surface in render.surfaceAssets)
                    {
                        var template = surface != null ? library.GetMaterial(surface.materialTemplateHash) : null;

                        if (template?.shader != null && template.shader.name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Treated as a regular mesh.
            }

            return false;
        }

        /// <summary>Bounds the icon is framed on: every placement plus the lot, cut at ground level.</summary>
        private static void FrameBounds(List<Placement> placements, PrefabBase prefab, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            foreach (var placement in placements)
            {
                var b = placement.Render.bounds;

                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);
                    var world = placement.Position + (placement.Rotation * Vector3.Scale(corner, placement.Scale));
                    min = Vector3.Min(min, world);
                    max = Vector3.Max(max, world);
                }
            }

            if (TryGetLot(prefab, out int lotWidth, out int lotDepth))
            {
                min = Vector3.Min(min, new Vector3(-lotWidth * 4f, 0f, -lotDepth * 4f));
                max = Vector3.Max(max, new Vector3(lotWidth * 4f, 0f, lotDepth * 4f));
            }

            // Basements are cut at ground level (see AboveGround).
            min.y = Mathf.Max(min.y, 0f);
        }

        /// <summary>
        /// The three colour slots of a colour set (variation 0, the default look), with the channels
        /// the game hands to an external source replaced by that source's colours: the parent object
        /// (fences, walls and awnings take the house's colours) or, for brand-coloured meshes, the
        /// game's default renter brand, which is what the game shows when a building has no renter.
        /// </summary>
        private static Color[] ResolveSlots(ColorProperties colours, Color[] parentSlots)
        {
            if (colours == null)
            {
                return null;
            }

            var slots = new[] { colours.GetColor(0, 0), colours.GetColor(0, 1), colours.GetColor(0, 2) };
            var external = colours.m_ExternalColorSource == ColorSourceType.Parent ? parentSlots : s_BrandColors;

            if (external != null && external.Length > 0 && UsesExternalColours(colours))
            {
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        if (colours.CanBeModifiedByExternal((sbyte)i))
                        {
                            int id = colours.m_ChannelsBinding[i].m_ChannelId;

                            if (id >= 0 && id < external.Length)
                            {
                                slots[i] = external[id];
                            }
                        }
                    }
                    catch
                    {
                        // The channel keeps its own colour.
                    }
                }
            }

            return slots;
        }

        /// <summary>True when some channel of the colour set comes from the parent or from the brand.</summary>
        private static bool UsesExternalColours(ColorProperties colours)
        {
            if (colours == null)
            {
                return false;
            }

            bool parent = colours.m_ExternalColorSource == ColorSourceType.Parent;
            bool brand = colours.m_ExternalColorSource == ColorSourceType.Brand && s_BrandColors != null && s_BrandColors.Length > 0;

            if (!parent && !brand)
            {
                return false;
            }

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (colours.CanBeModifiedByExternal((sbyte)i))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Not modifiable.
                }
            }

            return false;
        }

        /// <summary>Reads the game's default renter brand colours (BuildingConfigurationPrefab.m_DefaultRenterBrand) once.</summary>
        private void LoadDefaultBrand()
        {
            if (m_BrandLoaded)
            {
                return;
            }

            m_BrandLoaded = true;

            try
            {
                var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());

                try
                {
                    if (query.CalculateEntityCount() > 0)
                    {
                        var configuration = m_PrefabSystem.GetPrefab<BuildingConfigurationPrefab>(query.GetSingletonEntity());
                        s_BrandColors = configuration?.m_DefaultRenterBrand?.m_BrandColors;
                    }
                }
                finally
                {
                    query.Dispose();
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Default brand colours not found: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Cache-key text for a set of colour slots (8-bit per channel).</summary>
        private static string SlotsKey(Color[] slots)
        {
            if (slots == null)
            {
                return "-";
            }

            var text = new StringBuilder();

            foreach (var c in slots)
            {
                text.Append(Mathf.RoundToInt(c.r * 255f)).Append(',')
                    .Append(Mathf.RoundToInt(c.g * 255f)).Append(',')
                    .Append(Mathf.RoundToInt(c.b * 255f)).Append(';');
            }

            return text.ToString();
        }
    }
}
