using System.IO;
using CAS.Publishing.Installer.Catalog;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Creates a platform's CAS settings asset when it doesn't exist yet — CAS itself only creates it
    /// lazily, via its own settings window or a build, so a freshly installed CAS has nowhere to put a
    /// captured CAS ID until this runs. Resolves the path the same way the read path does
    /// (<see cref="SetupChecker.LocateAsset"/>), so a relocated asset is reused instead of duplicated
    /// at the default path. Also reads and writes the ad-format bitmask and the audience value —
    /// restored 2026-09-03 for the Placements screen after the CAS-ID-only scope had dropped them.
    /// </summary>
    internal static class CasSettingsWriter
    {
        private const string CasId = "com.cleversolutions.ads.unity";
        // Fallback CAS settings script guid (CAS 4.7.x) — used only when no sibling asset exists to copy it from.
        private const string FallbackScriptGuid = "cd2f38c563828458c8e900006c010cd2";

        private static ConfigRequirement FindCasField(string platform)
        {
            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok || load.Catalog?.External == null) return null;
            foreach (var e in load.Catalog.External)
            {
                if (e == null || e.Id != CasId || e.Config == null) continue;
                foreach (var req in e.Config)
                    if (req != null && req.Kind == "settingsAssetField" &&
                        string.Equals(req.Platform, platform, System.StringComparison.OrdinalIgnoreCase))
                        return req;
            }
            return null;
        }

        public static int ReadAdFlags(string platform) => ReadInt(platform, "allowedAdFlags");
        public static int ReadAudience(string platform) => ReadInt(platform, "audienceTagged");

        private static int ReadInt(string platform, string field)
        {
            var req = FindCasField(platform);
            if (req == null) return 0;
            var asset = SetupChecker.LocateAsset(req.AssetPath, req.AssetType);
            if (asset == null) return 0;
            var prop = new SerializedObject(asset).FindProperty(field);
            return prop != null && IsIntLike(prop) ? prop.intValue : 0;
        }

        public static void SetAdFlags(string platform, int flags) => WriteInt(platform, "allowedAdFlags", flags);
        public static void SetAudience(string platform, int audience) => WriteInt(platform, "audienceTagged", audience);

        // allowedAdFlags (AdFlags) and audienceTagged (Audience) are ENUM fields — their
        // SerializedProperty.propertyType is Enum, NOT Integer — but intValue reads/writes the backing
        // int for both. Guarding on Integer-only silently skipped every CAS ad-format/audience write.
        private static bool IsIntLike(SerializedProperty prop) =>
            prop.propertyType == SerializedPropertyType.Integer ||
            prop.propertyType == SerializedPropertyType.Enum;

        private static void WriteInt(string platform, string field, int value)
        {
            var req = FindCasField(platform);
            if (req == null)
            {
                Debug.LogWarning("[CAS Hub] CAS settings field for " + platform +
                                 " is not declared in the catalog — nothing written.");
                return;
            }
            var asset = EnsureAssetFor(req);
            if (asset == null)
            {
                Debug.LogWarning("[CAS Hub] CAS settings asset for " + platform +
                                 " could not be created or loaded (" + req.AssetPath + ") — '" +
                                 field + "' was not written.");
                return;
            }
            var so = new SerializedObject(asset);
            var prop = so.FindProperty(field);
            if (prop == null || !IsIntLike(prop))
            {
                // Asset exists but the expected field is absent — typically a stale FallbackScriptGuid
                // (CAS .meta regenerated / version change), so the created asset has a missing script.
                // Surface it instead of silently dropping the write.
                Debug.LogWarning("[CAS Hub] CAS settings field '" + field + "' not found on " +
                                 req.AssetPath + " — CAS script GUID mismatch? (check FallbackScriptGuid). " +
                                 "Ad-format/audience value was not written.");
                return;
            }
            prop.intValue = value;
            // ApplyModifiedProperties (not …WithoutUndo) registers undo AND notifies any open CAS
            // settings Inspector, so the change shows up live the moment the toggle flips.
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }

        public static Object EnsureAsset(string platform)
        {
            var req = FindCasField(platform);
            return req == null ? null : EnsureAssetFor(req);
        }

        // Resolves the CAS settings asset the SAME way the read path does — SetupChecker.LocateAsset
        // tolerates a relocated asset (by name/type), so we never create a duplicate at the default
        // path when the real asset lives elsewhere. Only when nothing is found do we create from template.
        private static Object EnsureAssetFor(ConfigRequirement req)
        {
            var existing = SetupChecker.LocateAsset(req.AssetPath, req.AssetType);
            if (existing != null) return existing;

            // The file exists but won't load — the script reference is stale (the asset was made
            // without CAS, or under another version). Repair in place, keeping the values.
            if (CasSettingsRepair.TryRepair(req.AssetPath))
            {
                existing = SetupChecker.LocateAsset(req.AssetPath, req.AssetType);
                if (existing != null) return existing;
            }

            // Creating a settings asset for a package that isn't in the project is wrong: the file
            // would carry a dangling script reference, and that same file would then make the scanner
            // read CAS as installed "outside UPM", so the wizard would stop offering the install.
            // This exact loop was observed on 2026-09-03.
            if (!CasPresence.IsInstalled())
            {
                Debug.LogWarning("[CAS Hub] CAS is not installed — its settings asset was not created. " +
                                 "Install CAS first, then configure it.");
                return null;
            }

            return CreateFromTemplate(req.AssetPath, TemplateManagerId(req.Platform));
        }

        /// <summary>
        /// The managerId a freshly created asset starts with: the CAS ID entered on Welcome when there
        /// is one, else CAS's own "demo". Placements creates the asset when CAS was installed without
        /// the Hub's install pass, so ApplyPending never ran — the asset kept "demo", and the
        /// Configuration cell for it led to Placements, which has no ID field. Only plain id
        /// characters go into the YAML; anything else falls back to "demo".
        /// </summary>
        internal static string TemplateManagerId(string platform)
        {
            var stored = CasIdApplier.NormalizeManagerId(
                InstallerKeyStore.Get("com.cleversolutions.ads.unity", platform));
            return IsPlainId(stored) ? stored : "demo";
        }

        internal static bool IsPlainId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            foreach (var c in id)
                if (!(c < 128 && char.IsLetterOrDigit(c)) && c != '.' && c != '_') return false;
            return true;
        }

        // Creates the CAS settings asset at the default path from the template. The caller has already
        // confirmed (via LocateAsset) that no asset exists; the LoadAssetAtPath guard avoids overwriting
        // a file that happens to sit at the exact path.
        private static Object CreateFromTemplate(string assetPath, string managerId)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var existing = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
            if (existing != null) return existing;

            // Order matters: ask CAS itself first (the only authoritative source), then a sibling
            // asset, and only then the constant — it is from 4.7.x and goes stale.
            var scriptGuid = CasSettingsRepair.ResolveScriptGuid()
                             ?? SiblingScriptGuid(assetPath)
                             ?? FallbackScriptGuid;
            var name = Path.GetFileNameWithoutExtension(assetPath);
            var yaml =
                "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!114 &11400000\nMonoBehaviour:\n" +
                "  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n" +
                "  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: 0}\n" +
                "  m_Enabled: 1\n  m_EditorHideFlags: 0\n  m_Script: {fileID: 11500000, guid: " + scriptGuid + ", type: 3}\n" +
                "  m_Name: " + name + "\n  m_EditorClassIdentifier:\n  testAdMode: 0\n  managerIds:\n  - " + managerId + "\n" +
                "  allowedAdFlags: 0\n  audienceTagged: 0\n  bannerSize: 0\n  bannerRefresh: 30\n" +
                "  interstitialInterval: 30\n  loadingMode: 2\n  debugMode: 0\n  trackLocationEnabled: 0\n" +
                "  interWhenNoRewardedAd: 1\n";

            var dir = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(assetPath, yaml);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            var created = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
            if (created != null) return created;

            // Never leave behind a file that doesn't load: it would look like an installed CAS and
            // suppress the install offer.
            AssetDatabase.DeleteAsset(assetPath);
            Debug.LogWarning("[CAS Hub] Created " + assetPath + " but Unity could not load it " +
                             "(CAS script GUID mismatch) — the file was removed again.");
            return null;
        }

        // The CAS settings script guid is shared across platforms — reuse a sibling asset's guid when present.
        private static string SiblingScriptGuid(string assetPath)
        {
            var dir = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(dir)) return null;
            foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { dir }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (p == assetPath) continue;
                var name = Path.GetFileNameWithoutExtension(p);
                if (name != null && name.StartsWith("CASSettings"))
                {
                    var assetText = File.Exists(p) ? File.ReadAllText(p) : null;
                    // Read the m_Script guid out of the asset YAML itself.
                    if (assetText != null)
                    {
                        var marker = "guid: ";
                        var idx = assetText.IndexOf("m_Script:", System.StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            var g = assetText.IndexOf(marker, idx, System.StringComparison.Ordinal);
                            if (g >= 0)
                            {
                                g += marker.Length;
                                var end = assetText.IndexOf(',', g);
                                if (end > g) return assetText.Substring(g, end - g).Trim();
                            }
                        }
                    }
                }
            }
            return null;
        }
    }
}
