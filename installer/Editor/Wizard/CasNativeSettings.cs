using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Opens CAS.AI's own native settings window for a platform (where CAS ID, ad formats,
    /// mediation networks, and the Tenjin key actually live now that the Configuration screen
    /// no longer writes any of that itself). Best-effort: tries the CAS menu item first, falls
    /// back to pinging the platform's settings asset in the Project window, and never throws —
    /// this is a convenience shortcut, not a critical path.
    /// </summary>
    internal static class CasNativeSettings
    {
        /// <summary>Menu-path candidates for a platform: CAS 4.7+ renamed the items to
        /// "Android Settings..." — that's the common case for modern CAS projects, so try it
        /// first, then fall back to the pre-4.7 plain "Android Settings" name.</summary>
        internal static string[] MenuCandidates(string platform)
        {
            var name = platform == "iOS" ? "iOS Settings" : "Android Settings";
            return new[]
            {
                "Assets/CleverAdsSolutions/" + name + "...",
                "Assets/CleverAdsSolutions/" + name,
            };
        }

        public static void Open(string platform)
        {
            try
            {
                foreach (var menu in MenuCandidates(platform))
                    if (UnityEditor.Menu.GetEnabled(menu) && EditorApplication.ExecuteMenuItem(menu)) return;

                var asset = AssetDatabase.LoadMainAssetAtPath(
                    "Assets/CleverAdsSolutions/Resources/CASSettings" + platform + ".asset");
                if (asset != null)
                {
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                }
                else
                {
                    Debug.Log($"[CAS Hub] CAS settings for {platform} aren't available yet — " +
                              "install/configure CAS first.");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CAS Hub] Could not open native CAS settings for {platform}: {e.Message}");
            }
        }
    }
}
