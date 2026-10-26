using System.Collections.Generic;
using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Common;
using CAS.Publishing.Installer.Scanner;

namespace CAS.Publishing.Installer.Migrator
{
    // ── Version target ────────────────────────────────────────────────────────

    /// <summary>
    /// Which version the user wants to install for a given package.
    /// <see cref="Recommended"/> resolves to <c>RecommendedVersion ?? MinVersion</c>;
    /// <see cref="Min"/> resolves to <c>MinVersion ?? RecommendedVersion</c>.
    /// </summary>
    public enum VersionTarget
    {
        /// <summary>Use the recommended version (default).</summary>
        Recommended,

        /// <summary>Use the minimum acceptable version.</summary>
        Min,
    }

    // ── Selection abstraction ─────────────────────────────────────────────────

    /// <summary>
    /// Determines whether a given package/legacy id is currently selected in the UI
    /// and which version target the user has chosen for that id.
    /// The planner is pure and does not reference Unity or EditorPrefs — the caller
    /// adapts whatever selection representation it holds into this interface.
    /// </summary>
    public interface ISelectionSet
    {
        /// <summary>Returns true when <paramref name="id"/> is part of the active selection.</summary>
        bool IsSelected(string id);

        /// <summary>
        /// Returns the chosen <see cref="VersionTarget"/> for <paramref name="id"/>.
        /// For ids not in the selection set, returns <see cref="VersionTarget.Recommended"/>
        /// (the safe default). The planner calls this only for selected ids — calling for
        /// unselected ids is allowed and must also return <see cref="VersionTarget.Recommended"/>.
        /// </summary>
        VersionTarget GetTarget(string id);
    }

