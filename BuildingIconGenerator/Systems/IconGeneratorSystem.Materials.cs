// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using BuildingIconGenerator.Rendering;
    using BuildingIconGenerator.Storage;
    using Colossal.IO.AssetDatabase;
    using Game.Prefabs;
    using UnityEngine;
    using UnityEngine.Rendering.HighDefinition;

    /// <content>HDRP materials built from the game's surfaces: textures, colour bake, alpha, special surfaces.</content>
    public sealed partial class IconGeneratorSystem
    {
        /// <summary>
        /// Brightness a fully metallic texel keeps, as a linear factor (about sRGB 0.58): galvanised
        /// metal under a neutral sky. No reflections are rendered, so metal would otherwise read as
        /// either white paint or black.
        /// </summary>
        private const float kMetalTone = 0.30f;

        /// <summary>Mean albedo alpha under which a prop surface is treated as see-through (nets, grilles, railings).</summary>
        private const float kCutOutMeanAlpha = 0.97f;

        private static readonly Color kWindowColour = new Color(0.27f, 0.29f, 0.34f, 1f);
        private static readonly Color kGlassColour = new Color(0.24f, 0.27f, 0.31f, 1f);
        private static readonly Color kWaterColour = new Color(0.16f, 0.42f, 0.52f, 1f);
        private static readonly Color kFoliageTint = new Color(0.62f, 0.95f, 0.38f, 1f);
        private static readonly Color kBarkTint = new Color(0.60f, 0.45f, 0.32f, 1f);
        private static readonly Color kUntexturedFoliage = new Color(0.27f, 0.45f, 0.16f, 1f);
        private static readonly Color kUntexturedBark = new Color(0.30f, 0.22f, 0.15f, 1f);
        private static readonly Color kGravelColour = new Color(0.58f, 0.53f, 0.45f, 1f);
        private static readonly Color kUntexturedColour = new Color(0.70f, 0.70f, 0.68f, 1f);

        private static Shader s_Lit;

        /// <summary>Source textures of the colour bake for each surface of the current capture: (albedo, control, size, mask).</summary>
        private readonly Dictionary<string, (Texture albedo, Texture control, int size, Texture mask)> m_GpuSources =
            new Dictionary<string, (Texture albedo, Texture control, int size, Texture mask)>();

        /// <summary>See-through decision per prop surface, for the run.</summary>
        private readonly Dictionary<string, bool> m_AlphaCut = new Dictionary<string, bool>(StringComparer.Ordinal);

        private static Shader Lit => s_Lit != null ? s_Lit : (s_Lit = Shader.Find("HDRP/Lit"));

        /// <summary>
        /// The materials of a placement, from the run cache when the same render prefab with the same
        /// colours was already built at this texture size or larger (a larger size only means more
        /// texels), otherwise built now and cached.
        /// </summary>
        private Material[] ObtainMaterials(Placement placement, MaterialLibrary library)
        {
            var render = placement.Render;
            var colours = placement.Colours;

            if (colours == null && !render.TryGet(out colours))
            {
                colours = null;
            }

            string heatKey = GeometryKey(render);
            UsageIndex.Note(heatKey);

            string keyBase = render.GetInstanceID() + "|" + (placement.Colours != null ? placement.Colours.GetInstanceID() : 0)
                             + (UsesExternalColours(colours) ? "|p" + SlotsKey(ResolveSlots(colours, placement.ParentSlots)) : string.Empty)
                             + (placement.IsPlant ? "|plant" : string.Empty) + "|";

            for (int size = m_CaptureSize; size <= Math.Max(kMaxPropCapture, kMaxBuildingCapture); size *= 2)
            {
                string cached = keyBase + size;

                if (m_MaterialCache.TryGetValue(cached, out var materials))
                {
                    Touch(cached);
                    return materials;
                }
            }

            string key = keyBase + m_CaptureSize;
            m_BuildingVt.Clear();
            var built = BuildMaterials(render, library, placement.Colours, placement.IsPlant, placement.ParentSlots);
            var meta = new CacheMeta { Stamp = ++m_Tick, VtSurfaces = m_BuildingVt.ToList(), Building = m_BuildingSerial, HeatKey = heatKey };
            m_CacheMeta[key] = meta;
            NoteSurfaces(meta);
            IndexMaterial(key, meta);
            m_MaterialOrder.Set(key, UsageIndex.Heat(heatKey), m_BuildingSerial);
            m_MaterialCache[key] = built;
            return built;
        }

        /// <summary>
        /// One HDRP/Lit material per surface of a render prefab, with the game's textures bound
        /// directly (virtual texturing is never resolved off-screen) and the colour variation baked
        /// into the albedo.
        ///
        /// <para>CS2 packs its mask and normal maps its own way: bound to HDRP/Lit, the mask's red
        /// channel would be read as metallic and render black under an empty sky. Only the albedo is
        /// used; metal is handled in the bake. Window, glass and water shaders carry no albedo that
        /// can be loaded and get representative colours instead. Every material is double-sided with
        /// flipped back-face normals, so faces wound the other way never open holes.</para>
        /// </summary>
        private Material[] BuildMaterials(RenderPrefab render, MaterialLibrary library, ColorProperties colours, bool isPlant, Color[] parentSlots)
        {
            var materials = new List<Material>();
            var lit = Lit;

            if (lit == null)
            {
                return materials.ToArray();
            }

            // The colour set can sit on the render prefab rather than on the object.
            if (colours == null && !render.TryGet(out colours))
            {
                colours = null;
            }

            var resolvedSlots = UsesExternalColours(colours) ? ResolveSlots(colours, parentSlots) : null;

            foreach (var surface in render.surfaceAssets)
            {
                try
                {
                    var template = library?.GetMaterial(surface.materialTemplateHash);
                    string templateShader = template?.shader?.name ?? string.Empty;
                    var material = new Material(lit) { hideFlags = HideFlags.HideAndDontSave };

                    foreach (var entry in LoadSurfaceTextures(surface, library))
                    {
                        if (material.HasProperty(entry.Key))
                        {
                            material.SetTexture(entry.Key, entry.Value);
                        }
                    }

                    // Textures are bound for real: sample them instead of the virtual-texturing
                    // resolve, which never runs off-screen.
                    material.DisableKeyword("ENABLE_VT");

                    bool isWindow = IsWindowSurface(surface, template);
                    bool isWater = !isWindow
                                   && (templateShader.IndexOf("WatShader", StringComparison.OrdinalIgnoreCase) >= 0
                                       || templateShader.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0
                                       || (surface.name ?? string.Empty).IndexOf("_Wat_", StringComparison.Ordinal) >= 0);
                    bool isGlass = !isWindow && !isWater && templateShader.IndexOf("GlsShader", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isVegetation = templateShader.IndexOf("Veg", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (isWindow)
                    {
                        // The game's own window tint when the template has a plausible one.
                        var windowColour = kWindowColour;

                        if (template != null && template.HasProperty("_BaseColor"))
                        {
                            var own = template.GetColor("_BaseColor");

                            if (own.a > 0.5f && own.maxColorComponent > 0.05f && own.maxColorComponent < 0.9f)
                            {
                                windowColour = new Color(own.r, own.g, own.b, 1f);
                            }
                        }

                        material.SetColor("_BaseColor", windowColour);
                    }
                    else if (isGlass)
                    {
                        material.SetColor("_BaseColor", kGlassColour);
                    }
                    else if (isWater)
                    {
                        material.SetColor("_BaseColor", kWaterColour);
                    }

                    // Plain (non-VT) surfaces - props, trees, bushes - list their textures in their
                    // properties: bind the albedo and give the bake its inputs.
                    if (!isWindow && !surface.isVTMaterial)
                    {
                        var plain = LoadNonVtTextures(surface);

                        if (plain.TryGetValue("_BaseColorMap", out var albedo) && material.HasProperty("_BaseColorMap"))
                        {
                            material.SetTexture("_BaseColorMap", albedo);
                        }

                        if (plain.Count > 0)
                        {
                            RecordBakeSources(surface, plain, m_CaptureSize);
                        }
                    }

                    // VT materials without a VT surface asset (most props): their textures come
                    // from the per-texture route.
                    if (!surface.hasVTSurfaceAsset)
                    {
                        var perTexture = LoadSurfaceTextures(surface, library);

                        if (perTexture.Count > 0)
                        {
                            RecordBakeSources(surface, perTexture, m_CaptureSize);
                        }
                    }

                    // Decals keep their atlas untouched: their paint is read from it.
                    if (templateShader.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        ApplyBakedAlbedo(surface, material, colours, template, resolvedSlots, library);
                    }

                    // Plants: leaf and bark textures are grey (the game's vegetation shader colours
                    // them) - a strong tint keeps them a mid green or brown. Bark is the "Root" shader.
                    if (isPlant || isVegetation)
                    {
                        bool bark = templateShader.IndexOf("Root", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool textured = material.GetTexture("_BaseColorMap") != null;
                        material.SetColor("_BaseColor", textured ? (bark ? kBarkTint : kFoliageTint) : (bark ? kUntexturedBark : kUntexturedFoliage));
                    }

                    material.SetTexture("_MaskMap", null);
                    material.SetTexture("_NormalMap", null);
                    material.DisableKeyword("_MASKMAP");
                    material.DisableKeyword("_NORMALMAP");
                    material.SetFloat("_Metallic", 0f);

                    // Low gloss: broad roofs and walls would otherwise catch the key light as a sheen.
                    material.SetFloat("_Smoothness", isWindow || isGlass ? 0.85f : (isWater ? 0.9f : 0.15f));

                    ApplyAlpha(material, surface, template, isPlant || isVegetation, isWindow || isGlass || isWater);

                    // Double-sided everywhere, back-face normals flipped.
                    material.SetFloat("_DoubleSidedEnable", 1f);

                    if (material.HasProperty("_DoubleSidedNormalMode"))
                    {
                        material.SetFloat("_DoubleSidedNormalMode", 0f);
                    }

                    try
                    {
                        HDMaterial.ValidateMaterial(material);
                    }
                    catch (Exception ex)
                    {
                        Mod.Log.Warn($"Material validation failed for '{surface.name}': {ex.GetType().Name}: {ex.Message}");
                    }

                    if (!isWindow && !isWater && !isGlass)
                    {
                        try
                        {
                            ColourUntextured(material, template, templateShader);
                        }
                        catch
                        {
                            // Keeps the material as it is; the slot must not be lost.
                        }
                    }

                    m_RunMaterials.Add(material);
                    materials.Add(material);
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn($"Material for '{surface?.name}' failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return materials.ToArray();
        }

        /// <summary>
        /// Alpha handling. Clipping only where the game clips - vegetation and surfaces whose
        /// template or own values enable alpha testing - because building atlases carry other data in
        /// alpha. Props measured as see-through (nets, chain-link, grilles) are alpha-blended instead:
        /// their strands are thinner than a texel at icon size, so the reduced alpha is coverage and
        /// a clip would keep them as solid sheets. Asset-clipped props keep the clip: blending thin
        /// double-sided panels without depth write lets the panels behind show through.
        /// </summary>
        private void ApplyAlpha(Material material, SurfaceAsset surface, Material template, bool vegetation, bool special)
        {
            bool assetClips = WantsAlphaTest(template, surface);
            bool cutByAlpha = !vegetation && !assetClips && !special && CutOutByAlpha(surface, material);
            bool clip = vegetation || assetClips || cutByAlpha;
            bool blend = cutByAlpha && surface != null && !surface.hasVTSurfaceAsset;

            material.SetFloat("_AlphaCutoffEnable", clip ? 1f : 0f);
            material.SetFloat("_AlphaCutoff", blend ? 0.04f : 0.5f);

            if (blend)
            {
                // HDRP/Lit transparent, alpha blend; ValidateMaterial derives keywords, queue and depth state.
                material.SetFloat("_SurfaceType", 1f);
                material.SetFloat("_BlendMode", 0f);
                material.SetFloat("_EnableBlendModePreserveSpecularLighting", 0f);
                material.SetFloat("_EnableFogOnTransparent", 0f);
                material.SetFloat("_TransparentDepthPrepassEnable", 0f);
                material.SetFloat("_TransparentWritingMotionVec", 0f);
                material.SetFloat("_TransparentZWrite", 1f);
            }

            if (clip)
            {
                material.EnableKeyword("_ALPHATEST_ON");
            }
            else
            {
                material.DisableKeyword("_ALPHATEST_ON");
            }
        }

        /// <summary>
        /// A surface left without any albedo would render HDRP/Lit's default white. Gravel patches get
        /// a gravel colour (the game samples terrain textures there), others the template's own base
        /// colour when it has one, otherwise a light grey that sits with the scene.
        /// </summary>
        private static void ColourUntextured(Material material, Material template, string templateShader)
        {
            if (!material.HasProperty("_BaseColorMap") || material.GetTexture("_BaseColorMap") != null)
            {
                return;
            }

            var current = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.white;

            if (current.r <= 0.97f || current.g <= 0.97f || current.b <= 0.97f)
            {
                return;
            }

            Color colour;

            if (templateShader.IndexOf("GraShader", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                colour = kGravelColour;
            }
            else if (template != null && template.HasProperty("_BaseColor")
                     && template.GetColor("_BaseColor") is Color own
                     && own.a > 0.5f && (own.r < 0.97f || own.g < 0.97f || own.b < 0.97f))
            {
                colour = new Color(own.r, own.g, own.b, 1f);
            }
            else
            {
                colour = kUntexturedColour;
            }

            material.SetColor("_BaseColor", colour);
        }

        /// <summary>Remembers the textures the colour bake of a surface reads (albedo required).</summary>
        private void RecordBakeSources(SurfaceAsset surface, Dictionary<string, Texture> textures, int size)
        {
            if (textures.TryGetValue("_BaseColorMap", out var albedo) && albedo != null)
            {
                textures.TryGetValue("_ControlMask", out var control);
                textures.TryGetValue("_MaskMap", out var mask);
                m_GpuSources[surface.name] = (albedo, control, size, mask);
            }
        }

        /// <summary>
        /// Bakes the surface's colour variation into its albedo on the GPU and binds the result. The
        /// slots come from the resolved parent/brand colours, else the object's colour set, else the
        /// template material's default slots (satellite dishes, many signs carry no colour set).
        /// </summary>
        private void ApplyBakedAlbedo(SurfaceAsset surface, Material material, ColorProperties colours, Material template,
            Color[] resolvedSlots, MaterialLibrary library)
        {
            if (surface == null)
            {
                return;
            }

            if (!m_GpuSources.ContainsKey(surface.name))
            {
                try
                {
                    // The textures the material got (fast read, one level); the full-resolution
                    // VT surface load only when that route had nothing.
                    var textures = LoadSurfaceTextures(surface, library);

                    if (textures.ContainsKey("_BaseColorMap"))
                    {
                        RecordBakeSources(surface, textures, m_CaptureSize);
                    }
                    else if (surface.hasVTSurfaceAsset && surface.vtSurfaceAsset != null)
                    {
                        var source = LoadVt(surface);

                        if (source != null)
                        {
                            RecordBakeSources(surface, BoundTextures(source, "_BaseColorMap", "_ControlMask", "_MaskMap"), m_CaptureSize);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn($"Bake inputs of '{surface.name}' failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Color[] slots = null;

            if (resolvedSlots != null)
            {
                slots = resolvedSlots;
            }
            else if (colours != null)
            {
                slots = new[] { colours.GetColor(0, 0), colours.GetColor(0, 1), colours.GetColor(0, 2) };
            }
            else if (template != null && template.HasProperty("colossal_ColorMask0"))
            {
                slots = new[] { template.GetColor("colossal_ColorMask0"), template.GetColor("colossal_ColorMask1"), template.GetColor("colossal_ColorMask2") };
            }

            if (slots == null || !m_GpuSources.TryGetValue(surface.name, out var sources))
            {
                return;
            }

            try
            {
                // Metalness: CS2's mask packs it in R, scaled by the template's _Metallic. Rock and
                // stone are never metal whatever their mask says.
                float metallic = template != null && template.HasProperty("_Metallic") ? Mathf.Clamp01(template.GetFloat("_Metallic")) : 0f;
                string name = surface.name ?? string.Empty;

                if (name.IndexOf("Rock", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Stone", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    metallic = 0f;
                }

                var baked = m_Baker.Bake(sources.albedo, sources.control, slots[0].linear, slots[1].linear, slots[2].linear, sources.size,
                    sources.mask, metallic, kMetalTone);
                material.SetTexture("_BaseColorMap", baked);
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Colour bake of '{surface.name}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Does the game alpha-test this surface? Template keyword or flag, or the surface's own values.</summary>
        private bool WantsAlphaTest(Material template, SurfaceAsset surface)
        {
            try
            {
                if (template != null)
                {
                    if (template.IsKeywordEnabled("_ALPHATEST_ON"))
                    {
                        return true;
                    }

                    foreach (var name in new[] { "_AlphaCutoffEnable", "_AlphaClip", "_AlphaTest" })
                    {
                        if (template.HasProperty(name) && template.GetFloat(name) > 0.5f)
                        {
                            return true;
                        }
                    }
                }

                if (surface != null && m_NonVtProps.TryGetValue(surface.name, out var props))
                {
                    if (props.Keywords.Contains("_ALPHATEST_ON"))
                    {
                        return true;
                    }

                    if (props.Floats.TryGetValue("_AlphaCutoffEnable", out float enabled) && enabled > 0.5f)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // No evidence of alpha testing.
            }

            return false;
        }

        /// <summary>
        /// Props whose albedo alpha is their opacity (nets, grilles, railings) while their template
        /// does not switch alpha testing on. Only surfaces without a VT surface asset: building
        /// atlases carry other data in alpha. Decided by the mean albedo alpha, measured once per
        /// surface on the GPU and kept in the facts index; an unmeasurable surface (no texture yet)
        /// is not remembered, so the next build tries again.
        /// </summary>
        private bool CutOutByAlpha(SurfaceAsset surface, Material material)
        {
            if (surface == null || surface.hasVTSurfaceAsset)
            {
                return false;
            }

            if (m_AlphaCut.TryGetValue(surface.name, out bool known))
            {
                return known;
            }

            bool measured = false;
            bool cut = false;

            try
            {
                string factKey = "alpha:" + surface.name + SurfaceStamp(surface);

                if (!FactsIndex.TryGet(factKey, out float alpha))
                {
                    alpha = -1f;
                }

                // The albedo the bake read, else the texture the material ended up with (a baked
                // albedo keeps the source alpha).
                Texture source = null;

                if (m_GpuSources.TryGetValue(surface.name, out var sources) && sources.albedo != null)
                {
                    source = sources.albedo;
                }
                else if (material != null && material.HasProperty("_BaseColorMap"))
                {
                    source = material.GetTexture("_BaseColorMap");
                }

                if (alpha >= 0f)
                {
                    measured = true;
                }
                else if (source != null)
                {
                    var pixels = GpuReadback.Read(source, 4);
                    alpha = 0f;

                    foreach (var c in pixels)
                    {
                        alpha += c.a;
                    }

                    alpha /= pixels.Length * 255f;
                    FactsIndex.Put(factKey, alpha);
                    measured = true;
                }

                if (alpha >= 0f)
                {
                    cut = alpha < kCutOutMeanAlpha && alpha > 0.02f;
                }
            }
            catch
            {
                // Unmeasurable: opaque.
            }

            if (measured)
            {
                m_AlphaCut[surface.name] = cut;
            }

            return cut;
        }

        /// <summary>A window surface: no VT surface asset and drawn by the game with its window shader.</summary>
        private static bool IsWindowSurface(SurfaceAsset surface, Material template)
        {
            try
            {
                if (surface == null || surface.hasVTSurfaceAsset)
                {
                    return false;
                }

                string shader = template != null && template.shader != null ? template.shader.name : null;
                return shader != null && shader.IndexOf("Win", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
