using System.IO;
using CAS.Publishing.Installer.Catalog;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    internal enum ReqStatus
    {
        Missing,        // file / settings asset absent
        NotConfigured,  // present but empty / placeholder value
        Configured,     // good
        NotApplicable,  // nothing to check (unknown kind, no params)
    }

    /// <summary>Result of evaluating one <see cref="ConfigRequirement"/>.</summary>
    internal sealed class ReqResult
    {
        public ReqStatus Status;
        public string Value;   // what's there (key / id / file path), if any
        public string Error;   // short explanation when missing / not configured
    }

    /// <summary>
    /// Generic handlers for the config requirement kinds the installer understands. The catalog
    /// (metadata) declares the requirements declaratively; this evaluates them against the project.
    /// Read-only — never mutates the project.
    /// </summary>
    internal static class SetupChecker
    {
        public static ReqResult Evaluate(ConfigRequirement req)
        {
            if (req == null) return new ReqResult { Status = ReqStatus.NotApplicable };
            switch (req.Kind)
            {
                case "assetFile":          return CheckAssetFile(req);
                case "settingsAssetField": return CheckSettingsField(req);
                case TenjinKeySettings.Kind: return CheckPublishingField(req);
                default:
                    return new ReqResult { Status = ReqStatus.NotApplicable, Error = $"unknown kind '{req.Kind}'" };
            }
        }

        private static ReqResult CheckAssetFile(ConfigRequirement req)
        {
            if (string.IsNullOrEmpty(req.FileName))
                return new ReqResult { Status = ReqStatus.NotApplicable, Error = "no fileName" };

            try
            {
                var matches = Directory.GetFiles(Application.dataPath, req.FileName, SearchOption.AllDirectories);
                if (matches.Length > 0)
                {
                    var rel = "Assets" + matches[0].Substring(Application.dataPath.Length).Replace('\\', '/');
                    return new ReqResult { Status = ReqStatus.Configured, Value = rel };
                }
            }
            catch (System.Exception e)
            {
                return new ReqResult { Status = ReqStatus.Missing, Error = e.Message };
            }

            return new ReqResult { Status = ReqStatus.Missing, Error = $"{req.FileName} not found in Assets/" };
        }

        private static ReqResult CheckSettingsField(ConfigRequirement req)
        {
            var asset = LocateAsset(req);
            if (asset == null)
                return new ReqResult { Status = ReqStatus.Missing, Error = "settings asset not found" };

            if (string.IsNullOrEmpty(req.Field))
                return new ReqResult { Status = ReqStatus.Configured, Value = "settings present" };

            var prop = new SerializedObject(asset).FindProperty(req.Field);
            if (prop == null)
                return new ReqResult { Status = ReqStatus.NotConfigured, Error = $"field '{req.Field}' not found" };

            var value = ReadValue(prop);
            var isPlaceholder = !string.IsNullOrEmpty(req.Placeholder) &&
                                string.Equals(value, req.Placeholder, System.StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(value) || isPlaceholder)
                return new ReqResult { Status = ReqStatus.NotConfigured, Value = value, Error = "not set" };

            return new ReqResult { Status = ReqStatus.Configured, Value = value };
        }

        // A field of the Hub's own PublishingSettings asset (the Tenjin key). The asset is ours, so
        // its location is known and only the field comes from the requirement; a missing asset means
        // the Tenjin step has never run, which is "not configured" rather than a broken project.
        private static ReqResult CheckPublishingField(ConfigRequirement req)
        {
            var field = string.IsNullOrEmpty(req.Field)
                ? TenjinKeySettings.DefaultFieldFor(req.Platform)
                : req.Field;

            var asset = TenjinKeySettings.Find();
            if (asset == null)
                return new ReqResult { Status = ReqStatus.NotConfigured, Error = "not set" };

            var prop = new SerializedObject(asset).FindProperty(field);
            if (prop == null)
                return new ReqResult { Status = ReqStatus.NotConfigured, Error = $"field '{field}' not found" };

            var value = ReadValue(prop);
            if (string.IsNullOrEmpty(value))
                return new ReqResult { Status = ReqStatus.NotConfigured, Value = value, Error = "not set" };

            return new ReqResult { Status = ReqStatus.Configured, Value = value };
        }

        private static Object LocateAsset(ConfigRequirement req)
            => LocateAsset(req.AssetPath, req.AssetType);

        /// <summary>
        /// Locates a settings asset by explicit path or by ScriptableObject type name. When an explicit
        /// <paramref name="assetPath"/> is given but nothing is there, the asset may have been moved out
        /// of the catalog's default folder (e.g. a relocated CAS settings asset) — so we fall back to
        /// finding it by file name. If the configured path sits in a <c>Resources</c> folder (the asset
        /// is loaded by name via <c>Resources.Load</c>, so it MUST live under some Resources folder), the
        /// fallback is restricted to Resources folders — mirroring how the SDK actually finds it at
        /// runtime and avoiding false positives from stray copies elsewhere. Otherwise it searches
        /// anywhere under Assets/. Keeps the default path as the fast path without hard-pinning location.
        /// </summary>
        internal static Object LocateAsset(string assetPath, string assetType)
        {
            // 1. Exact path — the default location.
            if (!string.IsNullOrEmpty(assetPath))
            {
                var atPath = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (atPath != null) return atPath;

                // 2. Moved out of the default folder → find it by file name. Restrict to Resources
                //    folders when the configured asset is a Resources-loaded one (the universal case).
                var byName = FindByFileName(Path.GetFileNameWithoutExtension(assetPath),
                                            requireResources: IsUnderResources(assetPath));
                if (byName != null) return byName;
            }

            // 3. Type-based search (catalog specifies a ScriptableObject type rather than a path).
            if (!string.IsNullOrEmpty(assetType))
            {
                var guids = AssetDatabase.FindAssets("t:" + assetType);
                if (guids.Length > 0)
                    return AssetDatabase.LoadAssetAtPath<Object>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }
            return null;
        }

        /// <summary>
        /// True when an asset path sits inside a Unity <c>Resources</c> special folder (has a
        /// <c>/Resources/</c> path segment). Case-sensitive, because Unity only treats an exactly-named
        /// "Resources" folder as special. <c>Assets/MyResources/x</c> is NOT a match (no segment boundary).
        /// </summary>
        internal static bool IsUnderResources(string assetPath)
            => !string.IsNullOrEmpty(assetPath) && assetPath.Replace('\\', '/').Contains("/Resources/");

        /// <summary>
        /// Locates an asset by its exact file name (no extension). Confirms an exact match —
        /// <see cref="AssetDatabase.FindAssets(string)"/> does token/prefix matching, so "CASSettings"
        /// would also surface "CASSettingsAndroid"; we filter to the precise file name. When
        /// <paramref name="requireResources"/>, only candidates under a <c>Resources</c> folder are
        /// accepted (matches how a Resources-loaded settings asset is actually found at runtime).
        /// </summary>
        private static Object FindByFileName(string fileName, bool requireResources)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            foreach (var guid in AssetDatabase.FindAssets(fileName))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), fileName,
                        System.StringComparison.OrdinalIgnoreCase))
                    continue;

                if (requireResources && !IsUnderResources(path))
                    continue;

                var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (obj != null) return obj;
            }
            return null;
        }

        private static string ReadValue(SerializedProperty prop)
        {
            if (prop.propertyType == SerializedPropertyType.String)
                return prop.stringValue;

            if (prop.isArray)
            {
                if (prop.arraySize == 0) return null;
                var first = prop.GetArrayElementAtIndex(0);
                return first.propertyType == SerializedPropertyType.String
                    ? first.stringValue
                    : $"[{prop.arraySize} item(s)]";
            }

            return null;
        }
    }
}
