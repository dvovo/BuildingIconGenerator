// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Rendering
{
    using System;
    using System.IO;
    using UnityEngine;

    /// <summary>
    /// The mod's own shaders, shipped as a Unity AssetBundle in <c>Bundles/building_icon_shaders</c>
    /// (built from the <c>Shaders</c> Unity project of the repository):
    /// <list type="bullet">
    /// <item><c>Bake</c>: applies a building's colour variation to its albedo (see <see cref="SurfaceBaker"/>);</item>
    /// <item><c>Compose</c>: turns the two background passes of an icon into the final RGBA image
    /// (see <see cref="CaptureRig"/>);</item>
    /// <item><c>GroundSplat</c>: composes the lot's ground and ground decals into one texture
    /// (see <see cref="GroundCompositor"/>).</item>
    /// </list>
    /// All three are required: without them no icon is generated.
    /// </summary>
    internal sealed class ShaderBundle : IDisposable
    {
        public const string FileName = "building_icon_shaders";
        private const string kBakeShader = "Hidden/BuildingIconGenerator/Bake";
        private const string kComposeShader = "Hidden/BuildingIconGenerator/Compose";
        private const string kSplatShader = "Hidden/BuildingIconGenerator/GroundSplat";

        private AssetBundle m_Bundle;

        private ShaderBundle()
        {
        }

        /// <summary>Colour bake material.</summary>
        public Material Bake { get; private set; }

        /// <summary>Icon composition material.</summary>
        public Material Compose { get; private set; }

        /// <summary>Ground composition material.</summary>
        public Material Splat { get; private set; }

        /// <summary>Loads the bundle from the mod folder. Returns null and an explanation on failure.</summary>
        public static ShaderBundle TryLoad(out string error)
        {
            var result = new ShaderBundle();

            try
            {
                string folder = Mod.Folder;
                string path = string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "Bundles", FileName);

                if (path == null || !File.Exists(path))
                {
                    error = $"shader bundle not found ('{path ?? FileName}')";
                    return null;
                }

                result.m_Bundle = AssetBundle.LoadFromFile(path);

                if (result.m_Bundle == null)
                {
                    error = "shader bundle could not be loaded (built with an incompatible Unity version?)";
                    return null;
                }

                Shader bake = null;
                Shader compose = null;
                Shader splat = null;

                foreach (var shader in result.m_Bundle.LoadAllAssets<Shader>())
                {
                    if (shader == null)
                    {
                        continue;
                    }

                    switch (shader.name)
                    {
                        case kBakeShader:
                            bake = shader;
                            break;
                        case kComposeShader:
                            compose = shader;
                            break;
                        case kSplatShader:
                            splat = shader;
                            break;
                    }
                }

                error = Missing(bake, kBakeShader) ?? Missing(compose, kComposeShader) ?? Missing(splat, kSplatShader);

                if (error != null)
                {
                    result.Dispose();
                    return null;
                }

                result.Bake = new Material(bake) { hideFlags = HideFlags.HideAndDontSave };
                result.Compose = new Material(compose) { hideFlags = HideFlags.HideAndDontSave };
                result.Splat = new Material(splat) { hideFlags = HideFlags.HideAndDontSave };
                return result;
            }
            catch (Exception ex)
            {
                error = $"shader bundle failed: {ex.GetType().Name}: {ex.Message}";
                result.Dispose();
                return null;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            foreach (var material in new[] { Bake, Compose, Splat })
            {
                if (material != null)
                {
                    UnityEngine.Object.DestroyImmediate(material);
                }
            }

            Bake = Compose = Splat = null;

            if (m_Bundle != null)
            {
                m_Bundle.Unload(true);
                m_Bundle = null;
            }
        }

        private static string Missing(Shader shader, string name)
        {
            if (shader == null)
            {
                return $"shader '{name}' missing from the bundle";
            }

            return shader.isSupported ? null : $"shader '{name}' not supported by this graphics API ({SystemInfo.graphicsDeviceType})";
        }
    }
}
