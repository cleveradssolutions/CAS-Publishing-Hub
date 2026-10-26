using System.Collections.Generic;
using System.Linq;
using System.Text;
using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Common;
using CAS.Publishing.Installer.Migrator;
using CAS.Publishing.Installer.Scanner;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Real per-component Install/Update actions for the wizard. Reuses the existing
    /// <see cref="MigrationPlanner"/> + <see cref="MigrationRunner"/> pipeline, scoped to a
    /// single component, behind a confirm dialog (it writes to Packages/manifest.json).
    /// </summary>
    internal static class WizardActions
    {
        /// <summary>Selects exactly one id at the recommended version target.</summary>
        private sealed class SingleSelection : ISelectionSet
        {
            private readonly string _id;
            public SingleSelection(string id) { _id = id; }
            public bool IsSelected(string id) => id == _id;
            public VersionTarget GetTarget(string id) => VersionTarget.Recommended;
        }

        /// <summary>
        /// Plans + applies the migration for a single component. Shows a confirm dialog first.
        /// Returns true when a migration was executed (success OR failure) so the caller should
        /// re-scan; false when cancelled or there was nothing to do.
        /// </summary>
        public static bool Apply(string componentId, string displayName)
        {
            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok)
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    "Catalog is unavailable — cannot apply. " +
                    (load.Error ?? "Make sure the metadata package is installed."), "OK");
                return false;
            }

            var report = ProjectScanner.Scan(load.Catalog);
            RegistryVersions.EnsureChecked(load.Catalog);

            // Compound legacy migration takes over BOTH entry points: installing an adapter or the
            // Firebase row while com.psv.firebase.base is still in the manifest must migrate the
            // whole family at once — a single-component plan would either duplicate the SDK or be
            // stripped by the partial-split backstop (the "Fix does nothing" loop).
            var manifest = ManifestProbe.Read();
            var splitGroup = LegacySplitRouting.FindGroupFor(report, manifest.Dependencies, componentId);
            if (splitGroup != null)
                return MigrateFirebaseLegacy(splitGroup, displayName, load.Catalog, manifest);

            var plan = MigrationPlanner.Plan(load.Catalog, report, new SingleSelection(componentId), InstallMethod.Upm, out var warnings);

            if (plan.Count == 0)
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    $"Nothing to apply for {displayName} — it may already be up to date, " +
                    "or the catalog has no version configured for it.", "OK");
                return false;
            }

            var references = ReferencesFor(load.Catalog, plan);
            if (!ConfirmPlan($"Apply changes for {displayName}?",
                    BuildSummary(displayName, plan, warnings, references), plan, warnings, references, "Apply"))
                return false;

            var result = new MigrationRunner().Apply(plan);
            if (result.Success)
            {
                Debug.Log($"[CAS Hub] Applied {result.ExecutedCount} action(s) for {displayName}. " +
                          "Unity will resolve packages now.");
                WarnAboutReferences(references);
            }
            else
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    $"Apply failed for {displayName}:\n• " + string.Join("\n• ", result.Failures) +
                    "\n\nNo backup is kept — use 'git status' / 'git restore .' to inspect or revert.", "OK");
            }

            return true; // re-scan regardless: reflect the real resulting state
        }

        /// <summary>
        /// Runs the compound legacy-Firebase migration (see <see cref="FirebaseMigrationPlan"/>):
        /// one confirm dialog listing removes + registry + installs, then a single
        /// MigrationRunner apply. Returns true when anything was attempted (caller re-scans).
        /// </summary>
        internal static bool MigrateFirebaseLegacy(
            Scanner.MigrationGroup group, string displayName,
            Catalog.PackageCatalog catalog, Scanner.ManifestData manifest)
        {
            System.Func<string, bool> embedded = id =>
                System.IO.Directory.Exists(System.IO.Path.Combine(
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "..")),
                    "Packages", id));

            var built = FirebaseMigrationPlan.Build(
                catalog, group, manifest.Dependencies,
                AssetInstallProbe.CollectLoadedIdentifiers(), embedded);

            if (built.Actions.Count == 0)
            {
                // Two distinct empty-plan causes: nothing to do (the legacy id already isn't in the
                // manifest — fine, no-op), or an aborted migration (the legacy id IS in the manifest,
                // but the catalog has no external record linking it — a stale catalog would strip
                // Firebase with no replacement, so FirebaseMigrationPlan aborts and warns instead).
                var message = built.Warnings.Count > 0
                    ? $"Cannot migrate {displayName}:\n• " + string.Join("\n• ", built.Warnings) +
                      "\n\nThe installed metadata catalog doesn't know how to replace this legacy " +
                      "package yet. Refresh the catalog (or update the metadata package) and try again."
                    : $"Nothing to migrate for {displayName} — {group.LegacyId} is not in manifest.json.";
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub", message, "OK");
                return false;
            }

            var warnings = new List<PlannerWarning>();
            foreach (var w in built.Warnings) warnings.Add(new PlannerWarning(group.LegacyId, w));

            var references = ReferencesFor(catalog, built.Actions);
            if (!ConfirmPlan($"Apply changes for {displayName}?",
                    BuildSummary(displayName, built.Actions, warnings, references), built.Actions, warnings,
                    references, "Apply"))
                return false;

            var result = new MigrationRunner().Apply(built.Actions);
            if (result.Success)
            {
                Debug.Log($"[CAS Hub] Migrated {group.LegacyId} → native Firebase + adapters " +
                          $"({result.ExecutedCount} action(s)). Unity will resolve packages now.");
                WarnAboutReferences(references);
            }
            else
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    $"Migration failed for {displayName}:\n• " + string.Join("\n• ", result.Failures) +
                    "\n\nNo backup is kept — use 'git status' / 'git restore .' to inspect or revert.", "OK");
            return true;
        }

        /// <summary>
        /// Switches a git-URL-installed external to the scoped-registry package: adds the registry
        /// scope(s) and AddPackage(id, recommendedVersion), which overwrites the git dependency in
        /// manifest.json. The normal planner treats a git install as already-current, so this is a
        /// dedicated path. Behind a confirm dialog.
        /// </summary>
        public static bool SwitchToUpm(string componentId, string displayName)
        {
            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok)
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    "Catalog is unavailable — cannot switch. " +
                    (load.Error ?? "Make sure the metadata package is installed."), "OK");
                return false;
            }

            var catalog = load.Catalog;
            ExternalRecord rec = null;
            if (catalog.External != null)
                foreach (var e in catalog.External)
                    if (e != null && e.Id == componentId) { rec = e; break; }
            if (rec == null)
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    $"{displayName} is not an external record in the catalog — cannot switch.", "OK");
                return false;
            }

            // A registryFirst record's recommendedVersion names its git tag — switch to the version
            // the registry actually has.
            RegistryVersions.EnsureChecked(catalog);
            var version = RegistryVersions.For(rec, catalog)
                          ?? (!string.IsNullOrEmpty(rec.RecommendedVersion) ? rec.RecommendedVersion : rec.MinVersion);
            if (string.IsNullOrEmpty(version))
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    $"No version configured for {displayName} in the catalog — cannot switch.", "OK");
                return false;
            }

            var plan = new List<MigrationAction>();
            if (rec.Scopes != null)
            {
                var url = ResolveRegistryUrl(catalog, rec);
                foreach (var scope in rec.Scopes)
                    if (!string.IsNullOrEmpty(scope))
                        plan.Add(new AddScopedRegistry(rec.Registry ?? string.Empty, url, scope));
            }
            // The git-installed package ALREADY has a dependencies entry (a git URL), so AddPackage
            // would be an idempotent no-op (it never overwrites an existing entry) — the git URL would
            // survive and the switch silently do nothing. UpdatePackageVersion overwrites the existing
            // value (git URL → registry version), which is what actually performs the switch.
            plan.Add(new UpdatePackageVersion(rec.Id, version));

            if (!EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    $"Switch {displayName} from a git URL to the registry version {version}?\n\n" +
                    "This replaces the git dependency in manifest.json with the scoped-registry package.",
                    "Switch", "Cancel"))
                return false;

            var result = new MigrationRunner().Apply(plan);
            if (result.Success)
                Debug.Log($"[CAS Hub] Switched {displayName} to UPM ({rec.Id}@{version}). " +
                          "Unity will resolve packages now.");
            else
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    $"Switch failed for {displayName}:\n• " + string.Join("\n• ", result.Failures), "OK");
            return true;
        }

        /// <summary>
        /// Removes a single component from <c>Packages/manifest.json</c> (one <see cref="RemovePackage"/>
        /// action) behind a confirm dialog. Doubles as a recovery path for a botched install. The
        /// scoped registry is left in place (harmless, and other CAS packages may still need it).
        /// Returns true when a removal was attempted (success OR failure) so the caller re-scans;
        /// false when cancelled.
        /// </summary>
        public static bool Remove(string componentId, string displayName)
        {
            var plan = new List<MigrationAction> { new RemovePackage(componentId) };

            var catalogLoad = CatalogLoader.Load();
            var references = catalogLoad.Status == CatalogLoadStatus.Ok
                ? ReferencesFor(catalogLoad.Catalog, plan)
                : null;
            var referenceNote = RetiredCodeProbe.Describe(references);

            if (!ConfirmPlan($"Remove {displayName}?",
                    $"Remove {displayName}?\n\n  • Remove {componentId}\n\n" +
                    (referenceNote != null ? "⚠ " + referenceNote + "\n\n" : "") +
                    "This writes directly to Packages/manifest.json. " +
                    "Make sure you have a clean git state — use 'git restore .' to undo if needed.",
                    plan, null, references, "Remove"))
                return false;

            var result = new MigrationRunner().Apply(plan);
            if (result.Success)
            {
                Debug.Log($"[CAS Hub] Removed {displayName} ({componentId}). " +
                          "Unity will resolve packages now.");
                WarnAboutReferences(references);
            }
            else
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    $"Remove failed for {displayName}:\n• " + string.Join("\n• ", result.Failures) +
                    "\n\nNo backup is kept — use 'git status' / 'git restore .' to inspect or revert.", "OK");
            }

            return true; // re-scan regardless: reflect the real resulting state
        }

        /// <summary>
        /// Migrates an external that was installed OUTSIDE UPM (e.g. via .unitypackage) to the UPM
        /// package: deletes the detected Assets/ folder(s) FIRST (guarded by git/path safety), and
        /// only on success registers the scope + adds the UPM package. Two steps on purpose — a
        /// single manifest-first plan would add the UPM package even if the delete is blocked
        /// (untracked files), leaving a duplicate. Returns true when anything was attempted.
        /// </summary>
        public static bool MigrateExternal(string componentId, string displayName)
        {
            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok)
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    "Catalog is unavailable — cannot migrate. " +
                    (load.Error ?? "Make sure the metadata package is installed."), "OK");
                return false;
            }

            var catalog = load.Catalog;
            ExternalRecord rec = null;
            if (catalog.External != null)
                foreach (var e in catalog.External)
                    if (e != null && e.Id == componentId) { rec = e; break; }
            if (rec == null)
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    $"{displayName} is not an external record in the catalog — cannot migrate.", "OK");
                return false;
            }

            var prepared = PrepareExternalMigration(catalog, rec);
            if (prepared.Error == ExternalMigration.NoVersion)
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    $"No version configured for {displayName} in the catalog — cannot migrate.", "OK");
                return false;
            }

            var deletePaths = prepared.DeletePaths;
            var sharedLeftovers = prepared.SharedLeftovers;
            var installLines = prepared.InstallLines;

            if (deletePaths.Count == 0)
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    $"{displayName} appears to be installed manually, but its known folders weren't " +
                    "found under Assets/ (non-standard layout). Remove the manual copy yourself, then " +
                    "use Install to add the UPM version.", "OK");
                return false;
            }

            // Custom, readable confirm window (UI Toolkit) instead of a cramped native text dialog. No
            // downgrade banner: version parity already pins the install to the on-disk version when
            // it's newer, so migrating can no longer downgrade — the install lines show the version.
            if (!MigrateConfirmWindow.Confirm(displayName, null, installLines, deletePaths, sharedLeftovers))
                return false;

            // STEP 1 — delete the manual copy FIRST. If git can't recover it (untracked/dirty), this
            // fails before any manifest change, so we never end up with both copies (a duplicate).
            if (!DeleteGuarded(displayName, deletePaths, "manifest.json was NOT changed."))
                return true; // re-scan: state unchanged but the user acted

            // STEP 2 — UPM install (registry scope + every resolved module). Safe now the copy is gone.
            var addPlan = prepared.AddPlan;

            var add = new MigrationRunner().Apply(addPlan);
            if (add.Success)
            {
                var installed = new StringBuilder();
                foreach (var line in installLines)
                {
                    if (installed.Length > 0) installed.Append(", ");
                    installed.Append(line);
                }
                Debug.Log($"[CAS Hub] Migrated {displayName} to UPM ({installed}). " +
                          "Unity will resolve packages now.");
                if (sharedLeftovers.Count > 0)
                    Debug.LogWarning($"[CAS Hub] {displayName}: files in the shared Assets/Plugins " +
                        "folder were NOT deleted — remove them by hand to avoid conflicts: Assets/" +
                        string.Join(", Assets/", sharedLeftovers));
            }
            else
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    $"The manual copy was removed, but the UPM install of {displayName} failed:\n• " +
                    string.Join("\n• ", add.Failures) +
                    "\n\nRun Install again from the Components tab.", "OK");
            }
            return true;
        }

        /// <summary>
        /// What moving a manual (Assets) copy of <c>rec</c> to UPM takes: the SDK files to delete
        /// first, the shared-folder leftovers to only report, and the manifest actions to apply once
        /// the copy is gone. Shared by the Hub's "Connect to Hub" and the agent-mode Install, so both
        /// remove exactly the same files.
        /// </summary>
        internal sealed class ExternalMigration
        {
            public const string NoVersion = "no-version";

            public string Error;
            public List<string> DeletePaths = new List<string>();
            public List<string> SharedLeftovers = new List<string>();
            public List<string> InstallLines = new List<string>();
            public List<MigrationAction> AddPlan = new List<MigrationAction>();
        }

        internal static ExternalMigration PrepareExternalMigration(PackageCatalog catalog, ExternalRecord rec)
        {
            var m = new ExternalMigration();

            RegistryVersions.EnsureChecked(catalog);
            var registryVersion = RegistryVersions.For(rec, catalog);
            var baseVersion = registryVersion
                              ?? (!string.IsNullOrEmpty(rec.RecommendedVersion) ? rec.RecommendedVersion : rec.MinVersion);
            if (string.IsNullOrEmpty(baseVersion))
            {
                m.Error = ExternalMigration.NoVersion;
                return m;
            }

            // Version parity: if the manual copy reports a version (via a catalog-declared static member)
            // NEWER than the catalog pin, migrate to THAT exact version — same version, just sourced from
            // our registry/git instead of Assets — so we never silently DOWNGRADE a project already on a
            // newer build (e.g. Firebase 13.6.0 on disk vs a 13.1.0 pin). The pin stays the floor.
            string onDiskVersion = null;
            if (!string.IsNullOrEmpty(rec.VersionType) && !string.IsNullOrEmpty(rec.VersionField))
                onDiskVersion = AssetInstallProbe.ReadStaticVersion(rec.VersionType, rec.VersionField);
            var effectiveVersion = ResolveEffectiveVersion(baseVersion, onDiskVersion);

            // Which UPM package(s) to install. A multi-module SDK (rec.Modules — e.g. Firebase ships
            // Analytics/RemoteConfig/Installations under one Assets/Firebase folder) installs EVERY
            // module whose markers are detected on disk, so deleting the shared folder doesn't drop
            // modules the project still uses. A single-package external installs just rec.Id.
            //
            // A record the catalog pins to its git source (installMethod: git) installs its git
            // chain instead — the registry does not carry that version (CAS 4.8.1-beta2 is on
            // neither openupm nor the private registry), so a registry AddPackage here left the
            // project with the manual copy deleted and a dependency Unity could not resolve.
            // A registryFirst record whose registry has caught up installs from the registry instead.
            var gitOnly = registryVersion == null
                          && MigrationPlanner.ResolveMethod(rec.InstallMethodName, InstallMethod.Upm) == InstallMethod.Git
                          && rec.Git?.Packages != null && rec.Git.Packages.Count > 0;
            var installs = gitOnly ? new List<AddPackage>() : ResolveInstallSet(rec, effectiveVersion);
            var gitInstalls = new List<AddGitPackage>();
            if (gitOnly)
                foreach (var p in rec.Git.Packages)
                    if (p != null && !string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(p.Url))
                        gitInstalls.Add(new AddGitPackage(p.Id, p.Spec));

            // Delete ONLY the SDK-owned folders the catalog declares, and only those that exist. No
            // file-walk: a folder can never be inferred from a stray user script, so user folders
            // (Assets/Scripts, …) can never be targeted.
            m.DeletePaths = AssetProbe.FindExisting(rec.AssetRoots);

            // The manual (.unitypackage) install also SCATTERS individual SDK files outside the owned
            // roots — e.g. Tenjin's BuildPostProcessor.cs / Dependencies.xml dropped into Assets/Editor.
            // Find them by file name + content signature (so a user's same-named file is never touched)
            // and fold them into the delete set, so they're shown in the confirm window and removed too
            // (a leftover BuildPostProcessor re-adds a stale [PostProcessBuild] and breaks the iOS build).
            foreach (var f in AssetInstallProbe.FindSignatureFiles(rec.LegacyAssetFiles, rec.AssetRoots))
                if (!m.DeletePaths.Contains(f)) m.DeletePaths.Add(f);

            // Precise native libs in the shared Assets/Plugins folder (e.g. libFirebaseCpp*.a) — matched
            // by exact name/glob from the catalog, so they're deleted with the rest (not left for the
            // user to prune by hand). Imprecise marker-only leftovers remain in SharedLeftovers below.
            foreach (var f in AssetInstallProbe.FindPluginFiles(rec.PluginFiles))
                if (!m.DeletePaths.Contains(f)) m.DeletePaths.Add(f);

            // Shared folder (Assets/Plugins) may hold this SDK's native libs ALONGSIDE other SDKs' —
            // never auto-deleted. Surface matching files so the user can prune them by hand.
            m.SharedLeftovers = AssetInstallProbe.FindLooseFiles(rec.AssetMarkers, "Plugins");

            foreach (var a in installs) m.InstallLines.Add($"{a.Id}@{a.Version}");
            foreach (var g in gitInstalls) m.InstallLines.Add($"{g.Id} (git: {g.Spec})");

            // A git install needs no registry scope — it would only register a source that lacks
            // the version.
            if (rec.Scopes != null && !gitOnly)
            {
                var url = ResolveRegistryUrl(catalog, rec);
                foreach (var scope in rec.Scopes)
                    if (!string.IsNullOrEmpty(scope))
                        m.AddPlan.Add(new AddScopedRegistry(rec.Registry ?? string.Empty, url, scope));
            }
            foreach (var a in installs) m.AddPlan.Add(a);
            foreach (var g in gitInstalls) m.AddPlan.Add(g);
            return m;
        }

        /// <summary>
        /// Deletes Assets-relative <paramref name="paths"/> behind the git guard. When git can't
        /// recover some of them (untracked/dirty) the user may opt into a permanent "Delete anyway";
        /// any other failure is final. Every outcome is reported in a dialog. True when the files are gone.
        /// </summary>
        internal static bool DeleteGuarded(string displayName, IReadOnlyList<string> paths, string unchangedNote)
        {
            var deletePlan = new List<MigrationAction>();
            foreach (var r in paths) deletePlan.Add(new BackupAndDeletePath(r));

            var del = new MigrationRunner().Apply(deletePlan);
            if (del.Success) return true;

            // Offer a permanent "Delete anyway" ONLY when the block was git's recoverability
            // guard (untracked/dirty). Other failures (PathSafety, IO) are not overridable here.
            var gitBlocked = del.Failures.Count > 0 &&
                             del.Failures.All(f => f.Contains(MigrationRunner.GitRefusalMarker));

            if (!gitBlocked || !EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    $"Some files of {displayName} aren't tracked by git, so they can't be recovered " +
                    "if removed:\n• " + string.Join("\n• ", del.Failures) +
                    "\n\nDelete them PERMANENTLY anyway? This cannot be undone.",
                    "Delete anyway", "Cancel"))
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    $"Couldn't remove the manual copy of {displayName}:\n• " +
                    string.Join("\n• ", del.Failures) +
                    "\n\nCommit those files to git first (so they're recoverable), or delete them " +
                    "manually, then try again. " + unchangedNote, "OK");
                return false;
            }

            // User opted in: retry the same deletes with the git guard overridden (PathSafety still on).
            var forcePlan = new List<MigrationAction>();
            foreach (var r in paths) forcePlan.Add(new BackupAndDeletePath(r, ignoreGitGuard: true));
            var forced = new MigrationRunner().Apply(forcePlan);
            if (forced.Success) return true;

            EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                $"Delete-anyway still failed for {displayName}:\n• " +
                string.Join("\n• ", forced.Failures) + "\n\n" + unchangedNote, "OK");
            return false;
        }

        /// <summary>
        /// Removes a manual (Assets) copy of a retired package — e.g. the Tenjin Unity plugin in
        /// Assets/Tenjin (QA 32). Confirm window first, then the guarded delete. True when anything
        /// was attempted (the caller re-scans).
        /// </summary>
        public static bool RemoveManualCopy(string displayName, List<string> paths)
        {
            if (paths == null || paths.Count == 0) return false;
            var plan = new List<MigrationAction>();
            foreach (var p in paths) plan.Add(new BackupAndDeletePath(p));
            var load = CatalogLoader.Load();
            var references = load.Status == CatalogLoadStatus.Ok
                ? ReferencesFor(load.Catalog, new List<MigrationAction>(),
                    new List<ComponentStatusProvider.RetiredPackage> { new ComponentStatusProvider.RetiredPackage { Id = displayName, Paths = paths } })
                : null;
            if (!PlanConfirmWindow.Confirm($"Remove the manual copy of {displayName}?", plan, null, references, "Delete"))
                return false;

            if (DeleteGuarded(displayName, paths, "Nothing was deleted."))
                Debug.Log($"[CAS Hub] Removed the manual copy of {displayName}: Assets/" +
                          string.Join(", Assets/", paths));
            return true;
        }

        /// <summary>
        /// Version parity: which version to actually install when migrating a manual (Assets) copy to UPM.
        /// When the on-disk SDK reports a version (via a catalog-declared static member) NEWER than the
        /// catalog pin, we install THAT exact version — just changing the source (Assets → our registry/git),
        /// never silently DOWNGRADING a project that's already on a newer build. Falls back to the catalog
        /// <paramref name="baseVersion"/> when the on-disk version is missing or not a valid semver, and is
        /// never below the pin (the pin stays the floor). Pure/testable.
        /// </summary>
        internal static string ResolveEffectiveVersion(string baseVersion, string onDiskVersion)
        {
            if (!string.IsNullOrEmpty(onDiskVersion)
                && SemVer.IsVersion(onDiskVersion)
                && SemVer.Compare(onDiskVersion, baseVersion) > 0)
                return onDiskVersion;
            return baseVersion;
        }

        /// <summary>
        /// Resolves the set of UPM packages to install when migrating <paramref name="rec"/>. For a
        /// multi-module external it returns one <see cref="AddPackage"/> per module whose markers are
        /// detected among the loaded types (at <c>module.RecommendedVersion ?? baseVersion</c>); for a
        /// single-package external it returns just <c>rec.Id@baseVersion</c>. Never empty — if a
        /// multi-module record somehow matches no module (the external was still flagged as present),
        /// it falls back to the primary id so a migration never installs nothing.
        /// </summary>
        internal static List<AddPackage> ResolveInstallSet(ExternalRecord rec, string baseVersion)
        {
            // Reflection over loaded types only when the record actually declares modules.
            var loaded = (rec.Modules != null && rec.Modules.Count > 0)
                ? (ICollection<string>)AssetInstallProbe.CollectLoadedIdentifiers()
                : null;
            return ResolveInstallSet(rec, baseVersion, loaded);
        }

        /// <summary>
        /// Pure core of <see cref="ResolveInstallSet(ExternalRecord,string)"/>: resolves the install
        /// set against an explicit <paramref name="loadedIdentifiers"/> set (no reflection), so it is
        /// unit-testable. See the public overload for the contract. Delegates to
        /// <see cref="ExternalInstallSet.Resolve"/> in the core assembly.
        /// </summary>
        internal static List<AddPackage> ResolveInstallSet(
            ExternalRecord rec, string baseVersion, ICollection<string> loadedIdentifiers)
            => ExternalInstallSet.Resolve(rec, baseVersion, loadedIdentifiers);

        private static string ResolveRegistryUrl(PackageCatalog catalog, ExternalRecord rec)
        {
            var key = rec.Registry;
            if (string.IsNullOrEmpty(key)) return string.Empty;
            if (catalog?.Registries != null && catalog.Registries.TryGetValue(key, out var url))
                return url ?? string.Empty;
            return key; // raw URL in the catalog
        }

        /// <summary>
        /// The game's scripts that still call into a package this plan removes (a retired package
        /// with catalog code markers). Shown in the confirm dialog and logged after the apply —
        /// the removal is still right, but nobody should learn about it from a compile error.
        /// </summary>
        /// <summary>
        /// The game's scripts that call into what this plan removes: retired packages dropped from
        /// the manifest, plus manual (Assets) copies being deleted — the copy's own files are not
        /// the game's and are left out of the search.
        /// </summary>
        internal static List<RetiredCodeProbe.Hit> ReferencesFor(PackageCatalog catalog, IReadOnlyList<MigrationAction> plan,
            IReadOnlyList<ComponentStatusProvider.RetiredPackage> manualCopies = null)
        {
            var removed = new List<string>();
            var exclude = new List<string>();
            foreach (var a in plan)
                if (a is RemovePackage rem && !string.IsNullOrEmpty(rem.Id)) removed.Add(rem.Id);
            if (manualCopies != null)
                foreach (var copy in manualCopies)
                {
                    if (!string.IsNullOrEmpty(copy?.Id) && !removed.Contains(copy.Id)) removed.Add(copy.Id);
                    if (copy?.Paths != null) exclude.AddRange(copy.Paths);
                }
            return removed.Count == 0
                ? new List<RetiredCodeProbe.Hit>()
                : RetiredCodeProbe.FindReferences(catalog, removed, excludeAssetPaths: exclude);
        }

        /// <summary>
        /// The confirm step for a manifest plan. With scripts to update it is the scrollable
        /// <see cref="PlanConfirmWindow"/> — the native dialog truncated that list (QA 87); without
        /// them the short native dialog stays.
        /// </summary>
        internal static bool ConfirmPlan(string title, string nativeSummary, IReadOnlyList<MigrationAction> plan,
            IReadOnlyList<PlannerWarning> warnings, IReadOnlyList<RetiredCodeProbe.Hit> references, string okText)
        {
            if (references != null && references.Count > 0)
                return PlanConfirmWindow.Confirm(title, plan, warnings, references, okText);
            return EditorUtility.DisplayDialog("CAS.AI Publishing Hub", nativeSummary, okText, "Cancel");
        }

        private static void WarnAboutReferences(IReadOnlyList<RetiredCodeProbe.Hit> references)
        {
            var note = RetiredCodeProbe.Describe(references);
            if (note == null) return;
            Debug.LogWarning("[CAS Hub] " + note + "\nReplace that code with CAS: it initializes Tenjin " +
                             "itself and reports purchases via CASHub.ReportPurchase; the SDK key lives in " +
                             TenjinKeySettings.KeyHome + " (any key the wrapper stored is carried over).");
        }

        private static string BuildSummary(
            string displayName,
            IReadOnlyList<MigrationAction> plan,
            IReadOnlyList<PlannerWarning> warnings,
            IReadOnlyList<RetiredCodeProbe.Hit> references = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Apply changes for {displayName}?");
            sb.AppendLine();

            foreach (var a in plan)
            {
                switch (a)
                {
                    case AddPackage add:           sb.AppendLine($"  • Add {add.Id}@{add.Version}"); break;
                    case AddGitPackage git:        sb.AppendLine($"  • Add {git.Id} (git: {git.Spec})"); break;
                    case UpdatePackageVersion upd: sb.AppendLine($"  • Update {upd.Id} → {upd.Version}"); break;
                    case RemovePackage rem:        sb.AppendLine($"  • Remove {rem.Id}"); break;
                    case AddScopedRegistry reg:    sb.AppendLine($"  • Register scope {reg.Scope} ({reg.Url})"); break;
                    case AddScopeToRegistry sc:    sb.AppendLine($"  • Add scope {sc.Scope} to {sc.Url}"); break;
                    case BackupAndDeletePath del:  sb.AppendLine($"  • Delete Assets/{del.RelativePath}"); break;
                }
            }

            if (warnings != null && warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Warnings:");
                foreach (var w in warnings)
                    sb.AppendLine($"  ⚠ {w.Message}");
            }

            var note = RetiredCodeProbe.Describe(references);
            if (note != null)
            {
                sb.AppendLine();
                sb.AppendLine("Code to update afterwards:");
                foreach (var line in note.Split('\n'))
                    sb.AppendLine($"  ⚠ {line}");
                sb.AppendLine("  Replace it with CAS — it initializes Tenjin itself; the key goes to " + TenjinKeySettings.KeyHome + ".");
            }

            sb.AppendLine();
            sb.Append("This writes directly to Packages/manifest.json. " +
                      "Make sure you have a clean git state — use 'git restore .' to undo if needed.");
            return sb.ToString();
        }
    }
}
