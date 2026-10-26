using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Settles the project conflicts CAS reports in its own settings window, so the client doesn't
    /// have to open it and press the buttons by hand. Runs once the ad formats are chosen and the
    /// solutions are enabled: that is when the dependency set is final.
    ///
    /// Both operations are best-effort and tolerate CAS being absent; neither throws.
    /// </summary>
    internal static class CasProjectConflicts
    {
        /// <summary>
        /// The Unity Ads package from Package Manager. CAS ships its own Unity Ads adapter, and
        /// this package pulls the same native libraries a second time — the build fails on the
        /// duplication. CAS reports it as an Error, not a warning.
        /// </summary>
        private const string LegacyUnityAdsPackage = "com.unity.ads";

        /// <summary>Settles everything we can settle ourselves. Order doesn't matter: the package
        /// goes through Package Manager, the resolve works on the *Dependencies.xml files.</summary>
        public static void ResolveAll(string platform)
        {
            RemoveLegacyUnityAds();
            if (platform != "iOS") ResolveAndroidDependencies();
        }

        /// <summary>
        /// Removes the legacy Unity Ads package when the manifest carries it. Asynchronous —
        /// Package Manager triggers its own re-resolve and domain reload; the configuration phase
        /// survives that, its state lives on disk (HubProjectState).
        /// </summary>
        public static bool RemoveLegacyUnityAds()
        {
            if (!AutoInstaller.ManifestHasDependency(LegacyUnityAdsPackage)) return false;

            Debug.Log("[CAS Hub] Removing " + LegacyUnityAdsPackage +
                      " — CAS ships its own Unity Ads adapter, and keeping both duplicates the " +
                      "native libraries and breaks the Android build.");
            try
            {
                Client.Remove(LegacyUnityAdsPackage);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CAS Hub] Could not remove " + LegacyUnityAdsPackage + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Runs the Android dependency resolution through CAS itself — it both prepares gradle
        /// (<c>CASPreprocessGradle.Configure</c>) and calls EDM4U. Requires the main gradle
        /// template to be enabled; without it CAS says so in the console on its own.
        /// </summary>
        public static bool ResolveAndroidDependencies()
        {
            try
            {
                var utils = FindCasType("CASEditorUtils");
                if (utils == null) return Warn("CASEditorUtils type not found");

                var method = utils.GetMethod("TryResolveAndroidDependencies",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null);
                if (method == null) return Warn("TryResolveAndroidDependencies(bool) not found");

                var ok = method.Invoke(null, new object[] { true }) is bool b && b;
                Debug.Log("[CAS Hub] Android dependency resolution " + (ok ? "finished." : "did not run."));
                return ok;
            }
            catch (Exception e)
            {
                return Warn("reflection error: " + e.Message);
            }
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

        private static bool Warn(string why)
        {
            Debug.LogWarning("[CAS Hub] Android dependencies were not resolved (" + why +
                             "). Run Assets → External Dependency Manager → Android Resolver → " +
                             "Resolve if the build complains about missing libraries.");
            return false;
        }
    }
}
