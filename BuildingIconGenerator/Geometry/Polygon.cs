// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Geometry
{
    using System.Collections.Generic;
    using System.Linq;
    using UnityEngine;

    /// <summary>Planar helpers for lot outlines and lane curves.</summary>
    internal static class Polygon
    {
        /// <summary>
        /// Fills a closed outline in the XZ plane with trapezoids, slab by slab between consecutive
        /// node Z values, using the even-odd rule. Exact for any outline - concave, self-touching or
        /// with repeated nodes - where ear clipping leaves holes or over-covers. Appends vertices
        /// (y = 0) and triangles facing +Y; returns the filled area in square metres.
        /// </summary>
        public static float ScanlineFill(Vector3[] outline, List<Vector3> vertices, List<int> triangles)
        {
            int count = outline.Length;
            float area = 0f;

            if (count < 3)
            {
                return 0f;
            }

            var levels = outline.Select(p => p.z).Distinct().OrderBy(z => z).ToList();
            var crossings = new List<(float mid, float lo, float hi)>();

            for (int s = 0; s + 1 < levels.Count; s++)
            {
                float z0 = levels[s];
                float z1 = levels[s + 1];

                if (z1 - z0 < 1e-5f)
                {
                    continue;
                }

                float zm = 0.5f * (z0 + z1);
                crossings.Clear();

                for (int i = 0; i < count; i++)
                {
                    var a = outline[i];
                    var b = outline[(i + 1) % count];

                    if ((a.z <= zm) == (b.z <= zm))
                    {
                        continue;
                    }

                    // The edge spans the whole slab (slab limits are node heights).
                    float t0 = (z0 - a.z) / (b.z - a.z);
                    float t1 = (z1 - a.z) / (b.z - a.z);
                    float tm = (zm - a.z) / (b.z - a.z);
                    crossings.Add((a.x + ((b.x - a.x) * tm), a.x + ((b.x - a.x) * t0), a.x + ((b.x - a.x) * t1)));
                }

                crossings.Sort((p, q) => p.mid.CompareTo(q.mid));

                for (int k = 0; k + 1 < crossings.Count; k += 2)
                {
                    var left = crossings[k];
                    var right = crossings[k + 1];

                    if (right.mid - left.mid < 1e-5f)
                    {
                        continue;
                    }

                    int v = vertices.Count;
                    vertices.Add(new Vector3(left.lo, 0f, z0));
                    vertices.Add(new Vector3(right.lo, 0f, z0));
                    vertices.Add(new Vector3(right.hi, 0f, z1));
                    vertices.Add(new Vector3(left.hi, 0f, z1));
                    triangles.Add(v);
                    triangles.Add(v + 2);
                    triangles.Add(v + 1);
                    triangles.Add(v);
                    triangles.Add(v + 3);
                    triangles.Add(v + 2);
                    area += 0.5f * ((right.lo - left.lo) + (right.hi - left.hi)) * (z1 - z0);
                }
            }

            return area;
        }

        /// <summary>Point of the cubic Bézier curve (a, b, c, d) at parameter t.</summary>
        public static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
        {
            float u = 1f - t;
            return (u * u * u * a) + (3f * u * u * t * b) + (3f * u * t * t * c) + (t * t * t * d);
        }
    }
}
