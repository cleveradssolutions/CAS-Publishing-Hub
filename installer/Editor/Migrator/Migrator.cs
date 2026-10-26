using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using CAS.Publishing.Installer.Common;

namespace CAS.Publishing.Installer.Migrator
{
    /// <summary>
    /// Executes a migration plan produced by <see cref="MigrationPlanner"/>: applies manifest
    /// mutations atomically (via <see cref="ManifestWriter"/>/<see cref="ManifestIO"/>) BEFORE
    /// deleting any legacy assets, and refuses to delete a path that escapes Assets/ or that
    /// git cannot recover (<see cref="PathSafety"/>, <see cref="GitGuard"/>).
    ///
    /// No built-in backup: recovery for deleted assets is delegated to the client's git, which
    /// is why <see cref="GitGuard"/> blocks deletion of untracked/ignored/dirty paths.
    /// </summary>
    public sealed class MigrationRunner
    {
        private const string LogPrefix = "[CAS Hub]";

        /// <summary>
        /// Marker embedded in a delete failure message when the git recoverability guard blocked the
        /// delete (untracked/dirty). Consumers detect the "offer Delete anyway" case by testing
        /// failures for this substring — share the constant so a copy reword can't silently break it.
        /// </summary>
        public const string GitRefusalMarker = "refusing to delete";

        private readonly string _projectRoot;
        private readonly string _manifestPath;

        /// <summary>
        /// Constructs a <see cref="MigrationRunner"/> targeting the Unity project at
        /// <paramref name="projectRoot"/>.
        /// </summary>
        /// <param name="projectRoot">
        /// Absolute path to the Unity project root (the directory that contains
        /// <c>Assets/</c>, <c>Packages/</c>, and <c>Library/</c>).
        /// When null the constructor derives it from <see cref="Application.dataPath"/>.
        /// </param>
        public MigrationRunner(string projectRoot = null)
        {
            _projectRoot  = projectRoot ?? DeriveProjectRoot();
            _manifestPath = Path.Combine(_projectRoot, "Packages", "manifest.json");
        }

        /// <summary>
        /// Executes the migration plan. Order is deliberate and safety-critical:
        /// (1) apply manifest mutations FIRST via the atomic <see cref="ManifestWriter"/> —
        /// if that fails, nothing on disk has been deleted; (2) only then delete legacy asset
        /// paths, each guarded by <see cref="PathSafety"/> (no escaping Assets/) and
        /// <see cref="GitGuard"/> (refuse what git can't recover). A delete failure leaves the
        /// project in a re-scannable Conflict state, never a void.
        /// </summary>
        public ApplyResult Apply(IReadOnlyList<MigrationAction> plan)
        {
            if (plan == null || plan.Count == 0)
                return new ApplyResult(true, 0, Array.Empty<string>());

            var failures = new List<string>();
            var executedCount = 0;

            if (!File.Exists(_manifestPath))
                return Fail(failures, $"manifest.json not found at '{_manifestPath}'.");

            // ── 1. Manifest mutations FIRST (atomic; aborts before any delete on failure) ──
            var manifestMutations = plan.Where(a => !(a is BackupAndDeletePath)).ToList();
            if (manifestMutations.Count > 0)
            {
                try
                {
                    ManifestWriter.ApplyActions(_manifestPath, manifestMutations);
                    executedCount += manifestMutations.Count;
                    // An installer-driven manifest change happened → a UPM domain reload will follow.
                    // Signal it so the wizard auto-reopens (to Components) even after first-run intro.
                    CAS.Publishing.Installer.Common.InstallReloadSignal.MarkPending();
                }
                catch (Exception e)
                {
                    failures.Add($"ManifestWriter.ApplyActions failed: {e.Message}");
                    return new ApplyResult(false, executedCount, failures);
                }
            }

            // ── 2. Delete legacy asset paths LAST (irreversible) ──
            var assetsRoot = Application.dataPath;
            foreach (var action in plan.OfType<BackupAndDeletePath>())
            {
                if (!PathSafety.TryResolveContained(assetsRoot, action.RelativePath, out var absolutePath, out var pathError))
                {
                    failures.Add($"DeletePath({action.RelativePath}): {pathError}");
                    return new ApplyResult(false, executedCount, failures);
                }

                // PathSafety (above) is always enforced. The git recoverability guard can be
                // explicitly overridden via IgnoreGitGuard (the user's "Delete anyway" choice).
                if (!action.IgnoreGitGuard && !GitGuard.IsTrackedAndClean(absolutePath, out var gitReason))
                {
                    failures.Add($"DeletePath({action.RelativePath}): {GitRefusalMarker} — {gitReason}");
                    return new ApplyResult(false, executedCount, failures);
                }

                if (!DeletePath(absolutePath, out var deleteError))
                {
                    failures.Add($"DeletePath({action.RelativePath}): {deleteError}");
                    return new ApplyResult(false, executedCount, failures);
                }
                executedCount++;
            }

            // ── 3. Re-resolve so UPM pulls the new/updated packages immediately ──
            // Writing manifest.json on disk does not always trigger a resolve until the editor
            // is refocused/restarted; force it so the install completes without a restart.
            if (manifestMutations.Count > 0)
            {
                try
                {
                    UnityEditor.PackageManager.Client.Resolve();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"{LogPrefix} Client.Resolve() after apply failed: {e.Message}. " +
                                     "Packages may need an editor refocus to resolve.");
                }
            }

            Debug.Log($"{LogPrefix} Apply complete. {executedCount} action(s) executed.");
            return new ApplyResult(true, executedCount, Array.Empty<string>());
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static ApplyResult Fail(List<string> failures, string message)
        {
            failures.Add(message);
            Debug.LogWarning($"{LogPrefix} Apply failed: {message}");
            return new ApplyResult(false, 0, failures);
        }

        /// <summary>
        /// Deletes <paramref name="absolutePath"/> (file or directory).
        /// Uses <see cref="AssetDatabase.DeleteAsset"/> for paths under Assets/ so that
        /// Unity cleans up <c>.meta</c> files and import state.
        /// Falls back to plain <see cref="File"/>/<see cref="Directory"/> for other paths.
        /// </summary>
        private static bool DeletePath(string absolutePath, out string error)
        {
            error = null;

            if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
                return true; // Already gone — idempotent success.

            var normalAssets = NormaliseSeparators(Application.dataPath);
            var normalTarget = NormaliseSeparators(absolutePath);

            if (normalTarget.StartsWith(normalAssets, StringComparison.OrdinalIgnoreCase))
            {
                // Slice using the already-normalised paths so casing/separator differences
                // between PathSafety's canonical path and Application.dataPath can't corrupt the slice.
                var assetPath = "Assets" + normalTarget.Substring(normalAssets.Length);

                if (!AssetDatabase.DeleteAsset(assetPath))
                {
                    error = $"AssetDatabase.DeleteAsset('{assetPath}') returned false.";
                    return false;
                }
                return true;
            }

            try
            {
                if (File.Exists(absolutePath))
                    File.Delete(absolutePath);
                else if (Directory.Exists(absolutePath))
                    Directory.Delete(absolutePath, recursive: true);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static string DeriveProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
        }

        private static string NormaliseSeparators(string path)
        {
            return path.Replace('\\', '/');
        }
    }
}
