// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Systems
{
    using System;
    using System.Collections.Generic;
    using BuildingIconGenerator.Geometry;
    using BuildingIconGenerator.Storage;
    using Game.Prefabs;
    using UnityEngine;
    using UnityEngine.Rendering;

    /// <content>Mesh preparation (normals, basements) and the texture size of a building's own surfaces.</content>
    public sealed partial class IconGeneratorSystem
    {
        /// <summary>Largest texture size read for a building's own surfaces.</summary>
        private const int kMaxBuildingCapture = 2048;

        /// <summary>Smallest texture size read for a building's own surfaces.</summary>
        private const int kMinBuildingCapture = 512;

        /// <summary>Atlas texels per rendered pixel a building's own surfaces get (1 = full detail).</summary>
        private const float kBuildingTexelsPerPixel = 1f;

        /// <summary>Repaired mesh copies kept across buildings before the set is flushed.</summary>
        private const int kMaxRepairedMeshes = 1500;

        /// <summary>Game mesh -> mesh to draw, for the current capture.</summary>
        private readonly Dictionary<Mesh, Mesh> m_FixedMeshes = new Dictionary<Mesh, Mesh>();

        /// <summary>Repaired copies by mesh name and vertex count, reused across buildings.</summary>
        private readonly Dictionary<string, Mesh> m_RunFixed = new Dictionary<string, Mesh>();

        /// <summary>Every repaired copy created during the run (destroyed when it ends).</summary>
        private readonly List<Mesh> m_RunMeshes = new List<Mesh>();

        /// <summary>(Game mesh, placement) -> mesh cut at ground level, for the current capture.</summary>
        private readonly Dictionary<(Mesh, Matrix4x4), Mesh> m_ClippedMeshes = new Dictionary<(Mesh, Matrix4x4), Mesh>();

        private string TexelDensityKey(RenderPrefab render)
        {
            if (!m_RenderStamps.TryGetValue(render, out string stamp))
            {
                var text = new System.Text.StringBuilder(64);
                AppendStamp(text, render.geometryAsset);
                stamp = text.ToString();
                m_RenderStamps[render] = stamp;
            }

            // The model's content stamp keeps a measure from outliving an update of the model.
            return "uvd:" + render.name + stamp;
        }

        /// <summary>
        /// The mesh to draw for a game mesh: the mesh itself, or a copy with usable normals when its
        /// own cannot shade correctly under HDRP/Lit - a normal attribute packed in fewer than three
        /// components, or vertex normals that disagree with their faces on more than 5% of the
        /// triangle corners (dark or light wedges on flat walls). Packed normals are decoded as
        /// octahedral when that agrees with the geometry, which keeps the authored smooth shading;
        /// otherwise they are recalculated.
        /// </summary>
        private Mesh RenderableMesh(Mesh mesh)
        {
            if (m_FixedMeshes.TryGetValue(mesh, out var known))
            {
                return known;
            }

            string runKey = mesh.name + "|" + mesh.vertexCount;

            if (m_RunFixed.TryGetValue(runKey, out var reused) && reused != null)
            {
                m_FixedMeshes[mesh] = reused;
                return reused;
            }

            var result = mesh;

            try
            {
                bool has = mesh.HasVertexAttribute(VertexAttribute.Normal);
                int dimension = has ? mesh.GetVertexAttributeDimension(VertexAttribute.Normal) : 0;
                bool packed = !has || dimension < 3;

                if (mesh.isReadable)
                {
                    float disagree = packed ? -1f : MeshProcessing.DisagreeingNormals(mesh);
                    bool inconsistent = disagree > MeshProcessing.kMaxDisagreeingNormals && disagree < 0.95f;

                    if (packed || inconsistent)
                    {
                        var copy = UnityEngine.Object.Instantiate(mesh);
                        copy.name = mesh.name + "_Normals";
                        copy.hideFlags = HideFlags.HideAndDontSave;

                        if (!(packed && dimension == 2 && MeshProcessing.TryDecodeOctahedral(mesh, copy)))
                        {
                            copy.RecalculateNormals();
                        }

                        m_RunMeshes.Add(copy);
                        m_RunFixed[runKey] = copy;
                        result = copy;
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Normals of '{mesh?.name}' not checked: {ex.Message}");
            }

            m_FixedMeshes[mesh] = result;
            return result;
        }

        /// <summary>
        /// Basements, underground car parks and ramps: in the game the terrain hides everything below
        /// ground, here there is only a flat lot. A mesh reaching more than 5 cm below ground (once
        /// placed) is replaced by a copy cut at y = 0; a mesh entirely underground by an empty one.
        /// Meshes above ground are returned untouched.
        /// </summary>
        private Mesh AboveGround(Mesh mesh, Matrix4x4 placement, List<Mesh> created)
        {
            if (mesh == null)
            {
                return null;
            }

            var b = mesh.bounds;
            float lowest = float.MaxValue;
            float highest = float.MinValue;

            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);
                float y = placement.MultiplyPoint3x4(corner).y;
                lowest = Mathf.Min(lowest, y);
                highest = Mathf.Max(highest, y);
            }

            if (lowest > -0.05f)
            {
                return mesh;
            }

            if (m_ClippedMeshes.TryGetValue((mesh, placement), out var known))
            {
                return known;
            }

            var result = mesh;

            try
            {
                // A mesh that is not CPU-readable cannot be cut and is drawn as is.
                if (!mesh.isReadable)
                {
                    result = mesh;
                }
                else if (highest <= 0f)
                {
                    result = new Mesh { name = mesh.name + "_Underground", hideFlags = HideFlags.HideAndDontSave };
                    result.subMeshCount = mesh.subMeshCount;
                    created.Add(result);
                }
                else
                {
                    result = MeshProcessing.ClipAtGround(mesh, placement);
                    created.Add(result);
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"'{mesh.name}' not cut at ground level: {ex.Message}");
                result = mesh;
            }

            m_ClippedMeshes[(mesh, placement)] = result;
            return result;
        }

        /// <summary>
        /// Texture size a building's own surfaces need in this icon: about one atlas texel per
        /// rendered pixel (the render is 2x the output), from the building's texel density - kept in
        /// the facts index - and the frame's metres per output pixel. Rounded to a power of two a
        /// quarter octave towards the larger size and clamped to [512, 2048]: a small house framed
        /// close reads 2048, a large block framed from afar 1024 or 512, decoding 4-16x fewer texels.
        /// </summary>
        private int BuildingCapture(RenderPrefab render, float metresPerOutputPixel)
        {
            int size = kMaxBuildingCapture;

            if (render != null && metresPerOutputPixel > 0f && FactsIndex.TryGet(TexelDensityKey(render), out float density) && density > 1e-6f)
            {
                float needed = 2f / (metresPerOutputPixel * density) * kBuildingTexelsPerPixel;
                int exponent = Mathf.CeilToInt(Mathf.Log(Mathf.Max(needed, 1f), 2f) - 0.25f);
                size = Mathf.Clamp(1 << Mathf.Clamp(exponent, 0, 14), kMinBuildingCapture, kMaxBuildingCapture);
            }

            return size;
        }

        /// <summary>Measures a building mesh's texel density once and records it in the facts index (0 when unmeasurable).</summary>
        private void MeasureTexelDensity(RenderPrefab render, Mesh[] meshes)
        {
            if (render == null || meshes == null)
            {
                return;
            }

            try
            {
                FactsIndex.Put(TexelDensityKey(render), MeshProcessing.TexelDensity(meshes));
            }
            catch
            {
                // Measured again the next time the building is drawn.
            }
        }
    }
}