    // ── Planner ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Pure, deterministic migration planner.  Given a catalog, a scan report, and
    /// a selection, it returns an ordered list of <see cref="MigrationAction"/>s and
    /// an optional list of <see cref="PlannerWarning"/>s for cases where an action
    /// cannot be generated (e.g. external records missing a version field).
    ///
    /// Guarantees:
    /// <list type="bullet">
    ///   <item>Same inputs → same outputs (no random ordering, no time-based keys).</item>
    ///   <item>No I/O, no side effects.</item>
    ///   <item>Null or empty inputs return empty output lists, never throw.</item>
    ///   <item>Action ordering: BackupAndDeletePath → RemovePackage →
    ///         AddScopedRegistry / AddScopeToRegistry → AddPackage / UpdatePackageVersion.</item>
    ///   <item>Duplicate <see cref="RemovePackage"/> entries (from split-replace) are deduplicated.</item>
    /// </list>
    /// </summary>
    public static class MigrationPlanner
    {
        /// <summary>
        /// Builds a migration plan for the currently-selected items.
        /// </summary>
        /// <param name="catalog">Loaded catalog.  Null → empty plan.</param>
        /// <param name="report">Latest scan report.  Null → empty plan.</param>
        /// <param name="selection">UI selection set.  Null → empty plan.</param>
        /// <param name="warnings">
        /// Output: non-fatal warnings for selected items that could not produce actions.
        /// Never null; may be empty.
        /// </param>
        /// <returns>Ordered, deduplicated list of migration actions. Never null.</returns>
        public static IReadOnlyList<MigrationAction> Plan(
            PackageCatalog catalog,
            ScanReport report,
            ISelectionSet selection,
            InstallMethod method,
            out IReadOnlyList<PlannerWarning> warnings)
        {
            var warningList = new List<PlannerWarning>();

            if (catalog == null || report == null || selection == null)
            {
                warnings = warningList;
                return new List<MigrationAction>();
            }

            // ── Per-bucket accumulators (applied in order at the end) ──────────
            var backups   = new List<BackupAndDeletePath>();
            var removes   = new List<RemovePackage>();
            var regAdds   = new List<MigrationAction>();   // AddScopedRegistry | AddScopeToRegistry
            var pkgAdds   = new List<MigrationAction>();   // AddPackage | UpdatePackageVersion
            var gitAdds = new List<MigrationAction>(); // AddGitPackage entries (git method)

            // Deduplication: track which ids have already produced a RemovePackage.
            var removedIds = new HashSet<string>();

            // ── Build catalog lookup maps ─────────────────────────────────────
            // Map packageId → PackageRecord for quick scan-result → record lookup.
            var packageRecordById = new Dictionary<string, PackageRecord>();
            if (catalog.Packages != null)
                foreach (var rec in catalog.Packages)
                    if (rec != null && rec.Id != null)
                        packageRecordById[rec.Id] = rec;

            // Map packageId → ExternalRecord.
            var externalRecordById = new Dictionary<string, ExternalRecord>();
            if (catalog.External != null)
                foreach (var rec in catalog.External)
                    if (rec != null && rec.Id != null)
                        externalRecordById[rec.Id] = rec;

            // ── Process selected PackageScanResults ───────────────────────────
            if (report.Packages != null)
            {
                foreach (var result in report.Packages)
                {
                    if (result == null) continue;
                    if (!selection.IsSelected(result.Id)) continue;

                    packageRecordById.TryGetValue(result.Id, out var record);
                    if (ResolveMethod(record?.InstallMethodName, method) == InstallMethod.Git &&
                        TryPlanGit(record?.Git, gitAdds, warningList, result.Id,
                            warnOnFallback: result.State != PackageState.UpmCurrent))
                        continue; // git chain emitted; skip UPM planning for this component
                    var target = selection.GetTarget(result.Id);
                    PlanForPackage(result, record, catalog, target, backups, removes, regAdds, pkgAdds, removedIds);
                }
            }

            // ── Process selected ExternalScanResults ──────────────────────────
            if (report.External != null)
            {
                foreach (var result in report.External)
                {
                    if (result == null) continue;
                    if (!selection.IsSelected(result.Id)) continue;

                    externalRecordById.TryGetValue(result.Id, out var record);

                    // A manual copy or a legacy wrapper already provides the SDK. The git chain must
                    // not bypass that: adding the package on top of a manual copy left Unity ignoring
                    // the package over GUID conflicts while the old copy kept running. Moving off it
                    // is the explicit migration (WizardActions.MigrateExternal), never a plain install.
                    if (result.State == ExternalState.InstalledOutsideUpm ||
                        result.State == ExternalState.InstalledLegacy)
                        continue;

                    // registryFirst: a registry version at or above minVersion beats the git source.
                    var registryVersion = RegistryVersions.For(record, catalog);
                    if (registryVersion == null &&
                        ResolveMethod(record?.InstallMethodName, method) == InstallMethod.Git &&
                        TryPlanGit(record?.Git, gitAdds, warningList, result.Id,
                            warnOnFallback: result.State != ExternalState.UpmCurrent))
                        continue; // git chain emitted; skip UPM planning for this component
                    var target = selection.GetTarget(result.Id);
                    PlanForExternal(result, record, catalog, target, regAdds, pkgAdds, warningList, registryVersion);
                }
            }

            // ── Process selected UninstallScanResults ─────────────────────────
            if (report.Uninstalls != null)
            {
                foreach (var result in report.Uninstalls)
                {
                    if (result == null) continue;
                    // UninstallScanResults use LegacyNpmId as selection key.
                    if (!selection.IsSelected(result.LegacyNpmId)) continue;

                    if (result.State == UninstallState.InstalledNeedsRemoval)
                        AddRemove(result.LegacyNpmId, removes, removedIds);
                }
            }

            // ── Detect partial split migrations ───────────────────────────────
            // For each RemovePackage whose id is a legacyId in a split group, check
            // whether all sibling replacements are also selected. Emit PartialSplitWarning
            // when one or more siblings are unselected so the UI can show a confirm dialog.
            if (report.SplitGroups != null && removes.Count > 0)
            {
                var removedLegacyIds = new HashSet<string>();
                foreach (var r in removes)
                    removedLegacyIds.Add(r.Id);

                foreach (var group in report.SplitGroups)
                {
                    if (!removedLegacyIds.Contains(group.LegacyId)) continue;

                    var selectedSiblings   = new List<string>();
                    var unselectedSiblings = new List<string>();

                    foreach (var pkgId in group.PackageIds)
                    {
                        if (selection.IsSelected(pkgId))
                            selectedSiblings.Add(pkgId);
                        else
                            unselectedSiblings.Add(pkgId);
                    }

                    if (unselectedSiblings.Count > 0)
                    {
                        warningList.Add(new PartialSplitWarning(group.LegacyId, selectedSiblings, unselectedSiblings));

                        // Safety backstop: never strip a legacy package whose full set of
                        // replacements isn't selected. Dropping the RemovePackage leaves the
                        // project in a re-scannable Conflict state rather than losing the
                        // unselected replacement's functionality. (UI checkbox-linking in
                        // Plan 3 prevents partial selection at the source; this guards the
                        // planner's public API against any caller that bypasses the UI.)
                        // Safe: a LegacyId shared by a split group (≥2 package records) is never
                        // also a standalone UninstallScanResult target, so this only drops the
                        // split-derived remove, not an unrelated uninstall.
                        removes.RemoveAll(r => r.Id == group.LegacyId);
                    }
                }
            }

            // ── Assemble in required order ────────────────────────────────────
            var actions = new List<MigrationAction>(
                backups.Count + removes.Count + regAdds.Count + pkgAdds.Count + gitAdds.Count);

            actions.AddRange(backups);
            actions.AddRange(removes);
            actions.AddRange(regAdds);
            actions.AddRange(pkgAdds);
            actions.AddRange(gitAdds);

            warnings = warningList;
            return actions;
        }

