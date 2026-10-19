// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Rendering
{
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>
    /// Texture-space ground of a lot. The lot surfaces (grass, asphalt, paving... in the game's
    /// renderer-priority order) and every decal lying on the ground (parking bays, lane markings,
    /// arrows) are composed on the GPU into one top-down texture, which then covers a single plane.
    ///
    /// Decals keep their real shapes and soft edges, their own albedo alpha is their opacity, there
    /// is no z-fighting between stacked layers and no atlas is ever read back to the CPU.
    /// </summary>
    internal sealed class GroundCompositor
    {
        private struct Layer
        {
            public List<Vector3> Vertices;
            public List<int> Triangles;
            public Texture Texture;
            public Color Tint;
            public float MetresPerRepeat;
        }

        private struct Decal
        {
            public Texture Atlas;
            public Rect Uv;
            public Vector2 C00;
            public Vector2 C10;
            public Vector2 C11;
            public Vector2 C01;
            public Color Tint;
        }

        private readonly List<Layer> m_Layers = new List<Layer>();
        private readonly List<Decal> m_Decals = new List<Decal>();
        private Vector2 m_Min = new Vector2(float.MaxValue, float.MaxValue);
        private Vector2 m_Max = new Vector2(float.MinValue, float.MinValue);

        /// <summary>
        /// Opacity remap of decal albedo alpha (low, high, decal flag): painted lines (alpha ~0.5)
        /// become solid, dirt and wear (below 0.3) fade out.
        /// </summary>
        private static readonly Vector4 kDecalAlpha = new Vector4(0.30f, 0.45f, 1f, 0f);

        public bool IsEmpty => m_Layers.Count == 0 && m_Decals.Count == 0;

        public Vector2 Min => m_Min;

        public Vector2 Max => m_Max;

        private static bool s_OrientationChecked;
        private static bool s_FlipY;

        /// <summary>
        /// Once per session: draws a white quad over the lower half of a small target and reads it
        /// back. Texture space has v = 0 at the bottom; on graphics APIs where GL lands the quad at
        /// the top, every composition flips its y.
        /// </summary>
        private static void CheckOrientation(Material splat)
        {
            if (s_OrientationChecked)
            {
                return;
            }

            s_OrientationChecked = true;
            var target = RenderTexture.GetTemporary(8, 8, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            Texture2D probe = null;

            try
            {
                RenderTexture.active = target;
                GL.PushMatrix();
                GL.LoadOrtho();
                GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
                splat.SetTexture("_MainTex", Texture2D.whiteTexture);
                splat.SetColor("_Tint", Color.white);
                splat.SetVector("_Alpha", new Vector4(0f, 1f, 0f, 0f));

                if (splat.SetPass(0))
                {
                    GL.Begin(GL.QUADS);
                    GL.TexCoord2(0f, 0f);
                    GL.Vertex3(0f, 0f, 0f);
                    GL.TexCoord2(1f, 0f);
                    GL.Vertex3(1f, 0f, 0f);
                    GL.TexCoord2(1f, 1f);
                    GL.Vertex3(1f, 0.5f, 0f);
                    GL.TexCoord2(0f, 1f);
                    GL.Vertex3(0f, 0.5f, 0f);
                    GL.End();
                }

                GL.PopMatrix();
                probe = new Texture2D(8, 8, TextureFormat.RGBA32, false, true);
                probe.ReadPixels(new Rect(0f, 0f, 8f, 8f), 0, 0);
                probe.Apply(false);
                var bottom = probe.GetPixel(4, 1);
                var top = probe.GetPixel(4, 6);
                s_FlipY = bottom.r < 0.5f && top.r > 0.5f;
            }
            catch
            {
                s_FlipY = false;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);

                if (probe != null)
                {
                    Object.DestroyImmediate(probe);
                }
            }
        }

        private float Y(float worldZ, float depth)
        {
            float y = (worldZ - m_Min.y) / depth;
            return s_FlipY ? 1f - y : y;
        }

        private void Grow(Vector2 p)
        {
            m_Min = Vector2.Min(m_Min, p);
            m_Max = Vector2.Max(m_Max, p);
        }

        /// <summary>A filled surface: triangles in world XZ (y ignored), texture tiled every metresPerRepeat.</summary>
        public void AddLayer(List<Vector3> vertices, List<int> triangles, Texture texture, Color tint, float metresPerRepeat)
        {
            if (vertices == null || triangles == null || triangles.Count < 3)
            {
                return;
            }

            foreach (var v in vertices)
            {
                Grow(new Vector2(v.x, v.z));
            }

            m_Layers.Add(new Layer
            {
                Vertices = vertices,
                Triangles = triangles,
                Texture = texture,
                Tint = tint,
                MetresPerRepeat = Mathf.Max(0.01f, metresPerRepeat),
            });
        }

        /// <summary>
        /// A ground decal: the atlas rectangle uv laid on the world quad whose corners are
        /// (u min, v min), (u max, v min), (u max, v max), (u min, v max), in world XZ.
        /// </summary>
        public void AddDecal(Texture atlas, Rect uv, Vector2 c00, Vector2 c10, Vector2 c11, Vector2 c01, Color tint)
        {
            if (atlas == null)
            {
                return;
            }

            Grow(c00);
            Grow(c10);
            Grow(c11);
            Grow(c01);
            m_Decals.Add(new Decal { Atlas = atlas, Uv = uv, C00 = c00, C10 = c10, C11 = c11, C01 = c01, Tint = tint });
        }

        /// <summary>
        /// Draws everything into a new sRGB render target of about metresPerPixel per texel (capped
        /// at maxSize), mipmapped for the final render. Returns null when there is nothing to draw.
        /// </summary>
        public RenderTexture Bake(Material splat, float metresPerPixel, int maxSize = 4096)
        {
            if (IsEmpty || splat == null)
            {
                return null;
            }

            CheckOrientation(splat);
            float width = Mathf.Max(0.5f, m_Max.x - m_Min.x);
            float depth = Mathf.Max(0.5f, m_Max.y - m_Min.y);
            float mpp = Mathf.Max(0.005f, metresPerPixel);
            float scale = Mathf.Min(1f, maxSize / Mathf.Max(width / mpp, depth / mpp));
            int pw = Mathf.Clamp(Mathf.CeilToInt(width / mpp * scale), 64, maxSize);
            int ph = Mathf.Clamp(Mathf.CeilToInt(depth / mpp * scale), 64, maxSize);

            var target = new RenderTexture(pw, ph, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "BuildingIconGenerator_Ground",
                hideFlags = HideFlags.HideAndDontSave,
                useMipMap = true,
                autoGenerateMips = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };

            target.Create();
            var previous = RenderTexture.active;
            RenderTexture.active = target;
            GL.PushMatrix();

            try
            {
                GL.LoadOrtho();
                GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));

                foreach (var layer in m_Layers)
                {
                    splat.SetTexture("_MainTex", layer.Texture != null ? layer.Texture : Texture2D.whiteTexture);
                    splat.SetColor("_Tint", layer.Tint);
                    splat.SetVector("_Alpha", new Vector4(0f, 1f, 0f, 0f));

                    if (!splat.SetPass(0))
                    {
                        continue;
                    }

                    GL.Begin(GL.TRIANGLES);

                    foreach (int index in layer.Triangles)
                    {
                        var v = layer.Vertices[index];
                        GL.TexCoord2(v.x / layer.MetresPerRepeat, v.z / layer.MetresPerRepeat);
                        GL.Vertex3((v.x - m_Min.x) / width, Y(v.z, depth), 0f);
                    }

                    GL.End();
                }

                // Consecutive decals sharing atlas and tint (the pieces of a curved lane strip, the
                // bays of a car park) go in one batch: one SetPass, one GL.Begin.
                Texture current = null;
                Color currentTint = default;
                bool open = false;

                foreach (var decal in m_Decals)
                {
                    if (!open || decal.Atlas != current || decal.Tint != currentTint)
                    {
                        if (open)
                        {
                            GL.End();
                            open = false;
                        }

                        splat.SetTexture("_MainTex", decal.Atlas);
                        splat.SetColor("_Tint", decal.Tint);
                        splat.SetVector("_Alpha", kDecalAlpha);

                        if (!splat.SetPass(0))
                        {
                            continue;
                        }

                        GL.Begin(GL.QUADS);
                        open = true;
                        current = decal.Atlas;
                        currentTint = decal.Tint;
                    }

                    Corner(decal.Uv.xMin, decal.Uv.yMin, decal.C00, width, depth);
                    Corner(decal.Uv.xMax, decal.Uv.yMin, decal.C10, width, depth);
                    Corner(decal.Uv.xMax, decal.Uv.yMax, decal.C11, width, depth);
                    Corner(decal.Uv.xMin, decal.Uv.yMax, decal.C01, width, depth);
                }

                if (open)
                {
                    GL.End();
                }
            }
            finally
            {
                GL.PopMatrix();
                RenderTexture.active = previous;
            }

            return target;
        }

        private void Corner(float u, float v, Vector2 world, float width, float depth)
        {
            GL.TexCoord2(u, v);
            GL.Vertex3((world.x - m_Min.x) / width, Y(world.y, depth), 0f);
        }
    }
}
