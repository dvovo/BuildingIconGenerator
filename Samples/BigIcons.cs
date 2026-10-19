// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// Optional integration with Building Icon Generator (BIG), without a compile-time reference.
//
// Copy this file into your own mod and change the namespace. Your mod keeps working when BIG is
// not installed: every call simply reports "no icon". When BIG is installed, calls go straight to
// its API through delegates bound once, so they cost about the same as direct calls.

namespace YourMod.Integration
{
    using System;
    using System.Reflection;

    /// <summary>Thin, reflection-based client for <c>BuildingIconGenerator.Api.BuildingIcons</c>.</summary>
    public static class BigIcons
    {
        private const string kAssembly = "BuildingIconGenerator";
        private const string kType = "BuildingIconGenerator.Api.BuildingIcons";
        private const int kApiVersion = 1;
        private const int kRetryMilliseconds = 2000;

        private delegate bool TryGetString(string prefabName, out string value);

        private static bool s_Resolved;
        private static bool s_Incompatible;
        private static int s_LastAttempt;
        private static bool s_Attempted;
        private static TryGetString s_TryGetPath;
        private static TryGetString s_TryGetUri;
        private static TryGetString s_TryGetThumbnailUri;
        private static Func<string, bool> s_HasIcon;
        private static Action<string> s_RequestGeneration;
        private static EventInfo s_IconUpdated;

        /// <summary>True when Building Icon Generator is installed and loaded.</summary>
        public static bool IsInstalled => Resolve();

        /// <summary>Full path of the building's PNG, if it has an icon.</summary>
        public static bool TryGetPath(string prefabName, out string path)
        {
            path = null;
            return Resolve() && s_TryGetPath(prefabName, out path);
        }

        /// <summary>UI address of the building's icon (coui://...), if it has one.</summary>
        public static bool TryGetUri(string prefabName, out string uri)
        {
            uri = null;
            return Resolve() && s_TryGetUri(prefabName, out uri);
        }

        /// <summary>
        /// UI address of the building's 128x128 thumbnail (coui://...), if it has one. Use it for
        /// anything shown at button size, such as UIObject.m_Icon: the full-size icon shrunk that
        /// much looks jagged in the game's UI.
        /// </summary>
        public static bool TryGetThumbnailUri(string prefabName, out string uri)
        {
            uri = null;
            return Resolve() && s_TryGetThumbnailUri(prefabName, out uri);
        }

        /// <summary>True when the building has an icon.</summary>
        public static bool HasIcon(string prefabName)
        {
            return Resolve() && s_HasIcon(prefabName);
        }

        /// <summary>Asks BIG to render the building's icon at the next opportunity. No effect without BIG.</summary>
        public static void RequestGeneration(string prefabName)
        {
            if (Resolve())
            {
                s_RequestGeneration(prefabName);
            }
        }

        /// <summary>Subscribes to "icon written" notifications (main thread, argument: prefab name).</summary>
        public static bool SubscribeIconUpdated(Action<string> handler)
        {
            if (!Resolve() || s_IconUpdated == null)
            {
                return false;
            }

            s_IconUpdated.AddEventHandler(null, handler);
            return true;
        }

        /// <summary>Removes a handler added with <see cref="SubscribeIconUpdated"/>.</summary>
        public static void UnsubscribeIconUpdated(Action<string> handler)
        {
            if (Resolve())
            {
                s_IconUpdated?.RemoveEventHandler(null, handler);
            }
        }

        /// <summary>
        /// Binds the API once BIG's assembly is loaded. Until then it is looked up again at most every
        /// two seconds, so the order in which the game loads the two mods does not matter and calls
        /// stay cheap when BIG is not installed.
        /// </summary>
        private static bool Resolve()
        {
            if (s_Resolved || s_Incompatible)
            {
                return s_Resolved;
            }

            int now = Environment.TickCount;

            if (s_Attempted && unchecked(now - s_LastAttempt) < kRetryMilliseconds)
            {
                return false;
            }

            s_Attempted = true;
            s_LastAttempt = now;

            try
            {
                Type api = null;

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name == kAssembly)
                    {
                        api = assembly.GetType(kType);
                        break;
                    }
                }

                if (api == null)
                {
                    return false;
                }

                // A future BIG with a different API version is treated as not installed.
                if (!(api.GetProperty("ApiVersion")?.GetValue(null) is int version) || version != kApiVersion)
                {
                    s_Incompatible = true;
                    return false;
                }

                var byRefString = typeof(string).MakeByRefType();
                s_TryGetPath = (TryGetString)Delegate.CreateDelegate(typeof(TryGetString), api.GetMethod("TryGetPath", new[] { typeof(string), byRefString }));
                s_TryGetUri = (TryGetString)Delegate.CreateDelegate(typeof(TryGetString), api.GetMethod("TryGetUri", new[] { typeof(string), byRefString }));
                s_TryGetThumbnailUri = (TryGetString)Delegate.CreateDelegate(typeof(TryGetString), api.GetMethod("TryGetThumbnailUri", new[] { typeof(string), byRefString }));
                s_HasIcon = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), api.GetMethod("HasIcon", new[] { typeof(string) }));
                s_RequestGeneration = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), api.GetMethod("RequestGeneration", new[] { typeof(string) }));
                s_IconUpdated = api.GetEvent("IconUpdated");
                s_Resolved = true;
            }
            catch
            {
                // An incompatible version of BIG: behave as if it were not installed, and stop looking.
                s_Incompatible = true;
            }

            return s_Resolved;
        }
    }
}
