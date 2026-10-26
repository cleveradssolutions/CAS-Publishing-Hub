using System;
using System.Collections.Generic;
using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Scanner;

namespace CAS.Publishing.Installer.Migrator
{
    /// <summary>Result of <see cref="FirebaseMigrationPlan.Build"/>: ordered actions + warnings.</summary>
    internal sealed class FirebaseMigrationPlanResult
    {
        public List<MigrationAction> Actions = new List<MigrationAction>();
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Pure builder for the compound legacy-Firebase migration: remove the legacy wrapper
    /// (com.psv.firebase.base) and any catalog-listed uninstalls present (com.psv.unity.edm),
    /// then install the native Firebase modules detected in the project plus the adapter
    /// packages of the split group (detectMarkers-gated, requires-gated). Built directly —
    /// NOT through MigrationPlanner — so the partial-split backstop cannot drop the removal;
    /// this builder IS the complete migration the backstop protects against losing.
    /// No I/O and no Unity APIs: caller supplies manifest deps, loaded identifiers, and the
    /// embedded-package probe.
    /// </summary>
    internal static class FirebaseMigrationPlan
    {
        internal static FirebaseMigrationPlanResult Build(
            PackageCatalog catalog,
            MigrationGroup group,
            IReadOnlyDictionary<string, string> dependencies,
            ICollection<string> loadedIdentifiers,
            Func<string, bool> embeddedExists)
        {
            var r = new FirebaseMigrationPlanResult();
            if (catalog == null || group == null || dependencies == null) return r;
            if (!dependencies.ContainsKey(group.LegacyId)) return r; // nothing to migrate

            var removes = new List<MigrationAction> { new RemovePackage(group.LegacyId) };
            var regs    = new List<MigrationAction>();
            var adds    = new List<MigrationAction>();
            var seenRegScopes = new HashSet<string>(); // "url|scope" dedup across natives + adapters

            // ── Catalog uninstalls present in manifest (e.g. com.psv.unity.edm) ──
            if (catalog.Uninstall != null)
                foreach (var u in catalog.Uninstall)
                    if (u?.LegacyNpmIds != null)
                        foreach (var lid in u.LegacyNpmIds)
                            if (!string.IsNullOrEmpty(lid) && lid != group.LegacyId && dependencies.ContainsKey(lid))
                                removes.Add(new RemovePackage(lid));

            // ── Native Firebase: the external record that names our legacy id ──
            ExternalRecord native = null;
            if (catalog.External != null)
                foreach (var e in catalog.External)
                    if (e?.LegacyManifestIds != null && e.LegacyManifestIds.Contains(group.LegacyId))
                    { native = e; break; }

            if (native != null)
            {
                var baseVersion = !string.IsNullOrEmpty(native.RecommendedVersion) ? native.RecommendedVersion : native.MinVersion;
                if (!string.IsNullOrEmpty(baseVersion))
                {
                    EmitRegistry(catalog, native.Registry, native.Scopes, native.Id, regs, seenRegScopes);
                    foreach (var add in ExternalInstallSet.Resolve(native, baseVersion, loadedIdentifiers))
                        adds.Add(add);
                }
                else
                    r.Warnings.Add($"No version configured for {native.Id} in the catalog — native modules skipped.");
            }
            else
            {
                // Stale catalog: no external record names group.LegacyId under LegacyManifestIds, so
                // there is nothing to replace the wrapper with. Continuing here would remove
                // com.psv.firebase.base and add ONLY the adapters — stripping Firebase from the
                // project with no native modules installed. Abort with an empty plan instead; the
                // caller (WizardActions.MigrateFirebaseLegacy) surfaces this warning and tells the
                // user to Refresh once the catalog updates.
                r.Warnings.Add($"No external catalog record is linked to {group.LegacyId} — native modules skipped.");
                return r; // r.Actions is still empty here — abort before removes/regs/adds are merged in
            }

            // ── Adapters: the split group's replacement packages ──
            foreach (var pkgId in group.PackageIds)
            {
                PackageRecord rec = null;
                if (catalog.Packages != null)
                    foreach (var p in catalog.Packages)
                        if (p != null && p.Id == pkgId) { rec = p; break; }
                if (rec == null) continue;

                var missing = RequirementGate.FirstMissing(rec.Requires, dependencies, embeddedExists);
                if (missing != null)
                {
                    r.Warnings.Add($"{rec.Id} skipped — requires {missing}, which is not in the project.");
                    continue;
                }

                if (rec.DetectMarkers != null && rec.DetectMarkers.Count > 0 &&
                    !AssetInstallProbe.IsPresentInIdentifiers(loadedIdentifiers ?? Array.Empty<string>(), rec.DetectMarkers))
                    continue; // feature not used in this project — don't add the adapter

                var version = !string.IsNullOrEmpty(rec.RecommendedVersion) ? rec.RecommendedVersion : rec.MinVersion;
                if (string.IsNullOrEmpty(version))
                {
                    r.Warnings.Add($"{rec.Id} skipped — no version configured in the catalog.");
                    continue;
                }

                EmitRegistry(catalog, rec.Registry, rec.Scopes, rec.Id, regs, seenRegScopes);
                adds.Add(new AddPackage(rec.Id, version));
            }

            r.Actions.AddRange(removes);
            r.Actions.AddRange(regs);
            r.Actions.AddRange(adds);
            return r;
        }

        private static void EmitRegistry(
            PackageCatalog catalog, string registryKey, IReadOnlyList<string> scopes, string fallbackScope,
            List<MigrationAction> regs, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(registryKey)) return;
            string url = null;
            if (catalog?.Registries != null && catalog.Registries.TryGetValue(registryKey, out var mapped))
                url = mapped;
            if (string.IsNullOrEmpty(url)) url = registryKey.Contains("://") ? registryKey : null;
            if (string.IsNullOrEmpty(url)) return;

            var effective = (scopes != null && scopes.Count > 0) ? scopes : new[] { fallbackScope };
            foreach (var scope in effective)
                if (!string.IsNullOrEmpty(scope) && seen.Add(url + "|" + scope))
                    regs.Add(new AddScopedRegistry(registryKey, url, scope));
        }
    }
}
