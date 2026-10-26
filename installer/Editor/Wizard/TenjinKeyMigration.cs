using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CAS.Publishing.Installer.Catalog;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Carries the Tenjin SDK key over from the retired attribution wrapper.
    ///
    /// The wrapper kept its keys in a ScriptableObject under <c>Resources/</c> (the catalog names
    /// the asset and its two fields — <see cref="UninstallRecord.LegacySettings"/>). Once the
    /// wrapper is removed that asset is left behind with a dead script reference, so nothing
    /// loads it — but the key inside is exactly what the Hub's Tenjin step asks for. Reading the
    /// YAML directly (no script needed) and filling the empty slots of <c>PublishingSettings</c>
    /// means a project that had attribution keeps it, instead of ending up with a key nobody uses
    /// and a wizard asking for one.
    ///
    /// Only empty slots are filled: a key entered in the Hub is never overwritten by a stale one.
    /// </summary>
    internal static class TenjinKeyMigration
    {
        private const string LogPrefix = "[CAS Hub]";

        /// <summary>
        /// Fills empty <c>PublishingSettings</c> keys from every legacy settings asset the catalog
        /// knows. Returns the platforms whose key was migrated (empty when there was nothing to do).
        /// </summary>
        public static List<string> TryMigrate()
        {
            var migrated = new List<string>();
            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok || load.Catalog?.Uninstall == null) return migrated;

            foreach (var record in load.Catalog.Uninstall)
            {
                var legacy = record?.LegacySettings;
                if (legacy == null || string.IsNullOrEmpty(legacy.Resource)) continue;

                var path = FindResourceAsset(legacy.Resource);
                if (path == null) continue;

                string yaml;
                try { yaml = File.ReadAllText(path); }
                catch { continue; }

                MigratePlatform("Android", legacy.AndroidKeyField, yaml, path, migrated);
                MigratePlatform("iOS", legacy.IosKeyField, yaml, path, migrated);
            }
            return migrated;
        }

        private static void MigratePlatform(string platform, string field, string yaml, string source, List<string> migrated)
        {
            if (string.IsNullOrEmpty(field)) return;
            if (!string.IsNullOrEmpty(TenjinKeySettings.ReadExisting(platform))) return;

            var key = ReadYamlString(yaml, field);
            if (string.IsNullOrEmpty(key)) return;
            if (!TenjinKeySettings.IsValid(key, TenjinKeySettings.RegexFor(platform)))
            {
                Debug.LogWarning($"{LogPrefix} The {platform} Tenjin key in {source} ('{key}') is not a valid " +
                                 "SDK key — not migrated; set the key in " + TenjinKeySettings.KeyHome + ".");
                return;
            }

            if (!TenjinKeySettings.Set(platform, key)) return;
            migrated.Add(platform);
            Debug.Log($"{LogPrefix} Tenjin SDK key for {platform} migrated from the retired wrapper's " +
                      $"{Path.GetFileName(source)} into PublishingSettings.");
        }

        /// <summary>The first <c>Resources/&lt;name&gt;.asset</c> under Assets/, or null.</summary>
        private static string FindResourceAsset(string resourceName)
        {
            var assets = Application.dataPath;
            if (!Directory.Exists(assets)) return null;
            var fileName = resourceName + ".asset";
            try
            {
                foreach (var file in Directory.EnumerateFiles(assets, fileName, SearchOption.AllDirectories))
                {
                    var parent = Path.GetFileName(Path.GetDirectoryName(file));
                    if (string.Equals(parent, "Resources", StringComparison.OrdinalIgnoreCase)) return file;
                }
            }
            catch { /* unreadable tree → nothing to migrate */ }
            return null;
        }

        /// <summary>
        /// A top-level serialized string field of a Unity YAML asset: <c>  field: value</c>. Quoted
        /// values are unquoted; an empty value is null. Pure, so the parsing rule is testable.
        /// </summary>
        internal static string ReadYamlString(string yaml, string field)
        {
            if (string.IsNullOrEmpty(yaml) || string.IsNullOrEmpty(field)) return null;
            var m = Regex.Match(yaml, @"^\s*" + Regex.Escape(field) + @":[ \t]*(.*?)[ \t]*\r?$", RegexOptions.Multiline);
            if (!m.Success) return null;

            var value = m.Groups[1].Value;
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0])
                value = value.Substring(1, value.Length - 2);
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
