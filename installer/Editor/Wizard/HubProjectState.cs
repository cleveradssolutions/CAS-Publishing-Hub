using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// The wizard's per-project state — the CAS ID entered on Welcome, "intro done", the platform
    /// picked, the configuration queue — kept in <c>UserSettings/CASHubState.json</c>.
    ///
    /// It used to live in EditorPrefs under keys carrying the project path. EditorPrefs is the
    /// Windows registry (a plist on macOS): deleting or re-creating a project in the same folder left
    /// the old state in place, and the reinstalled Hub skipped Welcome and wrote the previous
    /// project's CAS ID into the new one's settings. UserSettings is Unity's own per-user,
    /// per-project folder: never committed, gone with the project.
    ///
    /// Migration: when the file does not exist yet, the old EditorPrefs values are carried over only
    /// if the project shows it really went through the Hub — CAS holds a real (non-placeholder) id.
    /// A fresh project in a reused folder starts clean. The decision is made once, when the file is
    /// first written.
    /// </summary>
    internal static class HubProjectState
    {
        internal const string FileName = "CASHubState.json";
        private const string LegacyDecisionKey = "_legacyEditorPrefs";

        private static Dictionary<string, string> _values;

        private static string FilePath => Path.Combine(
            Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "UserSettings", FileName);

        /// <summary>Test hook: points the store at another file and drops the cache.</summary>
        internal static string PathOverride;

        /// <summary>Test hook: the legacy decision while <see cref="PathOverride"/> is set.</summary>
        internal static bool TrustLegacyInTests;

        private static string ActivePath => PathOverride ?? FilePath;

        /// <summary>
        /// The value for <paramref name="key"/>, or "" when unset. <paramref name="legacyPrefsKey"/>
        /// is the EditorPrefs key the value lived under before; it is read only while the legacy
        /// values are trusted (see the class summary), and copied into the file on first read.
        /// </summary>
        public static string Get(string key, string legacyPrefsKey = null)
        {
            var values = Values();
            if (values.TryGetValue(key, out var v)) return v ?? "";

            if (legacyPrefsKey != null && LegacyTrusted(values) && EditorPrefs.HasKey(legacyPrefsKey))
            {
                var legacy = ReadLegacy(legacyPrefsKey);
                if (!string.IsNullOrEmpty(legacy))
                {
                    values[key] = legacy;
                    Save(values);
                    return legacy;
                }
            }
            return "";
        }

        /// <summary>
        /// Stores <paramref name="value"/>; null or empty clears it. A cleared key stays in the file
        /// as "" — removing it would let the next <see cref="Get"/> copy the old EditorPrefs value
        /// back (Restart Intro undone, a 1.0.8 configuration queue with the Tenjin step revived).
        /// </summary>
        public static void Set(string key, string value)
        {
            var values = Values();
            value = value ?? "";
            if (values.TryGetValue(key, out var old) && (old ?? "") == value) return;
            values[key] = value;
            Save(values);
        }

        /// <summary>Drops the in-memory copy (tests, or after the file was changed on disk).</summary>
        internal static void InvalidateCache() => _values = null;

        private static Dictionary<string, string> Values()
        {
            if (_values != null) return _values;

            var path = ActivePath;
            if (File.Exists(path))
            {
                try
                {
                    _values = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path))
                              ?? new Dictionary<string, string>();
                    return _values;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CAS Hub] {FileName} is unreadable ({e.Message}) — starting the wizard state fresh.");
                }
            }

            _values = new Dictionary<string, string>
            {
                [LegacyDecisionKey] = DecideLegacy() ? "trusted" : "ignored",
            };
            Save(_values);
            return _values;
        }

        private static bool LegacyTrusted(Dictionary<string, string> values) =>
            values.TryGetValue(LegacyDecisionKey, out var d) && d == "trusted";

        // A project that went through the Hub has a real CAS ID in its CAS settings. One that was
        // re-created in the same folder has none, so the old prefs must not leak into it.
        private static bool DecideLegacy()
        {
            if (PathOverride != null) return TrustLegacyInTests;
            try
            {
                return !string.IsNullOrEmpty(CasIdApplier.ReadExisting("Android"))
                    || !string.IsNullOrEmpty(CasIdApplier.ReadExisting("iOS"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // EditorPrefs has no type info: IntroDone was a bool, everything else a string.
        private static string ReadLegacy(string key)
        {
            var s = EditorPrefs.GetString(key, "");
            if (!string.IsNullOrEmpty(s)) return s;
            return EditorPrefs.GetBool(key, false) ? "1" : "";
        }

        private static void Save(Dictionary<string, string> values)
        {
            try
            {
                var path = ActivePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(values, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CAS Hub] Couldn't save {FileName}: {e.Message}");
            }
        }
    }
}
