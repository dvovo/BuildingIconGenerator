// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Systems
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using BuildingIconGenerator.Rendering;
    using BuildingIconGenerator.Storage;
    using Colossal.IO.AssetDatabase;
    using Game.Prefabs;
    using UnityEngine;
    using UnityEngine.Rendering;

    /// <content>Building one icon, as a sequence of steps spread across frames.</content>
    public sealed partial class IconGeneratorSystem
    {
        /// <summary>Props smaller than this on the icon (output pixels) are not drawn at all.</summary>
        private const float kCullPixels = 1.0f;

        /// <summary>Smallest texture size read for a prop.</summary>
        private const int kMinPropCapture = 64;

        /// <summary>Largest texture size read for a prop.</summary>
        private const int kMaxPropCapture = 1024;

        /// <summary>Running estimate of one HDRP render pass on the main thread, in milliseconds.</summary>
        private double m_PassMs = 4;

        /// <summary>Texture size the next material build reads its surfaces at (set per placement).</summary>
        private int m_CaptureSize = 256;

        /// <summary>Serial number of the building being captured (cache bookkeeping).</summary>
        private int m_BuildingSerial;

        private static double Ms(long start)
        {
            return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>The capture of one building, or null when it has nothing to draw.</summary>
        private IEnumerator BeginCapture(PrefabBase prefab)
        {
            if (!(prefab is ObjectGeometryPrefab geometry) || geometry.m_Meshes == null)
            {
                return null;
            }

            return CaptureBuilding(prefab, geometry, m_Library);
        }

        /// <summary>
        /// Builds the building off-screen and photographs it. Yields <c>null</c> when the frame
        /// budget is spent and <see cref="s_EndFrame"/> before each GPU pass that does not fit in the
        /// current frame.
        ///
        /// <list type="number">
        /// <item>Collect what the game would place: the building's meshes, its sub-objects (rooftop
        /// machinery, trees, fences...) and lanes, each with the colours of the object it belongs to.</item>
        /// <item>Frame the camera on those bounds first, so every texture is read at the resolution
        /// it is actually seen at, and props smaller than a pixel are skipped.</item>
        /// <item>Queue every texture decode on the worker threads, then build materials and mesh
        /// renderers while the workers decode.</item>
        /// <item>Compose the ground, frame the final scene, queue the next building's textures,
        /// render the two passes and compose the icon asynchronously.</item>
        /// </list>
        /// </summary>
        private IEnumerator CaptureBuilding(PrefabBase prefab, ObjectGeometryPrefab geometry, MaterialLibrary library)
        {
            var placements = new List<Placement>();
            var stats = new CollectStats();
            CollectPlacements(geometry, prefab, Vector3.zero, Quaternion.identity, 0, placements, stats);

            var created = new List<Material>();
            var createdMeshes = new List<Mesh>();
            var obtained = new List<RenderPrefab>();
            var obtainedMeshes = new Dictionary<RenderPrefab, Mesh[]>();
            GameObject root = null;

            try
            {
                root = m_Rig.NewSubject();
                m_Ground = new GroundCompositor();
                LoadDefaultBrand();

                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                int parts = 0;

                // Provisional framing from placements and lot, before any material exists: it gives the
                // scale of the icon (metres per output pixel), hence the texture level of every surface.
                FrameBounds(placements, prefab, out var preMin, out var preMax);
                m_Rig.Frame(preMin, preMax);
                float preMpp = Mathf.Max(1e-4f, m_Rig.MetresPerOutputPixel);

                // Every surface this building needs, queued at once: the workers decode them while the
                // main thread walks the placements (most were already queued while the previous building
                // was on the GPU - see UpcomingSurfaces).
                foreach (var placement in placements)
                {
                    try
                    {
                        float px = ProjectedPixels(placement, preMpp);

                        if (!placement.IsBuilding && !placement.IsDecal && px < kCullPixels)
                        {
                            continue;
                        }

                        int capture;

                        if (placement.IsBuilding)
                        {
                            // Texel density not known yet (first time this building is drawn): measure it
                            // now from its meshes, which are obtained anyway a moment later.
                            if (!FactsIndex.TryGet(TexelDensityKey(placement.Render), out _))
                            {
                                var early = placement.Render.ObtainMeshes();
                                obtained.Add(placement.Render);
                                MeasureTexelDensity(placement.Render, early);
                            }

                            capture = BuildingCapture(placement.Render, preMpp);
                        }
                        else
                        {
                            capture = PropCapture(px);
                        }

                        foreach (var surface in placement.Render.surfaceAssets)
                        {
                            Prefetch(surface, library, capture, true);
                        }
                    }
                    catch
                    {
                        // A placement that cannot be prefetched is loaded synchronously below.
                    }
                }

                foreach (var placement in placements)
                {
                    if (m_FrameWatch.ElapsedMilliseconds > m_FrameBudgetMs)
                    {
                        yield return null;
                    }

                    var render = placement.Render;
                    float pixels = ProjectedPixels(placement, preMpp);

                    if (!placement.IsBuilding && !placement.IsDecal && pixels < kCullPixels)
                    {
                        continue;
                    }

                    // About two texels per output pixel the mesh spans (the render is 2x supersampled).
                    m_CaptureSize = placement.IsBuilding ? BuildingCapture(render, preMpp) : PropCapture(pixels);

                    var meshes = render.ObtainMeshes();
                    obtained.Add(render);

                    if (m_HeldGeometry.TryGetValue(render, out var held))
                    {
                        held.Building = m_BuildingSerial;
                        m_GeometryOrder.Set(render, UsageIndex.Heat(GeometryKey(render)), m_BuildingSerial);
                    }
                    else if (meshes != null && !obtainedMeshes.ContainsKey(render))
                    {
                        obtainedMeshes[render] = meshes;
                    }

                    var materials = ObtainMaterials(placement, library);

                    if (placement.IsDecal)
                    {
                        if (materials.Length > 0
                            && (AddGroundDecal(placement, materials[0], ref min, ref max)
                                || AddDecalPaint(placement, materials[0], root, createdMeshes, ref min, ref max)))
                        {
                            parts++;
                        }

                        continue;
                    }

                    if (meshes == null || meshes.Length == 0 || materials.Length == 0)
                    {
                        continue;
                    }

                    parts += AddMeshParts(placement, meshes, materials, root, createdMeshes);
                    GrowBounds(placement, render.bounds, ref min, ref max);
                }

                if (m_FrameWatch.ElapsedMilliseconds > m_FrameBudgetMs)
                {
                    yield return null;
                }

                // The lot: its surfaces and ground decals. Also widens the frame to the whole parcel.
                parts += BuildGround(prefab, ref min, ref max);

                if (parts == 0)
                {
                    Mod.Log.Info($"'{prefab.name}' has nothing to draw.");
                    yield break;
                }

                min.y = Mathf.Max(min.y, 0f);
                m_Rig.Frame(min, max);
                FinishDecalPaint();
                FinishGround(root, created, createdMeshes);

                string fileName = IconFileName(prefab.name);
                string path = Path.Combine(m_Registry.Folder, fileName);
                Action onWritten = RecordOnWrite(prefab.name, fileName, stats.Missing + CountMissingTextures(obtained));

                // The next building's textures: queued now, decoded by the workers while this
                // building is on the GPU.
                foreach (var upcoming in UpcomingSurfaces())
                {
                    if (m_FrameWatch.ElapsedMilliseconds > m_FrameBudgetMs)
                    {
                        yield return null;
                    }

                    Prefetch(upcoming.surface, library, upcoming.capture, false);
                }

                // Two full HDRP passes (over black and over white): each gets a new frame unless it
                // fits in what is left of the current one.
                if (m_FrameWatch.ElapsedMilliseconds + m_PassMs > m_FrameBudgetMs)
                {
                    yield return s_EndFrame;
                }

                long mark = Stopwatch.GetTimestamp();
                m_Rig.RenderPass(false);
                m_PassMs = (0.8 * m_PassMs) + (0.2 * Ms(mark));

                if (m_FrameWatch.ElapsedMilliseconds + m_PassMs > m_FrameBudgetMs)
                {
                    yield return s_EndFrame;
                }

                m_Rig.RenderPass(true);

                // Composition on the GPU; the readback and the PNG write complete on their own while
                // the next building is being prepared.
                m_Rig.ComposeAsync(path, m_Registry.ThumbnailPathFor(fileName), onWritten);
            }
            finally
            {
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }

                ReleaseObtainedMeshes(obtained, obtainedMeshes);

                foreach (var material in created)
                {
                    if (material != null)
                    {
                        UnityEngine.Object.DestroyImmediate(material);
                    }
                }

                foreach (var mesh in createdMeshes)
                {
                    if (mesh != null)
                    {
                        UnityEngine.Object.DestroyImmediate(mesh);
                    }
                }

                ReleaseGroundTargets();
                m_Ground = null;
                m_GpuSources.Clear();
                m_PendingPaint.Clear();
                m_FixedMeshes.Clear();
                m_ClippedMeshes.Clear();
            }
        }

        /// <summary>Largest side of a placement's bounds, in output pixels at the given scale.</summary>
        private static float ProjectedPixels(Placement placement, float metresPerPixel)
        {
            var b = placement.Render.bounds;
            var extent = Vector3.Scale(new Vector3(b.max.x - b.min.x, b.max.y - b.min.y, b.max.z - b.min.z), placement.Scale);
            return Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z)) / metresPerPixel;
        }

        /// <summary>Texture size for a prop spanning <paramref name="pixels"/> output pixels.</summary>
        private static int PropCapture(float pixels)
        {
            return Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.CeilToInt(pixels * 2f)), kMinPropCapture, kMaxPropCapture);
        }

        /// <summary>
        /// One renderer per mesh of the placement. The material list of a render prefab is indexed
        /// continuously across the sub-meshes of all its meshes (as RenderPrefab.ObtainMesh does),
        /// not per mesh.
        /// </summary>
        private int AddMeshParts(Placement placement, Mesh[] meshes, Material[] materials, GameObject root, List<Mesh> createdMeshes)
        {
            int parts = 0;
            int cursor = 0;
            var transform = Matrix4x4.TRS(placement.Position, placement.Rotation, placement.Scale);

            foreach (var mesh in meshes)
            {
                if (mesh == null)
                {
                    continue;
                }

                int subMeshes = Mathf.Max(1, mesh.subMeshCount);
                var slice = new Material[subMeshes];

                for (int i = 0; i < subMeshes; i++)
                {
                    slice[i] = materials[Mathf.Min(cursor, materials.Length - 1)];
                    cursor++;
                }

                // Foliage keeps its authored (bent) normals.
                var drawn = placement.IsPlant ? mesh : RenderableMesh(mesh);
                drawn = AboveGround(drawn, transform, createdMeshes);

                var part = new GameObject(mesh.name) { hideFlags = HideFlags.HideAndDontSave };
                part.transform.SetParent(root.transform, false);
                part.transform.localPosition = placement.Position;
                part.transform.localRotation = placement.Rotation;
                part.transform.localScale = placement.Scale;
                part.layer = CaptureRig.kLayer;
                part.AddComponent<MeshFilter>().sharedMesh = drawn;

                var renderer = part.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = slice;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.renderingLayerMask = uint.MaxValue;
                parts++;
            }

            return parts;
        }

        /// <summary>Grows the bounds by the eight corners of the placed, rotated mesh bounds.</summary>
        private static void GrowBounds(Placement placement, Colossal.Mathematics.Bounds3 bounds, ref Vector3 min, ref Vector3 max)
        {
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3(
                    (c & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (c & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (c & 4) == 0 ? bounds.min.z : bounds.max.z);
                var world = placement.Position + (placement.Rotation * Vector3.Scale(corner, placement.Scale));
                min = Vector3.Min(min, world);
                max = Vector3.Max(max, world);
            }
        }

        /// <summary>
        /// The registry update to run once the PNG is on disk. An icon drawn with some content
        /// missing (a pack or mod not installed) is recorded against the installed content, so it is
        /// regenerated when that changes - and only then.
        /// </summary>
        private Action RecordOnWrite(string name, string fileName, int missing)
        {
            if (m_Registry == null || !m_Fingerprints.TryGetValue(name, out ulong fingerprint))
            {
                return null;
            }

            var registry = m_Registry;
            ulong recorded = missing > 0 ? fingerprint ^ ContentHash() : fingerprint;
            return () =>
            {
                registry.Record(name, recorded, kRendererVersion, fileName);
                System.Threading.Interlocked.Increment(ref m_RunWritten);
            };
        }

        /// <summary>Surfaces of the obtained render prefabs that referenced textures the database does not know.</summary>
        private int CountMissingTextures(List<RenderPrefab> obtained)
        {
            return obtained.Sum(render =>
            {
                try
                {
                    return render.surfaceAssets.Count(surface => surface != null && m_SurfacesMissingTextures.Contains(surface.name));
                }
                catch
                {
                    return 0;
                }
            });
        }
    }
}
