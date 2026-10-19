// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Geometry
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;
    using UnityEngine.Rendering;

    /// <summary>
    /// Stateless operations on game meshes. Game meshes are never modified: every operation that
    /// changes geometry or attributes works on a copy owned by the caller.
    /// </summary>
    internal static class MeshProcessing
    {
        /// <summary>Share of triangle corners above which a mesh's normals are considered wrong.</summary>
        public const float kMaxDisagreeingNormals = 0.05f;

        /// <summary>Share of triangle corners whose vertex normal points away from its face (dot &lt; 0).</summary>
        public static float DisagreeingNormals(Mesh mesh)
        {
            var vertices = mesh.vertices;
            var normals = mesh.normals;

            if (normals == null || normals.Length != vertices.Length)
            {
                return 1f;
            }

            long corners = 0;
            long bad = 0;

            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var triangles = mesh.GetTriangles(sub);

                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    var a = vertices[triangles[t]];
                    var b = vertices[triangles[t + 1]];
                    var c = vertices[triangles[t + 2]];
                    var face = Vector3.Cross(b - a, c - a);

                    if (face.sqrMagnitude < 1e-12f)
                    {
                        continue;
                    }

                    for (int k = 0; k < 3; k++)
                    {
                        corners++;

                        if (Vector3.Dot(normals[triangles[t + k]], face) < 0f)
                        {
                            bad++;
                        }
                    }
                }
            }

            return corners == 0 ? 0f : (float)bad / corners;
        }

        /// <summary>
        /// Decodes octahedral-packed normals (two components) of <paramref name="source"/> into
        /// <paramref name="copy"/>, trying both axis conventions. Accepted only when at most
        /// <see cref="kMaxDisagreeingNormals"/> of the corners then disagree with their faces; this
        /// keeps the authored smooth shading, where recalculated normals would flatten it.
        /// </summary>
        public static bool TryDecodeOctahedral(Mesh source, Mesh copy)
        {
            var encoded = new List<Vector3>();
            source.GetNormals(encoded);
            var vertices = source.vertices;

            if (encoded.Count != vertices.Length || encoded.Count == 0)
            {
                return false;
            }

            var decoded = new Vector3[encoded.Count];

            // Gives the copy a plain three-component normal attribute to write into.
            copy.RecalculateNormals();

            for (int swap = 0; swap < 2; swap++)
            {
                for (int i = 0; i < encoded.Count; i++)
                {
                    float x = encoded[i].x;
                    float y = encoded[i].y;
                    float z = 1f - Mathf.Abs(x) - Mathf.Abs(y);

                    if (z < 0f)
                    {
                        float ox = x;
                        x = (1f - Mathf.Abs(y)) * (ox >= 0f ? 1f : -1f);
                        y = (1f - Mathf.Abs(ox)) * (y >= 0f ? 1f : -1f);
                    }

                    var n = swap == 0 ? new Vector3(x, y, z) : new Vector3(x, z, y);
                    decoded[i] = n.normalized;
                }

                copy.normals = decoded;

                if (DisagreeingNormals(copy) <= kMaxDisagreeingNormals)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A copy of <paramref name="mesh"/> cut at the ground plane (world y = 0 after
        /// <paramref name="placement"/>): every triangle is clipped against the plane
        /// (Sutherland-Hodgman, winding preserved) and vertex attributes are interpolated along the cut.
        /// </summary>
        public static Mesh ClipAtGround(Mesh mesh, Matrix4x4 placement)
        {
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var colours = mesh.colors32;
            var uv0 = new List<Vector2>();
            var uv1 = new List<Vector2>();
            mesh.GetUVs(0, uv0);
            mesh.GetUVs(1, uv1);

            bool hasNormals = normals != null && normals.Length == vertices.Length;
            bool hasTangents = tangents != null && tangents.Length == vertices.Length;
            bool hasColours = colours != null && colours.Length == vertices.Length;
            bool hasUv0 = uv0.Count == vertices.Length;
            bool hasUv1 = uv1.Count == vertices.Length;

            var height = new float[vertices.Length];

            for (int i = 0; i < vertices.Length; i++)
            {
                height[i] = placement.MultiplyPoint3x4(vertices[i]).y;
            }

            var outVertices = new List<Vector3>();
            var outNormals = new List<Vector3>();
            var outTangents = new List<Vector4>();
            var outColours = new List<Color32>();
            var outUv0 = new List<Vector2>();
            var outUv1 = new List<Vector2>();
            var subMeshes = new List<int>[mesh.subMeshCount];

            // A vertex of the clipped polygon: an original vertex (to = -1) or a point on edge src-to.
            int Emit(int src, int to, float f)
            {
                int index = outVertices.Count;

                if (to < 0)
                {
                    outVertices.Add(vertices[src]);
                    if (hasNormals) outNormals.Add(normals[src]);
                    if (hasTangents) outTangents.Add(tangents[src]);
                    if (hasColours) outColours.Add(colours[src]);
                    if (hasUv0) outUv0.Add(uv0[src]);
                    if (hasUv1) outUv1.Add(uv1[src]);
                }
                else
                {
                    outVertices.Add(Vector3.Lerp(vertices[src], vertices[to], f));
                    if (hasNormals) outNormals.Add(Vector3.Lerp(normals[src], normals[to], f).normalized);
                    if (hasTangents) outTangents.Add(Vector4.Lerp(tangents[src], tangents[to], f));
                    if (hasColours) outColours.Add(Color32.Lerp(colours[src], colours[to], f));
                    if (hasUv0) outUv0.Add(Vector2.Lerp(uv0[src], uv0[to], f));
                    if (hasUv1) outUv1.Add(Vector2.Lerp(uv1[src], uv1[to], f));
                }

                return index;
            }

            var polygon = new List<int>(4);
            var corner = new int[3];

            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                subMeshes[sub] = new List<int>();
                var triangles = mesh.GetTriangles(sub);

                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    corner[0] = triangles[t];
                    corner[1] = triangles[t + 1];
                    corner[2] = triangles[t + 2];

                    if (height[corner[0]] < 0f && height[corner[1]] < 0f && height[corner[2]] < 0f)
                    {
                        continue;
                    }

                    polygon.Clear();

                    for (int k = 0; k < 3; k++)
                    {
                        int a = corner[k];
                        int b = corner[(k + 1) % 3];
                        float ha = height[a];
                        float hb = height[b];

                        if (ha >= 0f)
                        {
                            polygon.Add(Emit(a, -1, 0f));
                        }

                        if ((ha >= 0f) != (hb >= 0f))
                        {
                            polygon.Add(Emit(a, b, ha / (ha - hb)));
                        }
                    }

                    for (int k = 1; k + 1 < polygon.Count; k++)
                    {
                        subMeshes[sub].Add(polygon[0]);
                        subMeshes[sub].Add(polygon[k]);
                        subMeshes[sub].Add(polygon[k + 1]);
                    }
                }
            }

            var result = new Mesh { name = mesh.name + "_AboveGround", hideFlags = HideFlags.HideAndDontSave };
            result.indexFormat = IndexFormat.UInt32;
            result.SetVertices(outVertices);
            if (hasNormals) result.SetNormals(outNormals);
            if (hasTangents) result.SetTangents(outTangents);
            if (hasColours) result.SetColors(outColours);
            if (hasUv0) result.SetUVs(0, outUv0);
            if (hasUv1) result.SetUVs(1, outUv1);
            result.subMeshCount = subMeshes.Length;

            for (int sub = 0; sub < subMeshes.Length; sub++)
            {
                result.SetTriangles(subMeshes[sub], sub);
            }

            if (!hasNormals)
            {
                result.RecalculateNormals();
            }

            result.RecalculateBounds();
            return result;
        }

        /// <summary>
        /// Texel density of a set of meshes: sqrt(UV area / world area) over all triangles, i.e.
        /// atlas texels per metre per unit of atlas size. Returns 0 when it cannot be measured.
        /// </summary>
        public static float TexelDensity(Mesh[] meshes)
        {
            double world = 0;
            double uv = 0;

            foreach (var mesh in meshes)
            {
                if (mesh == null || !mesh.isReadable)
                {
                    continue;
                }

                var v = mesh.vertices;
                var t = mesh.uv;

                if (t == null || t.Length != v.Length)
                {
                    continue;
                }

                var triangles = mesh.triangles;

                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int a = triangles[i];
                    int b = triangles[i + 1];
                    int c = triangles[i + 2];
                    world += Vector3.Cross(v[b] - v[a], v[c] - v[a]).magnitude * 0.5;
                    var e1 = t[b] - t[a];
                    var e2 = t[c] - t[a];
                    uv += Math.Abs((e1.x * e2.y) - (e1.y * e2.x)) * 0.5;
                }
            }

            return world > 1e-3 && uv > 1e-9 ? (float)Math.Sqrt(uv / world) : 0f;
        }
    }
}
