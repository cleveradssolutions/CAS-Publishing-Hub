using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Ensures the custom Android build templates exist in <c>Assets/Plugins/Android/</c> by copying the
    /// running Editor's defaults. Only creates missing files (never overwrites). Graceful: a missing
    /// default source is logged and skipped — never throws, never half-breaks the project.
    /// </summary>
    internal static class AndroidBuildFix
    {
        // Resolved at runtime so it matches the running Editor version, wherever it's installed. Asked
        // of Unity rather than built from applicationContentsPath: on macOS the Android module sits
        // next to Unity.app, not inside its Contents, so the old path found no templates there.
        private static string AndroidPlayerDir
        {
            get
            {
                try
                {
                    var dir = BuildPipeline.GetPlaybackEngineDirectory(BuildTarget.Android, BuildOptions.None);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
                catch (System.Exception) { /* Android module not installed — fall through */ }
                return Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines", "AndroidPlayer");
            }
        }

        // Per-file Editor default source CANDIDATES — tried in order, first that exists wins. This is
        // what makes the Fix work across Unity versions (2022.3 and 6000+): the gradle templates keep
        // their name + the GradleTemplates dir on both, but the Custom Main Manifest default moved
        // (Unity 6: Apk/UnityManifest.xml; older layouts: GradleTemplates/ or Apk/AndroidManifest.xml).
        private static IEnumerable<string> EditorSourceCandidates(string destFile)
        {
            var ap = AndroidPlayerDir;
            if (string.Equals(destFile, "AndroidManifest.xml", System.StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.Combine(ap, "Apk", "UnityManifest.xml");
                yield return Path.Combine(ap, "Tools", "GradleTemplates", "AndroidManifest.xml");
                yield return Path.Combine(ap, "Apk", "AndroidManifest.xml");
            }
            else
            {
                yield return Path.Combine(ap, "Tools", "GradleTemplates", destFile);
            }
        }

        // Minimal valid main manifest — written only if no Editor default manifest is found (Unity
        // layout differences). Unity merges its own activity/content into it.
        private const string MinimalMainManifest =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">\n" +
            "    <application />\n" +
            "</manifest>\n";

        private static string DestDirAbs => Path.Combine(
            Application.dataPath, "Plugins", "Android");

        /// <summary>Required templates currently absent from the project.</summary>
        public static List<string> MissingNow()
        {
            var present = new List<string>();
            if (Directory.Exists(DestDirAbs))
                foreach (var f in Directory.GetFiles(DestDirAbs))
                    present.Add(Path.GetFileName(f));
            return AndroidBuildTemplates.Missing(present);
        }

        /// <summary>
        /// Creates any missing Android build template from the Editor default. Returns how many were
        /// created. Logs (and skips) a template whose Editor default can't be found.
        /// </summary>
        public static int Ensure()
        {
            var created = CreateMissing();
            PinErrorProne();
            return created;
        }

        /// <summary>
        /// Unity 2022.3 only: pins error_prone_annotations in baseProjectTemplate.gradle (see
        /// <see cref="AndroidBuildTemplates.WithErrorProneForce"/>). Also patches a template the
        /// project already had. Unity 6 builds without it, so it is left alone there.
        /// </summary>
        internal static void PinErrorProne()
        {
#if !UNITY_6000_0_OR_NEWER
            var path = Path.Combine(DestDirAbs, "baseProjectTemplate.gradle");
            if (!File.Exists(path)) return;
            try
            {
                var text = File.ReadAllText(path);
                var patched = AndroidBuildTemplates.WithErrorProneForce(text);
                if (patched == text) return;
                File.WriteAllText(path, patched);
                Debug.Log("[CAS Hub] Pinned error_prone_annotations to 2.28.0 in " +
                          "Assets/Plugins/Android/baseProjectTemplate.gradle — Unity 2022.3 can't build with 2.38.0.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CAS Hub] Couldn't pin error_prone_annotations: {e.Message}");
            }
#endif
        }

        private static int CreateMissing()
        {
            var missing = MissingNow();
            if (missing.Count == 0) return 0;

            Directory.CreateDirectory(DestDirAbs);
            var created = 0;
            foreach (var file in missing)
            {
                var dest = Path.Combine(DestDirAbs, file);
                string foundSrc = null;
                foreach (var cand in EditorSourceCandidates(file))
                    if (File.Exists(cand)) { foundSrc = cand; break; }
                try
                {
                    if (foundSrc != null)
                    {
                        File.Copy(foundSrc, dest, overwrite: false);
                        created++;
                    }
                    else if (string.Equals(file, "AndroidManifest.xml", System.StringComparison.OrdinalIgnoreCase))
                    {
                        // No Editor default manifest on any known path — write a minimal valid one so
                        // the Custom Main Manifest toggle still turns on.
                        File.WriteAllText(dest, MinimalMainManifest);
                        created++;
                    }
                    else
                    {
                        Debug.LogWarning($"[CAS Hub] No Editor default found for '{file}' (looked under " +
                            $"{AndroidPlayerDir}). Skipped — enable it manually in Player Settings if your build needs it.");
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[CAS Hub] Couldn't create '{file}': {e.Message}");
                }
            }
            if (created > 0)
            {
                AssetDatabase.Refresh();
                Debug.Log($"[CAS Hub] Enabled {created} Android build template(s) under Assets/Plugins/Android.");
            }
            return created;
        }
    }
}