        // ── Package planning ─────────────────────────────────────────────────

        private static void PlanForPackage(
            PackageScanResult result,
            PackageRecord record,
            PackageCatalog catalog,
            VersionTarget target,
            List<BackupAndDeletePath> backups,
            List<RemovePackage> removes,
            List<MigrationAction> regAdds,
            List<MigrationAction> pkgAdds,
            HashSet<string> removedIds)
        {
            var targetVersion = record != null
                ? ResolveVersion(record.RecommendedVersion, record.MinVersion, target)
                : null;

            switch (result.State)
            {
                case PackageState.NotInstalled:
                    if (targetVersion != null)
                    {
                        EmitPackageRegistry(record, catalog, regAdds);
                        pkgAdds.Add(new AddPackage(result.Id, targetVersion));
                    }
                    // If no version in catalog, nothing useful can be done — silently skip
                    // (record-less packages are unusual; a warning is skipped here as the
                    //  catalog author is responsible for version fields on PackageRecord).
                    break;

                case PackageState.UpmCurrent:
                    // Already up to date — no action.
                    break;

                case PackageState.UpmOutdated:
                case PackageState.UpmBelowMin:
                    if (targetVersion != null)
                    {
                        EmitPackageRegistry(record, catalog, regAdds);
                        pkgAdds.Add(new UpdatePackageVersion(result.Id, targetVersion));
                    }
                    break;

                case PackageState.LegacyUpm:
                    // Remove the legacy id, add the canonical id.
                    if (!string.IsNullOrEmpty(result.DetectedLegacyNpmId))
                        AddRemove(result.DetectedLegacyNpmId, removes, removedIds);
                    if (targetVersion != null)
                    {
                        EmitPackageRegistry(record, catalog, regAdds);
                        pkgAdds.Add(new AddPackage(result.Id, targetVersion));
                    }
                    break;

                case PackageState.LegacyAssets:
                    // Backup and delete each legacy path, then add the canonical package.
                    if (result.DetectedLegacyPaths != null)
                        foreach (var path in result.DetectedLegacyPaths)
                            if (!string.IsNullOrEmpty(path))
                                backups.Add(new BackupAndDeletePath(path));
                    if (targetVersion != null)
                    {
                        EmitPackageRegistry(record, catalog, regAdds);
                        pkgAdds.Add(new AddPackage(result.Id, targetVersion));
                    }
                    break;

                case PackageState.Conflict:
                    // Backup and delete legacy asset paths, remove legacy npm id,
                    // then add the canonical package.
                    if (result.DetectedLegacyPaths != null)
                        foreach (var path in result.DetectedLegacyPaths)
                            if (!string.IsNullOrEmpty(path))
                                backups.Add(new BackupAndDeletePath(path));
                    if (!string.IsNullOrEmpty(result.DetectedLegacyNpmId))
                        AddRemove(result.DetectedLegacyNpmId, removes, removedIds);
                    if (targetVersion != null)
                    {
                        EmitPackageRegistry(record, catalog, regAdds);
                        pkgAdds.Add(new AddPackage(result.Id, targetVersion));
                    }
                    break;

                case PackageState.ScopeMissing:
                    // Dependency already in manifest but unresolvable — Fix adds only the scope.
                    EmitPackageRegistry(record, catalog, regAdds);
                    break;
            }
        }

        // ── External planning ─────────────────────────────────────────────────

