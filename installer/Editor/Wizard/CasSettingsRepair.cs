using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Repairs a "broken" CAS settings asset — the file is on disk, but Unity can't load it
    /// because its script reference (<c>m_Script</c>) points at a GUID that doesn't exist.
    ///
    /// How one appears: it was created while the CAS package was absent, or under a different CAS
    /// version (its script GUID changed between releases). The project then looks contradictory —
    /// the <c>Assets/CleverAdsSolutions</c> folder is there, so the scanner reads CAS as installed
    /// outside UPM, yet nothing can be written into the settings.
    ///
    /// The fix rewrites one line rather than recreating the file: everything else in the YAML
    /// (<c>managerIds</c>, ad formats, audience) is intact and there is no reason to lose it.
    /// </summary>
    internal static class CasSettingsRepair
    {
        // The CAS settings type name. Looked up by simple name inside a namespace starting with
        // "CAS", the same way CasMediation does, so this doesn't depend on the assembly name.
        private const string SettingsTypeName = "CASInitSettings";

        // m_Script: {fileID: 11500000, guid: <32 hex>, type: 3}
        private static readonly Regex ScriptGuidLine =
            new Regex(@"(m_Script:\s*\{\s*fileID:\s*\d+\s*,\s*guid:\s*)([0-9a-fA-F]{32})",
                RegexOptions.CultureInvariant);

        /// <summary>
        /// The file at the path exists but the asset doesn't load — meaning the script reference is
        /// broken. A missing file does NOT count as broken: that is simply "not created yet".
        /// </summary>
        public static bool IsBroken(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            if (!File.Exists(assetPath)) return false;
            return AssetDatabase.LoadAssetAtPath<Object>(assetPath) == null;
        }

        /// <summary>
        /// The authoritative script GUID: asked of CAS itself rather than guessed. Returns null
        /// when the CAS package isn't installed — then there is nothing to repair it with.
        /// </summary>
        public static string ResolveScriptGuid()
        {
            try
            {
                var type = FindCasType(SettingsTypeName);
                if (type == null) return null;

                var probe = ScriptableObject.CreateInstance(type);
                if (probe == null) return null;
                try
                {
                    var script = MonoScript.FromScriptableObject(probe);
                    if (script == null) return null;
                    var path = AssetDatabase.GetAssetPath(script);
                    if (string.IsNullOrEmpty(path)) return null;
                    var guid = AssetDatabase.AssetPathToGUID(path);
                    return string.IsNullOrEmpty(guid) ? null : guid;
                }
                finally
                {
                    Object.DestroyImmediate(probe);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Rewrites the script GUID in the asset to the current one and reimports. true — the asset
        /// loads afterwards. Does nothing and returns false when CAS isn't installed or the file
        /// isn't broken.
        /// </summary>
        public static bool TryRepair(string assetPath)
        {
            if (!IsBroken(assetPath)) return false;

            var guid = ResolveScriptGuid();
            if (guid == null)
            {
                Debug.LogWarning("[CAS Hub] " + assetPath + " can't be loaded and CAS isn't installed, " +
                                 "so its script reference can't be repaired — install CAS first.");
                return false;
            }

            try
            {
                var text = File.ReadAllText(assetPath);
                var fixedText = WithScriptGuid(text, guid);
                if (fixedText == null) return false;   // no m_Script line — not our format

                File.WriteAllText(assetPath, fixedText);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

                var ok = AssetDatabase.LoadAssetAtPath<Object>(assetPath) != null;
                Debug.Log("[CAS Hub] " + (ok
                    ? "Repaired the CAS settings asset at " + assetPath + " (stale script reference)."
                    : "Tried to repair " + assetPath + " but it still doesn't load."));
                return ok;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CAS Hub] Could not repair " + assetPath + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Returns the asset YAML with the script GUID replaced, or null when the text carries no
        /// <c>m_Script</c> line. Pure — extracted so the text surgery is covered by a test rather
        /// than only by hand.
        /// </summary>
        internal static string WithScriptGuid(string assetYaml, string guid)
        {
            if (string.IsNullOrEmpty(assetYaml) || string.IsNullOrEmpty(guid)) return null;
            if (!ScriptGuidLine.IsMatch(assetYaml)) return null;
            return ScriptGuidLine.Replace(assetYaml, m => m.Groups[1].Value + guid, 1);
        }

        private static Type FindCasType(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                    if (t.Name == simpleName && t.Namespace != null && t.Namespace.StartsWith("CAS"))
                        return t;
            }
            return null;
        }
    }
}
