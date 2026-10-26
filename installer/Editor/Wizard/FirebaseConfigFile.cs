using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Validates and copies a Firebase config file (google-services.json / GoogleService-Info.plist)
    /// picked by the user into the project's Assets/ folder, for the Configure screen's Firebase panel
    /// "Locate file…" button. Never throws — failures come back as an error string.
    /// </summary>
    internal static class FirebaseConfigFile
    {
        private const string AndroidFileName = "google-services.json";
        private const string IosFileName = "GoogleService-Info.plist";

        /// <summary>
        /// Pure check of the file NAME (case-insensitive, name only — no existence/content check).
        /// Null = ok; otherwise an error string naming the expected files.
        /// </summary>
        public static string Validate(string path)
        {
            if (string.IsNullOrEmpty(path)) return "No file selected.";

            var name = Path.GetFileName(path);
            if (string.Equals(name, AndroidFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, IosFileName, StringComparison.OrdinalIgnoreCase))
                return null;

            return $"Expected \"{AndroidFileName}\" or \"{IosFileName}\", got \"{name}\".";
        }

        /// <summary>
        /// Validates <paramref name="sourcePath"/>'s file name, then copies it to the root of
        /// <paramref name="assetsDir"/> (project-relative, e.g. "Assets") and imports it. Never
        /// overwrites — if the destination already exists this returns an error instead. Null = ok.
        /// The Validate call lives INSIDE the try below (not before it) — Path.GetFileName can throw
        /// on invalid path characters, and this method's contract is to never throw, only return an
        /// error string.
        /// </summary>
        public static string ValidateAndCopy(string sourcePath, string assetsDir)
        {
            try
            {
                var err = Validate(sourcePath);
                if (err != null) return err;

                var fileName = Path.GetFileName(sourcePath);
                var dir = string.IsNullOrEmpty(assetsDir) ? "Assets" : assetsDir.TrimEnd('/', '\\');
                var destRelative = dir + "/" + fileName;

                var projectRoot = Directory.GetParent(Application.dataPath).FullName;
                var destFull = Path.Combine(projectRoot, destRelative.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(destFull))
                    return $"{fileName} already exists in {dir}/ — remove it first if you want to replace it.";

                File.Copy(sourcePath, destFull, overwrite: false);
                AssetDatabase.ImportAsset(destRelative);
                return null;
            }
            catch (Exception e)
            {
                return $"Could not copy the file: {e.Message}";
            }
        }

        /// <summary>
        /// Pulls the application id out of the config contents: <c>package_name</c> for
        /// google-services.json, <c>BUNDLE_ID</c> for GoogleService-Info.plist. A regex rather than a
        /// real parser: exactly one scalar is needed, and dragging a JSON/plist parser into the editor
        /// assembly for it is not worth it. Nothing found or an unexpected shape yields null.
        /// </summary>
        public static string ParseApplicationId(string fileName, string fileContents)
        {
            var ids = ParseApplicationIds(fileName, fileContents);
            return ids.Length == 0 ? null : ids[0];
        }

        /// <summary>
        /// Every application id in the config. One Firebase project can serve several apps — then
        /// google-services.json carries several clients, each with its own <c>package_name</c>.
        /// Checking only the first would complain about a perfectly correct file whenever the game
        /// isn't its first client. An empty array means nothing was found or the shape is unexpected.
        /// </summary>
        public static string[] ParseApplicationIds(string fileName, string fileContents)
        {
            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(fileContents))
                return new string[0];

            try
            {
                string pattern = null;
                if (string.Equals(fileName, AndroidFileName, StringComparison.OrdinalIgnoreCase))
                    pattern = "\"package_name\"\\s*:\\s*\"([^\"]+)\"";
                else if (string.Equals(fileName, IosFileName, StringComparison.OrdinalIgnoreCase))
                    pattern = "<key>\\s*BUNDLE_ID\\s*</key>\\s*<string>([^<]*)</string>";

                if (pattern == null) return new string[0];

                var matches = Regex.Matches(fileContents, pattern);
                var ids = new string[matches.Count];
                for (var i = 0; i < matches.Count; i++)
                    ids[i] = matches[i].Groups[1].Value;
                return ids;
            }
            catch { /* this file's contract is to never throw */ }

            return new string[0];
        }

        /// <summary>
        /// The warning for a specific file: null when at least one of its ids matches the project's,
        /// or when there is nothing to compare. This is the check the screen calls;
        /// <see cref="IdMismatchWarning"/> is its pure core for a single pair.
        /// </summary>
        public static string MismatchWarningFor(string fileName, string fileContents, string projectId)
        {
            var ids = ParseApplicationIds(fileName, fileContents);
            if (ids.Length == 0 || string.IsNullOrEmpty(projectId)) return null;

            foreach (var id in ids)
                if (string.Equals(id, projectId, StringComparison.Ordinal)) return null;

            return IdMismatchWarning(ids[0], projectId);
        }

        /// <summary>
        /// The warning text when the config belongs to a different application. Null means no
        /// mismatch, or nothing to compare against. A warning, not a veto: unusual setups exist and
        /// the decision stays with the user.
        /// </summary>
        public static string IdMismatchWarning(string fileId, string projectId)
        {
            if (string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(projectId)) return null;
            if (string.Equals(fileId, projectId, StringComparison.Ordinal)) return null;
            return $"This config is for \"{fileId}\", but the project builds \"{projectId}\". " +
                   "Use it anyway only if that is intentional.";
        }
    }
}
