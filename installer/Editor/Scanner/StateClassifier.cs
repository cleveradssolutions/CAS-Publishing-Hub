using System.Collections.Generic;
using CAS.Publishing.Installer.Catalog;

namespace CAS.Publishing.Installer.Scanner
{
    /// <summary>
    /// Pure classification logic: given a <see cref="PackageRecord"/> or <see cref="ExternalRecord"/>
    /// plus probe data, produces the appropriate scan result.
    /// No I/O; no side effects.
    /// </summary>
    internal static class StateClassifier
    {
        // ── Git-URL helper ───────────────────────────────────────────────────

        /// <summary>True when a manifest dependency value is a git URL rather than a semver version.</summary>
        public static bool IsGitSpec(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value.Contains("://") || value.Contains(".git") || value.StartsWith("git@");
        }

        // ── PackageRecord classification ─────────────────────────────────────

        /// <summary>
        /// Classifies a <see cref="PackageRecord"/> using pre-collected probe data and
        /// returns the corresponding <see cref="PackageScanResult"/>.
        /// </summary>
        /// <param name="record">Catalog entry being classified.</param>
        /// <param name="manifest">Parsed manifest.json snapshot.</param>
        /// <param name="existingLegacyPaths">
        /// Legacy asset paths that actually exist on disk (already filtered by AssetProbe).
        /// </param>
        public static PackageScanResult Classify(
            PackageRecord record,
            ManifestData manifest,
            IReadOnlyList<string> existingLegacyPaths)
        {
            var deps = manifest.Dependencies;

            // Probe: is the canonical id in manifest?
            deps.TryGetValue(record.Id, out var canonicalVersion);
            bool hasCanonical = canonicalVersion != null;

            // Probe: is any legacy npm id in manifest?
            string detectedLegacyNpmId = null;
            string legacyNpmVersion = null;
            if (record.LegacyNpmIds != null)
            {
                foreach (var lid in record.LegacyNpmIds)
                {
                    if (string.IsNullOrEmpty(lid)) continue;
                    if (deps.TryGetValue(lid, out var lv))
                    {
                        detectedLegacyNpmId = lid;
                        legacyNpmVersion = lv;
                        break; // first hit wins; Conflict check below handles multiples via bool flags
                    }
                }
            }

            bool hasLegacyNpm = detectedLegacyNpmId != null;
            bool hasLegacyAssets = existingLegacyPaths != null && existingLegacyPaths.Count > 0;

            // ── Conflict detection ────────────────────────────────────────────
            // Conflict if:
            //   (a) canonical AND a legacy npm id are both in manifest, OR
            //   (b) any manifest id (canonical or legacy) AND legacy asset paths also present.
            bool anyManifestId = hasCanonical || hasLegacyNpm;
            if ((hasCanonical && hasLegacyNpm) || (anyManifestId && hasLegacyAssets))
            {
                // Prefer canonical version in the result; fall back to legacy npm version.
                var conflictVersion = canonicalVersion ?? legacyNpmVersion;
                return new PackageScanResult(
                    record.Id, record.DisplayName,
                    PackageState.Conflict,
                    conflictVersion,
                    detectedLegacyNpmId,
                    existingLegacyPaths);
            }

            // ── LegacyUpm ─────────────────────────────────────────────────────
            if (hasLegacyNpm)
            {
                return new PackageScanResult(
                    record.Id, record.DisplayName,
                    PackageState.LegacyUpm,
                    legacyNpmVersion,
                    detectedLegacyNpmId,
                    EmptyPaths);
            }

            // ── LegacyAssets ──────────────────────────────────────────────────
            if (hasLegacyAssets && !hasCanonical)
            {
                return new PackageScanResult(
                    record.Id, record.DisplayName,
                    PackageState.LegacyAssets,
                    null,
                    null,
                    existingLegacyPaths);
            }

            // ── Canonical UPM states ──────────────────────────────────────────
            if (hasCanonical)
            {
                if (IsGitSpec(canonicalVersion))
                    return new PackageScanResult(record.Id, record.DisplayName,
                        PackageState.UpmCurrent, canonicalVersion, null, EmptyPaths);

                // A semver dependency is only resolvable when a registered scope covers the id.
                // record.Scopes (catalog) is authoritative when present; otherwise the id itself
                // must be covered (exact or prefix scope). Mirrors ExternalState.ScopeMissing.
                bool covered;
                if (record.Scopes != null && record.Scopes.Count > 0)
                {
                    var anyExplicitScope = false;
                    foreach (var scope in record.Scopes)
                        if (!string.IsNullOrEmpty(scope) && manifest.HasRegisteredScope(scope))
                        { anyExplicitScope = true; break; }
                    // Fallback: an older catalog may have installed this id under an exact-id scope
                    // before a newer catalog widened record.Scopes to a broader prefix — accept
                    // coverage by id too, so the row doesn't flip to "Needs registry" on catalog update.
                    covered = anyExplicitScope || manifest.HasScopeCovering(record.Id);
                }
                else
                {
                    covered = manifest.HasScopeCovering(record.Id);
                }

                if (!covered)
                    return new PackageScanResult(record.Id, record.DisplayName,
                        PackageState.ScopeMissing, canonicalVersion, null, EmptyPaths);

                var state = ClassifyVersion(canonicalVersion, record.MinVersion, record.RecommendedVersion);
                return new PackageScanResult(
                    record.Id, record.DisplayName,
                    state,
                    canonicalVersion,
                    null,
                    EmptyPaths);
            }

            // ── NotInstalled ──────────────────────────────────────────────────
            return new PackageScanResult(
                record.Id, record.DisplayName,
                PackageState.NotInstalled,
                null, null, EmptyPaths);
        }

