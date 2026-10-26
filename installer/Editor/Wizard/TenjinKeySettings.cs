using System;
using System.IO;
using System.Text.RegularExpressions;
using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Runtime;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// The Tenjin SDK key: where it is declared, where it is stored and how it is validated.
    ///
    /// From CAS 4.8.1 the Tenjin SDK ships inside CAS and CAS initializes it, so the key is a CAS
    /// concern — the catalog declares it as a <c>publishingField</c> requirement on the CAS record,
    /// one per platform. The value itself lives in the Hub's own <see cref="PublishingSettings"/>
    /// asset, because CAS has no settings field for it (it only accepts the key at initialization,
    /// through MediationExtras). The requirement carries the field name, hint, regex and help URL;
    /// the constants here are fallbacks for an unreadable catalog.
    /// </summary>
    internal static class TenjinKeySettings
    {
        /// <summary>The requirement kind: a field of the Hub's own publishing settings asset.</summary>
        public const string Kind = "publishingField";

        /// <summary>
        /// Whether the wizard asks for the key at all. Off for now: the step, its Configure panel,
        /// its table cell and the Done row are hidden, the code stays. The catalog still declares
        /// the requirement, so SetupModel drops it while this is off. Headless agent mode still
        /// accepts a <c>tenjinKey</c> when one is passed. Not a const — a const false would turn the
        /// gated branches into unreachable-code warnings.
        /// </summary>
        internal static readonly bool StepEnabled = false;

        /// <summary>True for the catalog's Tenjin-key requirement while the step is hidden — the
        /// wizard and the Status report then act as if the catalog never declared it.</summary>
        internal static bool IsHidden(ConfigRequirement req, bool stepEnabled) =>
            !stepEnabled && req?.Kind == Kind;

        internal static bool IsHidden(ConfigRequirement req) => IsHidden(req, StepEnabled);

        /// <summary>Where the user-facing copy says the key belongs: the Hub's step while it is
        /// shown, the CAS dashboard while it is hidden.</summary>
        internal static string KeyHome =>
            StepEnabled ? "the Hub's Tenjin step" : "the app's settings in the CAS dashboard";

        /// <summary>Where the asset is created. Any <c>Resources</c> folder works for the runtime
        /// load, so a relocated asset is found by name (<see cref="SetupChecker.LocateAsset"/>).</summary>
        public const string DefaultAssetPath = "Assets/Resources/" + PublishingSettings.ResourceName + ".asset";

        public const string AndroidField = "tenjinKeyAndroid";
        public const string IosField = "tenjinKeyIos";

        // A Tenjin SDK key is a run of letters and digits. Anything with whitespace or punctuation
        // is a paste error, not a key; the length is left open so a future key format still passes.
        internal const string DefaultRegex = "^[A-Za-z0-9]+$";
        internal const string DefaultHint = "Tenjin SDK key";
        internal const string DefaultHelpUrl = "https://www.tenjin.io/dashboard/apps";

        private const string CasId = "com.cleversolutions.ads.unity";

        /// <summary>True when the trimmed value is non-empty and matches the pattern. Pure/testable.</summary>
        internal static bool IsValid(string value, string regex)
        {
            if (string.IsNullOrEmpty(value)) return false;
            var v = value.Trim();
            if (v.Length == 0) return false;
            return string.IsNullOrEmpty(regex) || Regex.IsMatch(v, regex);
        }

        /// <summary>Trims the entered key; null → empty. Pure/testable.</summary>
        internal static string Normalize(string value) => value?.Trim() ?? string.Empty;

        /// <summary>The asset field holding a platform's key when the catalog says nothing. Pure/testable.</summary>
        internal static string DefaultFieldFor(string platform) =>
            string.Equals(platform, "iOS", StringComparison.OrdinalIgnoreCase) ? IosField : AndroidField;

        /// <summary>
        /// The catalog requirement for a platform, or null when the catalog is unreadable or the CAS
        /// record does not declare the key (an older catalog). Callers fall back to the constants.
        /// </summary>
        public static ConfigRequirement Requirement(string platform)
        {
            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok || load.Catalog?.External == null) return null;
            foreach (var e in load.Catalog.External)
            {
                if (e == null || e.Id != CasId || e.Config == null) continue;
                foreach (var req in e.Config)
                    if (req != null && req.Kind == Kind &&
                        string.Equals(req.Platform, platform, StringComparison.OrdinalIgnoreCase))
                        return req;
            }
            return null;
        }

        public static string FieldFor(string platform)
        {
            var req = Requirement(platform);
            return string.IsNullOrEmpty(req?.Field) ? DefaultFieldFor(platform) : req.Field;
        }

        public static string RegexFor(string platform)
        {
            var req = Requirement(platform);
            return string.IsNullOrEmpty(req?.Regex) ? DefaultRegex : req.Regex;
        }

        public static string HintFor(string platform)
        {
            var req = Requirement(platform);
            return string.IsNullOrEmpty(req?.Hint) ? DefaultHint : req.Hint;
        }

        public static string HelpUrlFor(string platform)
        {
            var req = Requirement(platform);
            return string.IsNullOrEmpty(req?.Help) ? DefaultHelpUrl : req.Help;
        }

        /// <summary>The existing asset, wherever it was moved to, or null.</summary>
        public static PublishingSettings Find() =>
            SetupChecker.LocateAsset(DefaultAssetPath, nameof(PublishingSettings)) as PublishingSettings;

        /// <summary>
        /// The key already stored for a platform, or null when the asset is absent or the slot is
        /// empty. Used to prefill the Tenjin step and for the Done checklist.
        /// </summary>
        public static string ReadExisting(string platform)
        {
            var asset = Find();
            if (asset == null) return null;
            var value = asset.TenjinKeyFor(platform);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>
        /// Writes a platform's key, creating the asset at <see cref="DefaultAssetPath"/> when the
        /// project has none. Goes through SerializedObject like every other asset write in the Hub,
        /// so the field name can come from the catalog. Returns false (with a warning) when the
        /// write could not land.
        /// </summary>
        public static bool Set(string platform, string value)
        {
            var asset = Find() ?? Create();
            if (asset == null) return false;

            var field = FieldFor(platform);
            var so = new SerializedObject(asset);
            var prop = so.FindProperty(field);
            if (prop == null || prop.propertyType != SerializedPropertyType.String)
            {
                Debug.LogWarning("[CAS Hub] Publishing settings have no string field '" + field +
                                 "' — the Tenjin key for " + platform + " was not written.");
                return false;
            }

            prop.stringValue = Normalize(value);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return true;
        }

        private static PublishingSettings Create()
        {
            try
            {
                var dir = Path.GetDirectoryName(DefaultAssetPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    dir = dir.Replace('\\', '/');
                    if (!AssetDatabase.IsValidFolder(dir))
                    {
                        Directory.CreateDirectory(dir);
                        AssetDatabase.Refresh();
                    }
                }

                var asset = ScriptableObject.CreateInstance<PublishingSettings>();
                AssetDatabase.CreateAsset(asset, DefaultAssetPath);
                AssetDatabase.SaveAssets();
                return asset;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CAS Hub] Could not create " + DefaultAssetPath + ": " + e.Message);
                return null;
            }
        }
    }
}
