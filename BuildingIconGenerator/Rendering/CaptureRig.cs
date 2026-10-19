// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Rendering
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using UnityEngine;
    using UnityEngine.Experimental.Rendering;
    using UnityEngine.Rendering;
    using UnityEngine.Rendering.HighDefinition;

    /// <summary>
    /// Off-screen photo studio: a private orthographic camera, four directional lights and a
    /// fixed-exposure volume, all on a layer nothing else in the game uses, rendering into a render
    /// texture that never reaches the screen.
    ///
    /// <list type="bullet">
    /// <item>The rig stays inactive and is switched on only for each <c>Camera.Render()</c> call, so
    /// nothing it owns can leak into the live scene.</item>
    /// <item>HDRP's default exposure is metered for a dark scene; under a daylight key light that
    /// renders a white silhouette. A volume on the rig's own layer fixes the exposure.</item>
    /// <item>An isolated render has no sky and therefore no ambient light: a key, a fill, a rim and
    /// a headlight aligned with the camera keep every visible face lit.</item>
    /// </list>
    ///
    /// Each icon is rendered twice, over black and over white, at twice the output size. The
    /// <c>Compose</c> shader recovers a true alpha channel from the difference, un-premultiplies the
    /// colour, applies a mild saturation boost and downsamples 2x2 (supersampling is the
    /// anti-aliasing: HDRP ignores MSAA on a one-off render). The result is read back asynchronously
    /// and encoded to PNG on a worker thread, so the main thread never waits for the GPU or the disk.
    /// The same worker also writes a <see cref="kThumbnailSize"/> copy for the game's UI.
    /// </summary>
    internal sealed class CaptureRig : IDisposable
    {
        /// <summary>Layer used by the rig and the subject; nothing else in the game draws on it.</summary>
        public const int kLayer = 31;

        /// <summary>Rendered size (2x supersampled).</summary>
        private const int kSize = 2048;

        /// <summary>Size of the PNG written.</summary>
        private const int kOutputSize = 1024;

        /// <summary>
        /// Size of the thumbnail written next to each icon for the game's UI, the size at which the
        /// game requests its own thumbnails. The UI draws images without mipmaps: a 1024 px icon
        /// shrunk to a ~70 px button shimmers and looks jagged, a 128 px one does not.
        /// </summary>
        public const int kThumbnailSize = 128;

        /// <summary>Saturation applied to the final image (1 = unchanged).</summary>
        private const float kSaturation = 1.5f;

        /// <summary>Empty border around the subject, as a fraction of its projected size.</summary>
        private const float kMargin = 0.06f;

        /// <summary>Fixed exposure (EV100) matched to the light rig: mid-tone walls land mid-range.</summary>
        private const float kFixedExposureEV = 14.1f;

        private const float kKeyLux = 25000f;
        private const float kFillLux = 9000f;
        private const float kRimLux = 5000f;

        /// <summary>Light that always shines from the camera, so no visible face is left unlit.</summary>
        private const float kHeadLux = 20000f;

        /// <summary>
        /// Camera yaw around the building (the building itself is never rotated): 180 looks straight
        /// at the front (+Z, the street side); 135 shows the front and one side.
        /// </summary>
        private const float kYaw = 135f;

        /// <summary>Camera pitch, in degrees above the horizon.</summary>
        private const float kPitch = 30f;

        private static readonly Color kBackground = new Color(0.18f, 0.18f, 0.20f, 1f);
        private static readonly List<Task> s_PendingWrites = new List<Task>();
        private static int s_ReadbacksInFlight;
        private static int s_ConsecutiveWriteFailures;

        /// <summary>
        /// Icons composed but not yet on disk (GPU readbacks in flight plus PNG writes queued). The
        /// generator waits while this is high, so a slow disk cannot pile up image buffers in memory.
        /// </summary>
        public static int PendingWork
        {
            get
            {
                int writes = 0;

                lock (s_PendingWrites)
                {
                    foreach (var task in s_PendingWrites)
                    {
                        if (!task.IsCompleted)
                        {
                            writes++;
                        }
                    }
                }

                return Volatile.Read(ref s_ReadbacksInFlight) + writes;
            }
        }

        /// <summary>PNG writes that failed in a row since the last success (folder read-only, disk full...).</summary>
        public static int ConsecutiveWriteFailures => Volatile.Read(ref s_ConsecutiveWriteFailures);

        /// <summary>Clears <see cref="ConsecutiveWriteFailures"/> (start of a run).</summary>
        public static void ResetWriteFailures()
        {
            Volatile.Write(ref s_ConsecutiveWriteFailures, 0);
        }

        private GameObject m_Rig;
        private Camera m_Camera;
        private HDAdditionalCameraData m_HdCamera;
        private Transform m_Headlight;
        private RenderTexture m_Target;
        private RenderTexture m_PassBlack;
        private RenderTexture m_PassWhite;
        private Material m_Compose;
        private VolumeProfile m_Profile;

        /// <summary>World metres covered by one pixel of the output image (valid after <see cref="Frame"/>).</summary>
        public float MetresPerOutputPixel => m_Camera != null ? 2f * m_Camera.orthographicSize / kOutputSize : 0f;

        /// <summary>Camera right axis in world space (valid after <see cref="Frame"/>).</summary>
        public Vector3 CameraRight => m_Camera != null ? m_Camera.transform.right : Vector3.right;

        /// <summary>Camera up axis in world space (valid after <see cref="Frame"/>).</summary>
        public Vector3 CameraUp => m_Camera != null ? m_Camera.transform.up : Vector3.up;

        /// <summary>
        /// Metres per output pixel that <see cref="Frame"/> would give these bounds, computed without
        /// touching the camera (same view direction, projection and margin). Lets the generator
        /// prefetch the next building's textures at the size its icon will use.
        /// </summary>
        public static float PredictMetresPerOutputPixel(Vector3 min, Vector3 max)
        {
            var centre = (min + max) * 0.5f;
            var inverse = Quaternion.Inverse(Quaternion.LookRotation(-ViewDirection(), Vector3.up));
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            for (int i = 0; i < 8; i++)
            {
                var local = inverse * (Corner(min, max, i) - centre);
                minX = Mathf.Min(minX, local.x);
                maxX = Mathf.Max(maxX, local.x);
                minY = Mathf.Min(minY, local.y);
                maxY = Mathf.Max(maxY, local.y);
            }

            float orthographic = Mathf.Max(0.25f, Mathf.Max((maxY - minY) * 0.5f, (maxX - minX) * 0.5f) * (1f + kMargin));
            return 2f * orthographic / kOutputSize;
        }

        /// <summary>Blocks until every queued readback has been encoded and written (end of a run).</summary>
        public static void WaitForWrites()
        {
            try
            {
                if (Volatile.Read(ref s_ReadbacksInFlight) > 0)
                {
                    AsyncGPUReadback.WaitAllRequests();
                }
            }
            catch
            {
                // Nothing in flight, or the device is gone: the pending writes below still settle.
            }

            Task[] pending;

            lock (s_PendingWrites)
            {
                pending = s_PendingWrites.ToArray();
                s_PendingWrites.Clear();
            }

            try
            {
                Task.WaitAll(pending);
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"Waiting for icon writes: {ex.Message}");
            }
        }

        /// <summary>Creates the rig (idempotent). <paramref name="compose"/> is the bundle's Compose material.</summary>
        public bool Setup(Material compose)
        {
            m_Compose = compose;

            if (m_Rig != null)
            {
                return true;
            }

            try
            {
                m_Rig = new GameObject("BuildingIconGenerator_Rig") { hideFlags = HideFlags.HideAndDontSave };
                m_Rig.SetActive(false);

                m_Target = new RenderTexture(kSize, kSize, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
                m_Target.Create();

                var cameraObject = new GameObject("BuildingIconGenerator_Camera") { hideFlags = HideFlags.HideAndDontSave };
                cameraObject.transform.SetParent(m_Rig.transform, false);

                m_Camera = cameraObject.AddComponent<Camera>();
                m_Camera.orthographic = true;
                m_Camera.cullingMask = 1 << kLayer;
                m_Camera.clearFlags = CameraClearFlags.SolidColor;
                m_Camera.backgroundColor = kBackground;
                m_Camera.targetTexture = m_Target;
                m_Camera.enabled = false;

                try
                {
                    m_HdCamera = cameraObject.AddComponent<HDAdditionalCameraData>();
                    m_HdCamera.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                    m_HdCamera.backgroundColorHDR = kBackground;

                    // Only the rig's own volume: never the world's exposure or fog.
                    m_HdCamera.volumeLayerMask = 1 << kLayer;

                    // Decals are drawn through the DBuffer: make sure this camera has it enabled.
                    m_HdCamera.customRenderingSettings = true;
                    m_HdCamera.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.Decals] = true;
                    m_HdCamera.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.Decals, true);

                    CreateExposureVolume();
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn($"HDRP camera settings not applied: {ex.Message}");
                }

                // A warm key light and a cool sky fill, as in the game.
                CreateLight("BuildingIconGenerator_Key", new Vector3(35f, 215f, 0f), kKeyLux, 5200f);
                CreateLight("BuildingIconGenerator_Fill", new Vector3(15f, 60f, 0f), kFillLux, 9000f);
                CreateLight("BuildingIconGenerator_Rim", new Vector3(70f, 130f, 0f), kRimLux, 6500f);

                // Re-aimed along the camera in Frame(): whatever the camera sees is lit.
                m_Headlight = CreateLight("BuildingIconGenerator_Head", Vector3.zero, kHeadLux, 5800f);
                return true;
            }
            catch (Exception ex)
            {
                Mod.Log.Error($"Capture rig setup failed: {ex}");
                Dispose();
                return false;
            }
        }

        /// <summary>An empty, active root to hang the subject's meshes on.</summary>
        public GameObject NewSubject()
        {
            var root = new GameObject("BuildingIconGenerator_Subject") { hideFlags = HideFlags.HideAndDontSave };
            root.transform.SetParent(m_Rig.transform, false);
            root.SetActive(true);
            return root;
        }

        /// <summary>
        /// Aims the camera at the bounds: three-quarter view, the orthographic window fitted to the
        /// eight corners as projected on the camera plane, plus <see cref="kMargin"/>.
        /// </summary>
        public void Frame(Vector3 min, Vector3 max)
        {
            var centre = (min + max) * 0.5f;
            var size = max - min;
            float extent = Mathf.Max(0.5f, Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
            var transform = m_Camera.transform;

            transform.position = centre + (ViewDirection() * (extent * 4f));
            transform.LookAt(centre);

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            for (int i = 0; i < 8; i++)
            {
                var local = transform.InverseTransformPoint(Corner(min, max, i));
                minX = Mathf.Min(minX, local.x);
                maxX = Mathf.Max(maxX, local.x);
                minY = Mathf.Min(minY, local.y);
                maxY = Mathf.Max(maxY, local.y);
            }

            transform.position += (transform.right * ((minX + maxX) * 0.5f)) + (transform.up * ((minY + maxY) * 0.5f));

            float halfWidth = (maxX - minX) * 0.5f;
            float halfHeight = (maxY - minY) * 0.5f;
            float aspect = Mathf.Max(0.01f, m_Camera.aspect);
            m_Camera.orthographicSize = Mathf.Max(0.25f, Mathf.Max(halfHeight, halfWidth / aspect) * (1f + kMargin));
            m_Camera.nearClipPlane = 0.01f;
            m_Camera.farClipPlane = extent * 12f;

            if (m_Headlight != null)
            {
                // A directional light shines along its forward axis: the camera's own rotation puts
                // it behind the lens.
                m_Headlight.rotation = transform.rotation;
            }
        }

        /// <summary>Renders one background pass (black or white) and keeps it on the GPU.</summary>
        public void RenderPass(bool white)
        {
            var background = white ? Color.white : Color.black;
            m_Camera.backgroundColor = background;

            if (m_HdCamera != null)
            {
                m_HdCamera.backgroundColorHDR = background;
            }

            m_Rig.SetActive(true);

            try
            {
                m_Camera.Render();
            }
            finally
            {
                m_Rig.SetActive(false);
            }

            ref var pass = ref white ? ref m_PassWhite : ref m_PassBlack;

            if (pass == null)
            {
                pass = new RenderTexture(m_Target.descriptor)
                {
                    name = white ? "BuildingIconGenerator_PassWhite" : "BuildingIconGenerator_PassBlack",
                    filterMode = FilterMode.Point,
                };
                pass.Create();
            }

            Graphics.CopyTexture(m_Target, pass);
        }

        /// <summary>
        /// Composes both passes into the final icon and writes it to <paramref name="path"/> without
        /// blocking: the GPU readback completes a frame or two later, and its callback hands the
        /// bytes to a worker thread for PNG encoding and the disk write, after which
        /// <paramref name="onWritten"/> runs (on that worker). GPU commands execute in submission
        /// order, so the next building's passes cannot overwrite this one before it is composed.
        /// The UI thumbnail is written to <paramref name="thumbnailPath"/> first, so a recorded icon
        /// always has one.
        /// </summary>
        public void ComposeAsync(string path, string thumbnailPath, Action onWritten)
        {
            var target = RenderTexture.GetTemporary(kOutputSize, kOutputSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            m_Compose.SetTexture("_White", m_PassWhite);
            m_Compose.SetFloat("_Saturation", kSaturation);
            Graphics.Blit(m_PassBlack, target, m_Compose);
            Interlocked.Increment(ref s_ReadbacksInFlight);

            try
            {
                AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32, request =>
                {
                    try
                    {
                        if (request.hasError)
                        {
                            Mod.Log.Warn($"GPU readback failed for {Path.GetFileName(path)}");
                        }
                        else
                        {
                            QueueWrite(request.GetData<byte>().ToArray(), path, thumbnailPath, onWritten);
                        }
                    }
                    finally
                    {
                        RenderTexture.ReleaseTemporary(target);
                        Interlocked.Decrement(ref s_ReadbacksInFlight);
                    }
                });
            }
            catch (Exception ex)
            {
                // No callback will come: undo the bookkeeping so the run does not wait for it.
                RenderTexture.ReleaseTemporary(target);
                Interlocked.Decrement(ref s_ReadbacksInFlight);
                Mod.Log.Warn($"GPU readback not started for {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (m_Rig != null)
            {
                UnityEngine.Object.DestroyImmediate(m_Rig);
                m_Rig = null;
                m_Camera = null;
                m_HdCamera = null;
                m_Headlight = null;
            }

            foreach (var texture in new[] { m_PassBlack, m_PassWhite, m_Target })
            {
                if (texture != null)
                {
                    texture.Release();
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }

            m_PassBlack = m_PassWhite = m_Target = null;

            if (m_Profile != null)
            {
                UnityEngine.Object.DestroyImmediate(m_Profile);
                m_Profile = null;
            }
        }

        private static Vector3 ViewDirection()
        {
            return Quaternion.Euler(kPitch, kYaw, 0f) * Vector3.back;
        }

        private static Vector3 Corner(Vector3 min, Vector3 max, int index)
        {
            return new Vector3(
                (index & 1) == 0 ? min.x : max.x,
                (index & 2) == 0 ? min.y : max.y,
                (index & 4) == 0 ? min.z : max.z);
        }

        private static void QueueWrite(byte[] rgba, string path, string thumbnailPath, Action onWritten)
        {
            var task = Task.Run(() =>
            {
                try
                {
                    // EncodeArrayToPNG is thread-safe.
                    if (thumbnailPath != null)
                    {
                        byte[] thumbnail = Downsample(rgba, kOutputSize, kOutputSize / kThumbnailSize);
                        WriteAtomically(thumbnailPath, ImageConversion.EncodeArrayToPNG(thumbnail, GraphicsFormat.R8G8B8A8_UNorm, kThumbnailSize, kThumbnailSize));
                    }

                    WriteAtomically(path, ImageConversion.EncodeArrayToPNG(rgba, GraphicsFormat.R8G8B8A8_UNorm, kOutputSize, kOutputSize));
                    Volatile.Write(ref s_ConsecutiveWriteFailures, 0);
                    onWritten?.Invoke();
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref s_ConsecutiveWriteFailures);
                    Mod.Log.Warn($"Writing {Path.GetFileName(path)} failed: {ex.Message}");
                }
            });

            lock (s_PendingWrites)
            {
                s_PendingWrites.RemoveAll(t => t.IsCompleted);
                s_PendingWrites.Add(task);
            }
        }

        /// <summary>sRGB byte -> linear light.</summary>
        private static readonly float[] s_ToLinear = BuildToLinear();

        /// <summary>Linear light (in 1/4095 steps) -> sRGB byte.</summary>
        private static readonly byte[] s_ToSrgb = BuildToSrgb();

        /// <summary>
        /// Shrinks a square RGBA32 image (sRGB colour, straight alpha) by an integer
        /// <paramref name="factor"/>, averaging each block in linear light weighted by alpha. Linear
        /// light keeps thin bright details (windows, road markings) from turning dark; the alpha
        /// weighting keeps the transparent background from bleeding into the edges.
        /// </summary>
        internal static byte[] Downsample(byte[] rgba, int size, int factor)
        {
            int outSize = size / factor;
            var result = new byte[outSize * outSize * 4];
            float samples = factor * factor;

            for (int oy = 0; oy < outSize; oy++)
            {
                for (int ox = 0; ox < outSize; ox++)
                {
                    float r = 0f, g = 0f, b = 0f, a = 0f;

                    for (int dy = 0; dy < factor; dy++)
                    {
                        int index = ((((oy * factor) + dy) * size) + (ox * factor)) * 4;

                        for (int dx = 0; dx < factor; dx++, index += 4)
                        {
                            float alpha = rgba[index + 3] * (1f / 255f);

                            if (alpha <= 0f)
                            {
                                continue;
                            }

                            r += s_ToLinear[rgba[index]] * alpha;
                            g += s_ToLinear[rgba[index + 1]] * alpha;
                            b += s_ToLinear[rgba[index + 2]] * alpha;
                            a += alpha;
                        }
                    }

                    int o = ((oy * outSize) + ox) * 4;

                    if (a <= 0f)
                    {
                        continue;
                    }

                    float inverse = 1f / a;
                    result[o] = ToSrgb(r * inverse);
                    result[o + 1] = ToSrgb(g * inverse);
                    result[o + 2] = ToSrgb(b * inverse);
                    result[o + 3] = (byte)Math.Min(255, (int)((a / samples * 255f) + 0.5f));
                }
            }

            return result;
        }

        private static byte ToSrgb(float linear)
        {
            int step = (int)((linear * 4095f) + 0.5f);
            return s_ToSrgb[step < 0 ? 0 : (step > 4095 ? 4095 : step)];
        }

        private static float[] BuildToLinear()
        {
            var table = new float[256];

            for (int i = 0; i < 256; i++)
            {
                double c = i / 255.0;
                table[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
            }

            return table;
        }

        private static byte[] BuildToSrgb()
        {
            var table = new byte[4096];

            for (int i = 0; i < 4096; i++)
            {
                double l = i / 4095.0;
                double c = l <= 0.0031308 ? l * 12.92 : (1.055 * Math.Pow(l, 1.0 / 2.4)) - 0.055;
                table[i] = (byte)Math.Min(255, (int)((c * 255.0) + 0.5));
            }

            return table;
        }

        /// <summary>
        /// Writes the PNG next to its final name and then swaps it in, so the icon at
        /// <paramref name="path"/> is always either the previous complete image or the new one -
        /// never a truncated file, whatever happens to the game or the disk meanwhile.
        /// </summary>
        private static void WriteAtomically(string path, byte[] png)
        {
            string temporary = path + ".tmp";

            try
            {
                File.WriteAllBytes(temporary, png);

                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(temporary, path, null, true);
                    }
                    else
                    {
                        File.Move(temporary, path);
                    }
                }
                catch (IOException)
                {
                    // The old icon is open elsewhere (an image viewer, a backup tool): overwrite it in place.
                    File.Copy(temporary, path, true);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch
                {
                    // A leftover temporary file is removed when the registry next loads.
                }
            }
        }

        private void CreateExposureVolume()
        {
            var volumeObject = new GameObject("BuildingIconGenerator_Exposure") { hideFlags = HideFlags.HideAndDontSave };
            volumeObject.transform.SetParent(m_Rig.transform, false);
            volumeObject.layer = kLayer;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.hideFlags = HideFlags.HideAndDontSave;
            m_Profile = profile;

            var exposure = profile.Add<Exposure>(true);
            exposure.mode.overrideState = true;
            exposure.mode.value = ExposureMode.Fixed;
            exposure.fixedExposure.overrideState = true;
            exposure.fixedExposure.value = kFixedExposureEV;

            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10000f;
            volume.profile = profile;
        }

        private Transform CreateLight(string name, Vector3 euler, float lux, float kelvin)
        {
            var lightObject = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            lightObject.transform.SetParent(m_Rig.transform, false);
            lightObject.transform.rotation = Quaternion.Euler(euler);
            lightObject.layer = kLayer;

            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.cullingMask = 1 << kLayer;
            light.shadows = LightShadows.None;
            light.useColorTemperature = true;
            light.colorTemperature = kelvin;

            // In HDRP the intensity of a light is driven by its additional data, in lux.
            try
            {
                lightObject.AddComponent<HDAdditionalLightData>().SetIntensity(lux);
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"HDRP light settings not applied: {ex.Message}");
            }

            return lightObject.transform;
        }
    }
}