        // ── ExternalRecord classification ────────────────────────────────────

        /// <summary>
        /// Classifies an <see cref="ExternalRecord"/> against manifest data. When the external is
        /// absent from the manifest but a non-UPM copy was detected on disk
        /// (<paramref name="detectedOutsideUpm"/>), reports
        /// <see cref="ExternalState.InstalledOutsideUpm"/> instead of NotInstalled so the hub
        /// won't offer an Install that duplicates it.
        /// </summary>
        /// <param name="registeredUpmVersion">Version of this package registered with the Unity
        /// Package Manager (direct OR transitive — e.g. com.tenjin.sdk pulled in by the tenjin
        /// adapter), or null when not registered. A registered package is a valid UPM install even
        /// when absent from manifest.json, and its loaded types must NOT be mistaken for a manual
        /// Assets/ copy (the "installed manually, but its known folders weren't found" dead end).</param>
        /// <param name="outsideUpmVersion">The version the manual copy reports at runtime (read
        /// through the record's <c>versionType</c>/<c>versionField</c>), or null when it declares
        /// none. Only used with <paramref name="detectedOutsideUpm"/>: without it the wizard showed
        /// "Already installed (?)" and the CAS version gate could not tell an old copy from a
        /// current one.</param>
        public static ExternalScanResult Classify(
            ExternalRecord record,
            ManifestData manifest,
            bool detectedOutsideUpm = false,
            string registeredUpmVersion = null,
            string outsideUpmVersion = null)
        {
            var deps = manifest.Dependencies;

            if (!deps.TryGetValue(record.Id, out var version))
            {
                // Canonical id absent. A LEGACY package that already provides this SDK (e.g. the bundled
                // git package com.psv.tenjin) takes precedence — manifest is authoritative, so this beats
                // reflection (no false-positive) and prevents offering an Install that would duplicate the
                // SDK. The hub reports InstalledLegacy and offers no action.
                if (record.LegacyManifestIds != null)
                {
                    foreach (var legacyId in record.LegacyManifestIds)
                    {
                        if (!string.IsNullOrEmpty(legacyId) && deps.TryGetValue(legacyId, out var legacyVersion))
                            return new ExternalScanResult(
                                record.Id, record.DisplayName,
                                ExternalState.InstalledLegacy,
                                legacyVersion,
                                legacyId);
                    }
                }

                // Registered with the Package Manager (transitively, e.g. via an adapter's
                // dependency) → a real UPM install. Checked BEFORE the outside-UPM signal: the
                // loaded types that trip that signal are the registered package's own assemblies.
                if (!string.IsNullOrEmpty(registeredUpmVersion))
                    return new ExternalScanResult(
                        record.Id, record.DisplayName,
                        ExternalState.UpmCurrent,
                        registeredUpmVersion);

                // Manifest is the source of truth for UPM; if a non-UPM copy is on disk, surface it.
                if (detectedOutsideUpm)
                    return new ExternalScanResult(
                        record.Id, record.DisplayName,
                        ExternalState.InstalledOutsideUpm,
                        string.IsNullOrEmpty(outsideUpmVersion) ? null : outsideUpmVersion);

                return new ExternalScanResult(
                    record.Id, record.DisplayName,
                    ExternalState.NotInstalled,
                    null);
            }

            // A git-URL install has no scoped registry by design — treat presence as installed.
            if (IsGitSpec(version))
                return new ExternalScanResult(record.Id, record.DisplayName, ExternalState.UpmCurrent, version);

            // Found in dependencies — check if any required scope is registered.
            bool anyScope = false;
            if (record.Scopes != null)
            {
                foreach (var scope in record.Scopes)
                {
                    if (manifest.HasRegisteredScope(scope))
                    {
                        anyScope = true;
                        break;
                    }
                }
            }
            else
            {
                // No scopes required by catalog — treat as fine.
                anyScope = true;
            }

            var state = anyScope ? ExternalState.UpmCurrent : ExternalState.ScopeMissing;
            return new ExternalScanResult(record.Id, record.DisplayName, state, version);
        }

        // ── Version classification helpers ───────────────────────────────────

        /// <summary>
        /// Given the detected version and the catalog version constraints, determines
        /// the appropriate <see cref="PackageState"/> for a canonically-installed package.
        /// </summary>
        private static PackageState ClassifyVersion(
            string detected,
            string minVersion,
            string recommendedVersion)
        {
            // "No constraints" path — any version is current.
            bool hasMin = !string.IsNullOrEmpty(minVersion);
            bool hasRecommended = !string.IsNullOrEmpty(recommendedVersion);

            if (!hasMin && !hasRecommended)
                return PackageState.UpmCurrent;

            // Check below minimum first (most severe).
            if (hasMin && CatalogUpdater.IsNewer(minVersion, detected))
                return PackageState.UpmBelowMin;

            // At or above min — check vs recommended.
            if (hasRecommended && CatalogUpdater.IsNewer(recommendedVersion, detected))
                return PackageState.UpmOutdated;

            return PackageState.UpmCurrent;
        }

        private static readonly string[] EmptyPaths = System.Array.Empty<string>();
    }
}
