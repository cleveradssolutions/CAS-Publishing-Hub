using CAS.Publishing.Installer.Catalog;
using UnityEditor;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Read-only access to the CAS managerId already configured on the CAS per-platform settings
    /// asset. The installer no longer writes CAS settings — CAS ID, ad formats, and audience are
    /// configured entirely in CAS.AI's own settings window (<see cref="CasNativeSettings"/>). This
    /// reader is used to display the already-configured id (Done screen) and to decide whether a
    /// build-target switch should prompt the user to finish configuring a platform
    /// (<see cref="BuildTargetWatcher"/>/<see cref="BuildSwitchPolicy"/>). Reads the CAS config
    /// requirements (asset path + field + placeholder) from the catalog, like SetupChecker.
    /// </summary>
    internal static class CasSettingsReader
    {
        private const string CasId = "com.cleversolutions.ads.unity";

        /// <summary>
        /// Reads the CAS managerId already configured for a platform, or null when CAS isn't
        /// installed, the asset/field is absent, or the slot is empty / still the placeholder.
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

        private static string FirstString(SerializedProperty arrayProp)
        {
            var first = arrayProp.GetArrayElementAtIndex(0);
            return first.propertyType == SerializedPropertyType.String ? first.stringValue : null;
        }
    }
}