        private static void PlanForExternal(
            ExternalScanResult result,
            ExternalRecord record,
            PackageCatalog catalog,
            VersionTarget target,
            List<MigrationAction> regAdds,
            List<MigrationAction> pkgAdds,
            List<PlannerWarning> warnings,
            string registryVersion = null)
        {
            switch (result.State)
            {
                case ExternalState.UpmCurrent:
                    // Nothing to do — unless a registryFirst record sits below its minVersion (a git
                    // tag the git source has since moved past, or an old registry version): the
                    // registry has a version that qualifies, so move it there.
                    if (registryVersion != null && BelowMin(result.DetectedVersion, record?.MinVersion))
                    {
                        AddScopes(record, catalog, regAdds);
                        pkgAdds.Add(new UpdatePackageVersion(result.Id, registryVersion));
                    }
                    break;

                case ExternalState.InstalledOutsideUpm:
                    // A non-UPM (.unitypackage) copy is already present — adding the UPM package
                    // would duplicate it. Skip. Migration to UPM is an explicit, separate action
                    // (WizardActions.MigrateExternal), never part of an Install/Express plan.
                    break;

                case ExternalState.InstalledLegacy:
                    // A legacy package (e.g. com.psv.tenjin) already provides this SDK. Leave it alone:
                    // installing the canonical id would duplicate the SDK, and the legacy wrapper's
                    // namespace may differ. Moving to the canonical split is a separate, deliberate step.
                    break;

                case ExternalState.NotInstalled:
                    // Ensure the scoped registry is registered, then add the package.
                    if (record?.Scopes != null)
                    {
                        var registryUrl = ResolveRegistryUrl(record, catalog);
                        var registryName = record.Registry ?? string.Empty;

                        foreach (var scope in record.Scopes)
                        {
                            if (string.IsNullOrEmpty(scope)) continue;
                            // For a completely uninstalled external, assume registry URL
                            // is not yet present and emit AddScopedRegistry.
                            // Phase 4b ManifestWriter will handle idempotency (if the URL
                            // already exists it merges the scope rather than duplicating).
                            regAdds.Add(new AddScopedRegistry(registryName, registryUrl, scope));
                        }
                    }

                    // A registryFirst record installs the registry's version — its recommendedVersion
                    // names the git tag, which the registry doesn't carry.
                    var addVersion = registryVersion ?? (record != null
                        ? ResolveVersion(record.RecommendedVersion, record.MinVersion, target)
                        : null);

                    if (!string.IsNullOrEmpty(addVersion))
                    {
                        pkgAdds.Add(new AddPackage(result.Id, addVersion));
                    }
                    else
                    {
                        // No version configured — emit a warning instead of an AddPackage.
                        warnings.Add(new PlannerWarning(result.Id,
                            $"External record '{result.Id}' has no recommendedVersion or minVersion " +
                            "configured in the catalog. AddPackage action skipped."));
                    }
                    break;

                case ExternalState.ScopeMissing:
                    // Package is in manifest but required scopes are not registered.
                    // Only registry actions needed — no package add/update.
                    if (record?.Scopes != null)
                    {
                        var registryUrl = ResolveRegistryUrl(record, catalog);
                        foreach (var scope in record.Scopes)
                        {
                            if (string.IsNullOrEmpty(scope)) continue;
                            // Registry URL is known to exist (package resolved somehow),
                            // so append the missing scope.
                            regAdds.Add(new AddScopeToRegistry(registryUrl, scope));
                        }
                    }
                    break;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Ensures the scoped registry for a catalog package before its AddPackage/Update action.
        /// Scope comes from <c>record.Scopes</c>, defaulting to the package id itself (an exact-id
        /// scope is always correct and never over-captures). No registry key configured → no action
        /// (git installs and authoring gaps must not crash planning). ManifestWriter merges by URL,
        /// so re-emitting for an existing registry block is idempotent.
        /// </summary>
        internal static void EmitPackageRegistry(
            PackageRecord record, PackageCatalog catalog, List<MigrationAction> regAdds)
        {
            if (record == null || string.IsNullOrEmpty(record.Registry)) return;

            string url = null;
            if (catalog?.Registries != null && catalog.Registries.TryGetValue(record.Registry, out var mapped))
                url = mapped;
            if (string.IsNullOrEmpty(url)) url = record.Registry.Contains("://") ? record.Registry : null;
            if (string.IsNullOrEmpty(url)) return;

            var scopes = (record.Scopes != null && record.Scopes.Count > 0)
                ? (IEnumerable<string>)record.Scopes
                : new[] { record.Id };
            foreach (var scope in scopes)
                if (!string.IsNullOrEmpty(scope))
                    regAdds.Add(new AddScopedRegistry(record.Registry, url, scope));
        }

        /// <summary>
        /// Resolves the concrete version string for a package given the user's
        /// <see cref="VersionTarget"/> choice.
        /// <list type="bullet">
        ///   <item><see cref="VersionTarget.Recommended"/> → <paramref name="recommended"/> ?? <paramref name="min"/></item>
        ///   <item><see cref="VersionTarget.Min"/> → <paramref name="min"/> ?? <paramref name="recommended"/></item>
        /// </list>
        /// </summary>
        private static string ResolveVersion(string recommended, string min, VersionTarget target)
        {
            recommended = string.IsNullOrEmpty(recommended) ? null : recommended;
            min         = string.IsNullOrEmpty(min)         ? null : min;
            return target == VersionTarget.Min
                ? (min ?? recommended)
                : (recommended ?? min);
        }

        /// <summary>
        /// Adds a <see cref="RemovePackage"/> action for <paramref name="id"/> only if
        /// it hasn't been added before (deduplication for split-replace scenarios).
        /// </summary>
        private static void AddRemove(
            string id,
            List<RemovePackage> removes,
            HashSet<string> removedIds)
        {
            if (removedIds.Add(id))
                removes.Add(new RemovePackage(id));
        }

        /// <summary>
        /// Resolves a registry URL from the catalog's <c>Registries</c> dictionary using
        /// the key stored in <see cref="ExternalRecord.Registry"/>. Falls back to the raw
        /// key string when the dictionary is absent or the key is not found.
        /// </summary>
        private static string ResolveRegistryUrl(ExternalRecord record, PackageCatalog catalog)
        {
            if (record == null) return string.Empty;

            var key = record.Registry;
            if (string.IsNullOrEmpty(key)) return string.Empty;

            if (catalog?.Registries != null && catalog.Registries.TryGetValue(key, out var url))
                return url ?? string.Empty;

            // Key not in map — treat the raw value as the URL (direct URL in catalog).
            return key;
        }

        /// <summary>
        /// The external record installs from its git source right now: <c>installMethod: git</c>,
        /// and (for <c>registryFirst</c>) the registry has no version at or above minVersion yet.
        /// </summary>
        internal static bool InstallsFromGit(ExternalRecord rec, PackageCatalog catalog) =>
            ResolveMethod(rec?.InstallMethodName, InstallMethod.Upm) == InstallMethod.Git &&
            RegistryVersions.For(rec, catalog) == null;

        /// <summary>
        /// An installed spec below <paramref name="minVersion"/>: a plain version, or a git URL whose
        /// <c>#tag</c> is a version. A git URL without a version tag counts as fine — it is a
        /// deliberate pin the hub can't compare.
        /// </summary>
        internal static bool BelowMin(string installed, string minVersion)
        {
            if (string.IsNullOrEmpty(installed) || !SemVer.IsVersion(minVersion)) return false;
            var hash = installed.LastIndexOf('#');
            var version = hash >= 0 ? installed.Substring(hash + 1) : installed;
            return SemVer.IsVersion(version) && SemVer.Compare(version, minVersion) < 0;
        }

        private static void AddScopes(ExternalRecord record, PackageCatalog catalog, List<MigrationAction> regAdds)
        {
            if (record?.Scopes == null) return;
            var registryUrl = ResolveRegistryUrl(record, catalog);
            foreach (var scope in record.Scopes)
                if (!string.IsNullOrEmpty(scope))
                    regAdds.Add(new AddScopedRegistry(record.Registry ?? string.Empty, registryUrl, scope));
        }

        /// <summary>
        /// The install method for a single record: the catalog may demand git
        /// (<c>"installMethod": "git"</c>), otherwise the pass's own method applies. Needed for
        /// versions the registry lacks — the record then states where it installs from, and that is a
        /// data change, not an installer release. Case-insensitive; an unknown value reads as
        /// "registry" so a typo in the metadata can't send an install somewhere unexpected.
        /// </summary>
        internal static InstallMethod ResolveMethod(string recordMethod, InstallMethod fallback) =>
            string.Equals(recordMethod, "git", System.StringComparison.OrdinalIgnoreCase)
                ? InstallMethod.Git
                : fallback;

        /// <summary>
        /// Emits an <see cref="AddGitPackage"/> for each entry in the component's git chain. Returns
        /// true when git planning handled the component (a git block was present). Returns false when
        /// there is no git block, so the caller falls back to UPM planning — adding a warning only
        /// when <paramref name="warnOnFallback"/> (i.e. the component actually needs installing, so
        /// an already up-to-date component with no git block doesn't spam the confirm dialog).
        /// </summary>
        private static bool TryPlanGit(GitInstall git, List<MigrationAction> gitAdds,
            List<PlannerWarning> warnings, string componentId, bool warnOnFallback)
        {
            if (git?.Packages == null || git.Packages.Count == 0)
            {
                if (warnOnFallback)
                    warnings.Add(new PlannerWarning(componentId,
                        $"No git source for '{componentId}' — falling back to UPM for this component."));
                return false;
            }
            foreach (var p in git.Packages)
                if (p != null && !string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(p.Url))
                    gitAdds.Add(new AddGitPackage(p.Id, p.Spec));
            return true;
        }
    }
}
