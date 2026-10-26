using CAS.Publishing.Installer.Catalog;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Writes CAS IDs captured on the first screen into the CAS settings assets (managerIds) once
    /// the CAS package is installed and its per-platform settings asset exists. Idempotent: only
    /// overwrites an empty/placeholder value, never a real one the user already set. Reads the CAS
    /// config requirements (asset path + field + placeholder) from the catalog, like SetupChecker.
    /// </summary>
    internal static class CasIdApplier
    {
        private const string CasId = "com.cleversolutions.ads.unity";

        /// <summary>True when a stored id should overwrite the current asset value.</summary>
        internal static bool ShouldWrite(string current, string stored, string placeholder)
        {
            if (string.IsNullOrEmpty(stored)) return false;
            if (string.IsNullOrEmpty(current)) return true;
            return !string.IsNullOrEmpty(placeholder) &&
                   string.Equals(current, placeholder, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Applies any pending CAS IDs to the CAS settings assets. Safe to call often.</summary>
        public static void ApplyPending()
        {
            // Fast exit: nothing captured means nothing to apply. Avoids catalog disk I/O on every
            // Components rebuild / window open in projects that never entered a CAS ID.
            if (string.IsNullOrEmpty(InstallerKeyStore.Get(CasId, "Android")) &&
                string.IsNullOrEmpty(InstallerKeyStore.Get(CasId, "iOS")))
                return;

            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok || load.Catalog?.External == null) return;

            ExternalRecord cas = null;
            foreach (var e in load.Catalog.External)
                if (e != null && e.Id == CasId) { cas = e; break; }
            if (cas?.Config == null) return;

            var changed = false;
            foreach (var req in cas.Config)
            {
                if (req == null || req.Kind != "settingsAssetField" || string.IsNullOrEmpty(req.Field)) continue;

                var stored = InstallerKeyStore.Get(CasId, req.Platform);
                if (string.IsNullOrEmpty(stored)) continue;

                var asset = SetupChecker.LocateAsset(req.AssetPath, req.AssetType);
                if (asset == null)
                {
                    // Clean project: the recommended install just added CAS but no CASSettings asset
                    // exists yet (CAS creates it lazily via its own window/build). Without this the
                    // entered id would live only in the keystore and never reach an asset. Create it —
                    // but only when CAS is actually installed, so we never author a settings asset for
                    // a platform whose CAS package isn't present.
                    if (!CasPresence.IsInstalled()) continue;
                    asset = CasSettingsWriter.EnsureAsset(req.Platform);
                    if (asset == null) continue; // create failed — warning already logged in the writer
                }

                var so = new SerializedObject(asset);
                var prop = so.FindProperty(req.Field);
                if (prop == null) continue;

                if (WriteIfNeeded(prop, stored, req.Placeholder))
                {
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(asset);
                    changed = true;
                    Debug.Log($"[CAS Hub] Applied CAS ID for {req.Platform}: {stored}");
                }
            }

            if (changed) AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Reads the CAS managerId already configured for a platform, or null when CAS isn't
        /// installed, the asset/field is absent, or the slot is empty / still the placeholder.
        /// Used to prefill the first screen when CAS was set up before the hub.
        /// </summary>
        public static string ReadExisting(string platform)
        {
            if (string.IsNullOrEmpty(platform)) return null;

            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok || load.Catalog?.External == null) return null;

            ExternalRecord cas = null;
            foreach (var e in load.Catalog.External)
                if (e != null && e.Id == CasId) { cas = e; break; }
            if (cas?.Config == null) return null;

            foreach (var req in cas.Config)
            {
                if (req == null || req.Kind != "settingsAssetField" || string.IsNullOrEmpty(req.Field)) continue;
                if (!string.Equals(req.Platform, platform, System.StringComparison.OrdinalIgnoreCase)) continue;

                var asset = SetupChecker.LocateAsset(req.AssetPath, req.AssetType);
                if (asset == null) return null;

                var prop = new SerializedObject(asset).FindProperty(req.Field);
                if (prop == null) return null;

                var value = prop.isArray
                    ? (prop.arraySize > 0 ? FirstString(prop) : null)
                    : (prop.propertyType == SerializedPropertyType.String ? prop.stringValue : null);

                if (string.IsNullOrEmpty(value)) return null;

                var isPlaceholder = !string.IsNullOrEmpty(req.Placeholder) &&
                                    string.Equals(value, req.Placeholder, System.StringComparison.OrdinalIgnoreCase);
                return isPlaceholder ? null : value;
            }
            return null;
        }

        /// <summary>Trims the entered managerId; null → empty. Pure/testable.</summary>
        internal static string NormalizeManagerId(string value) => value?.Trim() ?? string.Empty;

        /// <summary>
        /// Force-writes <paramref name="value"/> to the CAS managerId for <paramref name="platform"/>,
        /// overwriting any current value (unlike <see cref="ApplyPending"/>, which only fills
        /// empty/placeholder). Also persists it to the key store so a later reinstall re-applies it.
        /// No-op when CAS isn't installed (its settings asset doesn't exist yet).
        /// </summary>
        public static void SetManagerId(string platform, string value)
        {
            var v = NormalizeManagerId(value);

            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok || load.Catalog?.External == null) return;

            ExternalRecord cas = null;
            foreach (var e in load.Catalog.External)
                if (e != null && e.Id == CasId) { cas = e; break; }
            if (cas?.Config == null) return;

            foreach (var req in cas.Config)
            {
                if (req == null || req.Kind != "settingsAssetField" || string.IsNullOrEmpty(req.Field)) continue;
                if (!string.Equals(req.Platform, platform, System.StringComparison.OrdinalIgnoreCase)) continue;

                // On any "can't write the asset" path, break (not return) so the keystore write
                // below still runs — the entered id is remembered for a later (re)install.
                var asset = SetupChecker.LocateAsset(req.AssetPath, req.AssetType);
                if (asset == null) break;

                var so = new SerializedObject(asset);
                var prop = so.FindProperty(req.Field);
                if (prop == null) break;

                if (prop.isArray)
                {
                    if (prop.arraySize == 0) prop.arraySize = 1;
                    var first = prop.GetArrayElementAtIndex(0);
                    if (first.propertyType != SerializedPropertyType.String) break;
                    first.stringValue = v;
                }
                else if (prop.propertyType == SerializedPropertyType.String)
                {
                    prop.stringValue = v;
                }
                else break;

                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
                break;
            }

            InstallerKeyStore.Set(CasId, platform, v);
        }

        // managerIds is a string list — write a single-element list when the slot is empty/placeholder.
        private static bool WriteIfNeeded(SerializedProperty prop, string stored, string placeholder)
        {
            if (prop.isArray)
            {
                var current = prop.arraySize > 0 ? FirstString(prop) : null;
                if (!ShouldWrite(current, stored, placeholder)) return false;
                if (prop.arraySize == 0) prop.arraySize = 1;
                var first = prop.GetArrayElementAtIndex(0);
                if (first.propertyType != SerializedPropertyType.String) return false;
                first.stringValue = stored;
                return true;
            }
            if (prop.propertyType == SerializedPropertyType.String)
            {
                if (!ShouldWrite(prop.stringValue, stored, placeholder)) return false;
                prop.stringValue = stored;
                return true;
            }
            return false;
        }

        private static string FirstString(SerializedProperty arrayProp)
        {
            var first = arrayProp.GetArrayElementAtIndex(0);
            return first.propertyType == SerializedPropertyType.String ? first.stringValue : null;
        }
    }
}
