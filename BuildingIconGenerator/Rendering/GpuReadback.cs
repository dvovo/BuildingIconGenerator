// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Rendering
{
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>
    /// Synchronous GPU -> CPU reads of small, reduced copies of a texture. Each call stalls the main
    /// thread until the GPU has drained its queue, so callers cache what they measure (see
    /// <see cref="Storage.FactsIndex"/>) and only read what they need.
    /// </summary>
    internal static class GpuReadback
    {
        /// <summary>
        /// The texture reduced on the GPU to <paramref name="size"/> x <paramref name="size"/> by
        /// successive 2x2 halvings (half-float intermediates, so dark linear values keep their
        /// precision), then read back. Values are LINEAR: the targets are linear, so an sRGB source
        /// is decoded by the sampler and written back as is.
        /// </summary>
        public static Color32[] Read(Texture texture, int size)
        {
            var temporaries = new List<RenderTexture>();
            RenderTexture previous = RenderTexture.active;
            Texture2D readable = null;

            try
            {
                Texture current = texture;
                int w = texture.width;
                int h = texture.height;

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

                var final = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                final.filterMode = FilterMode.Bilinear;
                Graphics.Blit(current, final);
                temporaries.Add(final);
                RenderTexture.active = final;

                readable = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                readable.ReadPixels(new Rect(0f, 0f, size, size), 0, 0);
                readable.Apply(false);
                return readable.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;

                foreach (var temporary in temporaries)
                {
                    RenderTexture.ReleaseTemporary(temporary);
                }

                if (readable != null)
                {
                    Object.DestroyImmediate(readable);
                }
            }
        }
    }
}
