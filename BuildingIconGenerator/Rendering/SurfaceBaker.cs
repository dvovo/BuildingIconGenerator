// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Rendering
{
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>
    /// Bakes a surface's colour variation on the GPU, entirely without CPU readbacks:
    ///
    /// <code>
    /// rgb = albedo.rgb · Π_k lerp(1, slot_k, control_k) · lerp(1, metalTone, mask.r · metallic)
    /// a   = albedo.a
    /// </code>
    ///
    /// Sources are first reduced to the requested size by successive 2x2 halvings (a box filter),
    /// then one blit with the bake shader writes a linear, mipmapped render texture that is bound
    /// directly to the material. Baked textures live until they are released individually (cache
    /// eviction) or all together (end of a run).
    /// </summary>
    internal sealed class SurfaceBaker
    {
        private readonly Material m_Material;
        private readonly HashSet<RenderTexture> m_Baked = new HashSet<RenderTexture>();

        public SurfaceBaker(Material bakeMaterial)
        {
            m_Material = bakeMaterial;
        }

        /// <summary>GPU memory held by baked textures, in bytes (mips included).</summary>
        public long BakedBytes { get; private set; }

        /// <summary>
        /// Bakes <paramref name="albedo"/> with the colour slots <paramref name="c0"/>..<paramref name="c2"/>
        /// (linear) weighted by the RGB channels of <paramref name="control"/>, at size x size.
        /// When <paramref name="mask"/> is given and <paramref name="metallic"/> &gt; 0, texels marked
        /// as metal keep <paramref name="metalTone"/> of their brightness: metal under a neutral sky
        /// rather than white paint, since no reflections are rendered.
        /// </summary>
        public RenderTexture Bake(Texture albedo, Texture control, Color c0, Color c1, Color c2, int size,
            Texture mask, float metallic, float metalTone)
        {
            var temporaries = new List<RenderTexture>();

            try
            {
                var source = Reduce(albedo, size, temporaries);
                var weights = control != null ? Reduce(control, size, temporaries) : null;
                var metal = mask != null && metallic > 0f ? Reduce(mask, size, temporaries) : null;

                var result = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    name = "BuildingIconGenerator_Bake",
                    hideFlags = HideFlags.HideAndDontSave,
                    useMipMap = true,
                    autoGenerateMips = true,
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Trilinear,
                };
                result.Create();

                m_Material.SetTexture("_ControlMask", weights != null ? (Texture)weights : Texture2D.blackTexture);
                m_Material.SetVector("_Colour0", c0);
                m_Material.SetVector("_Colour1", c1);
                m_Material.SetVector("_Colour2", c2);
                m_Material.SetTexture("_MaskMap", metal != null ? metal : (Texture)Texture2D.blackTexture);
                m_Material.SetVector("_Metal", new Vector4(metal != null ? metallic : 0f, metalTone, 0f, 0f));
                Graphics.Blit(source, result, m_Material);

                m_Baked.Add(result);
                BakedBytes += Bytes(result);
                return result;
            }
            finally
            {
                foreach (var temporary in temporaries)
                {
                    RenderTexture.ReleaseTemporary(temporary);
                }
            }
        }

        /// <summary>Frees one baked texture. False when the texture was not produced by this baker.</summary>
        public bool Release(Texture texture)
        {
            if (texture is RenderTexture target && m_Baked.Remove(target))
            {
                BakedBytes -= Bytes(target);
                target.Release();
                Object.DestroyImmediate(target);
                return true;
            }

            return false;
        }

        /// <summary>Frees every baked texture.</summary>
        public void ReleaseAll()
        {
            foreach (var texture in m_Baked)
            {
                if (texture != null)
                {
                    texture.Release();
                    Object.DestroyImmediate(texture);
                }
            }

            m_Baked.Clear();
            BakedBytes = 0;
        }

        /// <summary>Successive 2x2 halvings on the GPU, then one bilinear step to the exact size.</summary>
        private static Texture Reduce(Texture source, int size, List<RenderTexture> temporaries)
        {
            Texture current = source;
            int w = source.width;
            int h = source.height;

            while (w / 2 >= size && h / 2 >= size)
            {
                w /= 2;
                h /= 2;
                var half = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                half.filterMode = FilterMode.Bilinear;
                Graphics.Blit(current, half);
                temporaries.Add(half);
                current = half;
            }

            if (w == size && h == size)
            {
                return current;
            }

            var final = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            final.filterMode = FilterMode.Bilinear;
            Graphics.Blit(current, final);
            temporaries.Add(final);
            return final;
        }

        private static long Bytes(RenderTexture texture)
        {
            return (long)texture.width * texture.height * 4 * 4 / 3;
        }
    }
}
