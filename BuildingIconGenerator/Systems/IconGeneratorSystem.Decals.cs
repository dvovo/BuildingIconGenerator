// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Systems
{
    using System;
    using System.Collections.Generic;
    using BuildingIconGenerator.Rendering;
    using Colossal.Mathematics;
    using Game.Prefabs;
    using Unity.Mathematics;
    using UnityEngine;
    using UnityEngine.Rendering;
    using UnityEngine.Rendering.HighDefinition;

    /// <content>
    /// Decals that do not lie on the ground (roof markings, wall signs): drawn as flat geometry
    /// where the decal carries paint. The decal's atlas rectangle is read once on the CPU, its
    /// painted cells are merged into rectangles, and each rectangle is drawn opaque in its own paint
    /// colour (white lines stay white, yellow stay yellow). Ground decals go through the ground
    /// composition instead.
    /// </content>
    public sealed partial class IconGeneratorSystem
    {
        /// <summary>Height of decal paint above the object's ground.</summary>
        private const float kDecalLift = 0.12f;

        /// <summary>Albedo alpha (0-255) from which a decal texel counts as paint: painted lines are ~127, dirt and wear far below.</summary>
        private const int kDecalPaintAlpha = 90;

        /// <summary>Share of a cell's texels that must be paint for the cell to count.</summary>
        private const float kDecalCellCoverage = 0.25f;

        /// <summary>Longest side of the paint grid, in cells.</summary>
        private const int kDecalPaintGrid = 512;

        /// <summary>
        /// Narrowest a painted line may appear in the output image, in pixels. Thinner lines would
        /// break into dashes or vanish; they are widened about their centre line.
        /// </summary>
        private const float kMinPaintPixels = 1.5f;

        /// <summary>Atlas readbacks kept at once.</summary>
        private const int kMaxAtlasReadbacks = 8;

        private readonly Dictionary<string, DecalPaint> m_DecalPaint = new Dictionary<string, DecalPaint>();
        private readonly List<PendingPaint> m_PendingPaint = new List<PendingPaint>();
        private readonly Dictionary<string, Color32[]> m_AtlasPixels = new Dictionary<string, Color32[]>();

        /// <summary>A decal's paint: rectangles plus one colour per paint cluster, shared by every placement of the decal.</summary>
        private sealed class DecalPaint
        {
            /// <summary>Painted rectangles in the decal's local space: (min x, min z, max x, max z), metres.</summary>
            public readonly List<Vector4> Rects = new List<Vector4>();

            /// <summary>Index into <see cref="Colours"/> and <see cref="Materials"/> for each rectangle.</summary>
            public readonly List<int> RectColour = new List<int>();

            public readonly List<Color> Colours = new List<Color>();
            public readonly List<Material> Materials = new List<Material>();
        }

        /// <summary>A paint mesh of the current capture, filled once the camera is framed.</summary>
        private struct PendingPaint
        {
            public Mesh Mesh;
            public DecalPaint Paint;
            public Quaternion Rotation;

            /// <summary>Placement scale (lane decals are stretched along their curve).</summary>
            public Vector3 Scale;
        }

        /// <summary>
        /// Adds a decal as paint geometry. The mesh itself is filled in <see cref="FinishDecalPaint"/>:
        /// line widths depend on the final framing.
        /// </summary>
        private bool AddDecalPaint(Placement placement, Material material, GameObject root, List<Mesh> meshes, ref Vector3 min, ref Vector3 max)
        {
            var render = placement.Render;

            // Lane decals may carry no DecalProperties: the whole texture then.
            var area = new Bounds2(new float2(0f, 0f), new float2(1f, 1f));

            if (render.TryGet(out DecalProperties decal))
            {
                area = decal.m_TextureArea;
            }

            var bounds = render.bounds;

            if (!m_DecalPaint.TryGetValue(render.name, out var paint))
            {
                paint = BuildDecalPaint(material.GetTexture("_BaseColorMap"), area, bounds);
                m_DecalPaint[render.name] = paint;

                foreach (var colour in paint.Colours)
                {
                    var m = new Material(material) { hideFlags = HideFlags.HideAndDontSave };
                    m_RunMaterials.Add(m);
                    m.SetTexture("_BaseColorMap", null);
                    m.SetColor("_BaseColor", colour);

                    try
                    {
                        m.SetFloat("_DoubleSidedEnable", 1f);
                        m.SetFloat("_SurfaceType", 0f);
                        m.SetFloat("_AlphaCutoffEnable", 0f);
                        m.DisableKeyword("_ALPHATEST_ON");
                        m.SetFloat("_Smoothness", 0f);
                        m.SetFloat("_Metallic", 0f);
                        HDMaterial.ValidateMaterial(m);
                    }
                    catch
                    {
                        // Drawn with the copied settings.
                    }

                    paint.Materials.Add(m);
                }
            }

            if (paint.Rects.Count == 0)
            {
                return false;
            }

            var paintMesh = new Mesh { name = "BuildingIconGenerator_DecalPaint", hideFlags = HideFlags.HideAndDontSave };
            meshes.Add(paintMesh);
            var scale = placement.Scale == Vector3.zero ? Vector3.one : placement.Scale;
            m_PendingPaint.Add(new PendingPaint { Mesh = paintMesh, Paint = paint, Rotation = placement.Rotation, Scale = scale });

            var part = new GameObject("BuildingIconGenerator_DecalPaint") { hideFlags = HideFlags.HideAndDontSave };
            part.transform.SetParent(root.transform, false);
            part.transform.localPosition = placement.Position;
            part.transform.localRotation = placement.Rotation;
            part.layer = CaptureRig.kLayer;
            part.AddComponent<MeshFilter>().sharedMesh = paintMesh;
            var renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = paint.Materials.ToArray();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.renderingLayerMask = uint.MaxValue;

            for (int c = 0; c < 4; c++)
            {
                var corner = new Vector3((c & 1) == 0 ? bounds.min.x : bounds.max.x, 0f, (c & 2) == 0 ? bounds.min.z : bounds.max.z);
                var world = placement.Position + (placement.Rotation * Vector3.Scale(corner, scale));
                min = Vector3.Min(min, world);
                max = Vector3.Max(max, world);
            }

            return true;
        }

        /// <summary>
        /// Fills every paint mesh of the capture now that the camera is framed: each rectangle's
        /// narrow side is measured on screen (projected onto the camera plane, so the view's
        /// foreshortening counts) and widened to <see cref="kMinPaintPixels"/> when it falls short.
        /// </summary>
        private void FinishDecalPaint()
        {
            float mpp = m_Rig.MetresPerOutputPixel;
            var right = m_Rig.CameraRight;
            var up = m_Rig.CameraUp;

            foreach (var pending in m_PendingPaint)
            {
                var paint = pending.Paint;
                var vertices = new List<Vector3>();
                var perColour = new List<int>[paint.Colours.Count];

                for (int k = 0; k < perColour.Length; k++)
                {
                    perColour[k] = new List<int>();
                }

                // Screen pixels per local metre along the decal's local X and Z.
                var ax = pending.Rotation * Vector3.right;
                var az = pending.Rotation * Vector3.forward;
                float pxX = mpp > 0f ? new Vector2(Vector3.Dot(ax, right), Vector3.Dot(ax, up)).magnitude / mpp : 0f;
                float pxZ = mpp > 0f ? new Vector2(Vector3.Dot(az, right), Vector3.Dot(az, up)).magnitude / mpp : 0f;

                for (int ri = 0; ri < paint.Rects.Count; ri++)
                {
                    var r = paint.Rects[ri];
                    float x0 = r.x * pending.Scale.x;
                    float z0 = r.y * pending.Scale.z;
                    float x1 = r.z * pending.Scale.x;
                    float z1 = r.w * pending.Scale.z;

                    if (pxX > 0f && (x1 - x0) * pxX < kMinPaintPixels)
                    {
                        float half = 0.5f * kMinPaintPixels / pxX;
                        float c = 0.5f * (x0 + x1);
                        x0 = c - half;
                        x1 = c + half;
                    }

                    if (pxZ > 0f && (z1 - z0) * pxZ < kMinPaintPixels)
                    {
                        float half = 0.5f * kMinPaintPixels / pxZ;
                        float c = 0.5f * (z0 + z1);
                        z0 = c - half;
                        z1 = c + half;
                    }

                    int i = vertices.Count;
                    vertices.Add(new Vector3(x0, kDecalLift, z0));
                    vertices.Add(new Vector3(x1, kDecalLift, z0));
                    vertices.Add(new Vector3(x1, kDecalLift, z1));
                    vertices.Add(new Vector3(x0, kDecalLift, z1));
                    perColour[paint.RectColour[ri]].AddRange(new[] { i, i + 2, i + 1, i, i + 3, i + 2 });
                }

                var mesh = pending.Mesh;
                mesh.indexFormat = IndexFormat.UInt32;
                mesh.SetVertices(vertices);
                mesh.subMeshCount = perColour.Length;

                for (int k = 0; k < perColour.Length; k++)
                {
                    mesh.SetTriangles(perColour[k], k);
                }

                var normals = new Vector3[vertices.Count];

                for (int k = 0; k < normals.Length; k++)
                {
                    normals[k] = Vector3.up;
                }

                mesh.normals = normals;
                mesh.RecalculateBounds();
            }

            m_PendingPaint.Clear();
        }

        /// <summary>
        /// Paint rectangles of a decal: the atlas rectangle sampled on a grid of at most
        /// <see cref="kDecalPaintGrid"/> cells per side, painted cells merged greedily into rectangles
        /// (runs per row, extended downwards while the next row has the same run), each with its
        /// mean paint colour quantised so similar paints share one material.
        /// </summary>
        private DecalPaint BuildDecalPaint(Texture atlas, Bounds2 area, Bounds3 bounds)
        {
            var result = new DecalPaint();

            if (atlas == null)
            {
                return result;
            }

            try
            {
                int size = atlas.width;
                var pixels = AtlasPixels(atlas);

                if (pixels == null || pixels.Length < size * size)
                {
                    return result;
                }

                int x0 = Mathf.Clamp(Mathf.FloorToInt(area.min.x * size), 0, size - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt(area.max.x * size), x0 + 1, size);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(area.min.y * size), 0, size - 1);
                int y1 = Mathf.Clamp(Mathf.CeilToInt(area.max.y * size), y0 + 1, size);
                int w = x1 - x0;
                int h = y1 - y0;
                int step = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(w, h) / (float)kDecalPaintGrid));
                int gw = (w + step - 1) / step;
                int gh = (h + step - 1) / step;

                var painted = new bool[gw * gh];
                var sums = new Vector3[gw * gh];
                var hitsPerCell = new int[gw * gh];

                for (int cy = 0; cy < gh; cy++)
                {
                    for (int cx = 0; cx < gw; cx++)
                    {
                        int hits = 0;
                        int total = 0;
                        var sum = Vector3.zero;

                        for (int y = cy * step; y < Mathf.Min(h, (cy + 1) * step); y++)
                        {
                            for (int x = cx * step; x < Mathf.Min(w, (cx + 1) * step); x++)
                            {
                                var c = pixels[((y0 + y) * size) + x0 + x];
                                total++;

                                if (c.a >= kDecalPaintAlpha)
                                {
                                    hits++;
                                    sum += new Vector3(c.r, c.g, c.b);
                                }
                            }
                        }

                        int index = (cy * gw) + cx;
                        painted[index] = total > 0 && hits >= total * kDecalCellCoverage;
                        sums[index] = sum;
                        hitsPerCell[index] = hits;
                    }
                }

                var used = new bool[gw * gh];
                float sx = (bounds.max.x - bounds.min.x) / w * step;
                float sz = (bounds.max.z - bounds.min.z) / h * step;
                var clusters = new Dictionary<int, int>();

                for (int cy = 0; cy < gh; cy++)
                {
                    for (int cx = 0; cx < gw; cx++)
                    {
                        if (!painted[(cy * gw) + cx] || used[(cy * gw) + cx])
                        {
                            continue;
                        }

                        int ex = cx;

                        while (ex + 1 < gw && painted[(cy * gw) + ex + 1] && !used[(cy * gw) + ex + 1])
                        {
                            ex++;
                        }

                        int ey = cy;

                        while (ey + 1 < gh)
                        {
                            bool full = true;

                            for (int x = cx; x <= ex && full; x++)
                            {
                                full = painted[((ey + 1) * gw) + x] && !used[((ey + 1) * gw) + x];
                            }

                            if (!full)
                            {
                                break;
                            }

                            ey++;
                        }

                        var sum = Vector3.zero;
                        int hits = 0;

                        for (int y = cy; y <= ey; y++)
                        {
                            for (int x = cx; x <= ex; x++)
                            {
                                used[(y * gw) + x] = true;
                                sum += sums[(y * gw) + x];
                                hits += hitsPerCell[(y * gw) + x];
                            }
                        }

                        // Mean paint colour: linear readback -> sRGB-space for SetColor, ground tint applied.
                        var linear = hits > 0 ? sum / (hits * 255f) : Vector3.one;
                        var colour = new Color(linear.x, linear.y, linear.z, 1f).gamma;
                        colour = new Color(Mathf.Clamp01(colour.r * kGroundTint.r), Mathf.Clamp01(colour.g * kGroundTint.g), Mathf.Clamp01(colour.b * kGroundTint.b), 1f);
                        int key = (Mathf.RoundToInt(colour.r * 7f) * 64) + (Mathf.RoundToInt(colour.g * 7f) * 8) + Mathf.RoundToInt(colour.b * 7f);

                        if (!clusters.TryGetValue(key, out int slot))
                        {
                            slot = result.Colours.Count;
                            clusters[key] = slot;
                            result.Colours.Add(colour);
                        }

                        float ax = bounds.min.x + (cx * sx);
                        float bx = Mathf.Min(bounds.max.x, bounds.min.x + ((ex + 1) * sx));
                        float az = bounds.min.z + (cy * sz);
                        float bz = Mathf.Min(bounds.max.z, bounds.min.z + ((ey + 1) * sz));
                        result.Rects.Add(new Vector4(ax, az, bx, bz));
                        result.RectColour.Add(slot);
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Decal paint of '{atlas.name}' failed: {ex.GetType().Name}: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Texels of a decal atlas, read back once and shared by every decal of the run. Keyed by name
        /// and size rather than instance: an atlas evicted and read again is a new object with the same texels.
        /// </summary>
        private Color32[] AtlasPixels(Texture atlas)
        {
            string id = (string.IsNullOrEmpty(atlas.name) ? atlas.GetInstanceID().ToString() : atlas.name) + "|" + atlas.width;

            if (!m_AtlasPixels.TryGetValue(id, out var pixels))
            {
                if (m_AtlasPixels.Count >= kMaxAtlasReadbacks)
                {
                    m_AtlasPixels.Clear();
                }

                pixels = GpuReadback.Read(atlas, atlas.width);
                m_AtlasPixels[id] = pixels;
            }

            return pixels;
        }
    }
}
