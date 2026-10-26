using System;
using System.Collections.Generic;

namespace CAS.Publishing.Installer.Scanner
{
    // ── Split migration group ────────────────────────────────────────────────

    /// <summary>
    /// Describes a one-to-many split migration: a single legacy npm id that maps
    /// to two or more catalog <see cref="PackageRecord"/>s.
    /// Derived at scan time from <c>legacyNpmIds</c> entries shared across records;
    /// no catalog schema change is required.
    /// </summary>
    public sealed class MigrationGroup
    {
        /// <summary>The shared legacy npm id (e.g. "com.psv.firebase.base").</summary>
        public string LegacyId { get; }

        /// <summary>Canonical catalog ids of all replacement packages.</summary>
        public IReadOnlyList<string> PackageIds { get; }

        internal MigrationGroup(string legacyId, IReadOnlyList<string> packageIds)
        {
            LegacyId   = legacyId;
            PackageIds = packageIds ?? Array.Empty<string>();
        }
    }

    // ── State enums ──────────────────────────────────────────────────────────

    /// <summary>
    /// Installation state of a catalog <c>PackageRecord</c> inside the client project.
    /// </summary>
    public enum PackageState
    {
        /// <summary>Not in manifest and no legacy asset paths present.</summary>
        NotInstalled,

        /// <summary>In manifest under canonical id, version ≥ recommendedVersion (or minVersion when recommended is absent).</summary>
        UpmCurrent,

        /// <summary>In manifest under canonical id, version ≥ minVersion but &lt; recommendedVersion.</summary>
        UpmOutdated,

        /// <summary>In manifest under canonical id, version &lt; minVersion.</summary>
        UpmBelowMin,

        /// <summary>In manifest under a legacy npm id (not the canonical id).</summary>
        LegacyUpm,

        /// <summary>Legacy asset paths exist on disk; manifest has neither the canonical id nor any legacy id.</summary>
        LegacyAssets,

        /// <summary>
        /// Conflict: canonical id AND a legacy id are both in manifest, OR
        /// any manifest id AND legacy asset paths are both present.
        /// </summary>
        Conflict,

        /// <summary>
        /// In manifest under the canonical id (semver), but NO registered scoped-registry scope
        /// covers it — Unity cannot resolve it ("Package cannot be found"). Fix = add the scope.
        /// </summary>
        ScopeMissing,
    }

    /// <summary>
    /// Removal state of a legacy package described by an <c>UninstallRecord</c>.
    /// Only legacy ids that are actually present in manifest produce a scan result.
    /// </summary>
    public enum UninstallState
    {
        /// <summary>The legacy npm id is not in manifest — nothing to do.</summary>
        NotInstalled,

        /// <summary>The legacy npm id is present in manifest and should be removed.</summary>
        InstalledNeedsRemoval,
    }

    /// <summary>
    /// Installation state of an external <c>ExternalRecord</c> inside the client project.
    /// </summary>
    public enum ExternalState
    {
        /// <summary>Not found in manifest dependencies.</summary>
        NotInstalled,

        /// <summary>Found in manifest dependencies.</summary>
        UpmCurrent,

        /// <summary>Found in manifest dependencies but none of the required scopes are registered.</summary>
        ScopeMissing,

        /// <summary>
        /// Not in manifest, but a non-UPM copy (e.g. .unitypackage) was detected in Assets/ via
        /// the catalog's <c>assetMarkers</c>. Installing via UPM would duplicate it, so the hub
        /// blocks Install and offers Migrate-to-UPM instead.
        /// </summary>
        InstalledOutsideUpm,

        /// <summary>
        /// Not on the canonical UPM id, but a LEGACY package that already provides this SDK is present
        /// in manifest (a catalog <c>legacyManifestIds</c> entry, e.g. the bundled git package
        /// <c>com.psv.tenjin</c>). The SDK already works, so the hub reports it as installed (legacy)
        /// and offers NO Install/Migrate — installing the canonical id would duplicate the SDK
        /// (CS0101/CS0433) and the legacy wrapper's namespace may differ. Moving to the canonical
        /// split is a separate, deliberate migration.
        /// </summary>
        InstalledLegacy,
    }

    // ── Per-package results ──────────────────────────────────────────────────

    /// <summary>
    /// Scan result for one <c>PackageRecord</c>.
    /// </summary>
    public sealed class PackageScanResult
    {
        /// <summary>Canonical package id (e.g. "com.example.debug").</summary>
        public string Id { get; }

        /// <summary>Human-readable display name from the catalog.</summary>
        public string DisplayName { get; }

        /// <summary>Classified installation state.</summary>
        public PackageState State { get; }

        /// <summary>
        /// Version string found in manifest.json, or null when not installed via UPM.
        /// Set for both canonical id and legacy npm id hits.
        /// </summary>
        public string DetectedVersion { get; }

        /// <summary>
        /// The legacy npm id that was found in manifest, or null when not applicable.
        /// </summary>
        public string DetectedLegacyNpmId { get; }

        /// <summary>
        /// Legacy asset paths (relative to Assets/) that actually exist on disk.
        /// Non-empty only when state is <see cref="PackageState.LegacyAssets"/> or
        /// <see cref="PackageState.Conflict"/>.
        /// </summary>
        public IReadOnlyList<string> DetectedLegacyPaths { get; }

        internal PackageScanResult(
            string id,
            string displayName,
            PackageState state,
            string detectedVersion,
            string detectedLegacyNpmId,
            IReadOnlyList<string> detectedLegacyPaths)
        {
            Id = id;
            DisplayName = displayName;
            State = state;
            DetectedVersion = detectedVersion;
            DetectedLegacyNpmId = detectedLegacyNpmId;
            DetectedLegacyPaths = detectedLegacyPaths ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Scan result for one <c>ExternalRecord</c>.
    /// </summary>
    public sealed class ExternalScanResult
    {
        /// <summary>Package id (e.g. "com.cleversolutions.ads.mediation").</summary>
        public string Id { get; }

        /// <summary>Human-readable display name from the catalog.</summary>
        public string DisplayName { get; }

        /// <summary>Classified installation state.</summary>
        public ExternalState State { get; }

        /// <summary>Version string found in manifest.json, or null when not installed.</summary>
        public string DetectedVersion { get; }

        /// <summary>
        /// The legacy manifest id that provides this SDK (e.g. "com.psv.tenjin"), set only when
        /// <see cref="State"/> is <see cref="ExternalState.InstalledLegacy"/>; null otherwise.
        /// Used by the UI to tell the user which package currently provides the SDK.
        /// </summary>
        public string DetectedLegacyId { get; }

        internal ExternalScanResult(
            string id,
            string displayName,
            ExternalState state,
            string detectedVersion,
            string detectedLegacyId = null)
        {
            Id = id;
            DisplayName = displayName;
            State = state;
            DetectedVersion = detectedVersion;
            DetectedLegacyId = detectedLegacyId;
        }
    }

    /// <summary>
    /// Scan result for one legacy npm id from a catalog <c>UninstallRecord</c>.
    /// Only emitted when the legacy id is actually present in manifest.json;
    /// ids that are already absent produce no scan result.
    /// </summary>
    public sealed class UninstallScanResult
    {
        /// <summary>
        /// The legacy npm id found in manifest.json (e.g. "com.psv.unity.edm").
        /// </summary>
        public string LegacyNpmId { get; }

        /// <summary>Removal state — always <see cref="UninstallState.InstalledNeedsRemoval"/>
        /// for results that are actually emitted (the scanner skips absent ids).</summary>
        public UninstallState State { get; }

        /// <summary>Version string found in manifest.json for this legacy id.</summary>
        public string DetectedVersion { get; }

        internal UninstallScanResult(string legacyNpmId, UninstallState state, string detectedVersion)
        {
            LegacyNpmId     = legacyNpmId;
            State           = state;
            DetectedVersion = detectedVersion;
        }
    }

    // ── Top-level report ─────────────────────────────────────────────────────

    /// <summary>
    /// Immutable snapshot of the full installer scan — one entry per catalog record.
    /// </summary>
    public sealed class ScanReport
    {
        /// <summary>Catalog version string copied from the input <c>PackageCatalog</c>.</summary>
        public string CatalogVersion { get; }

        /// <summary>UTC timestamp when the scan was performed.</summary>
        public DateTime ScannedAtUtc { get; }

        /// <summary>One result per <c>PackageRecord</c> in the catalog.</summary>
        public IReadOnlyList<PackageScanResult> Packages { get; }

        /// <summary>One result per <c>ExternalRecord</c> in the catalog.</summary>
        public IReadOnlyList<ExternalScanResult> External { get; }

        /// <summary>
        /// Scan results for legacy npm ids listed in <c>UninstallRecord</c> entries.
        /// Contains only ids that are actually present in manifest.json — absent ids
        /// produce no entry here. Never null; may be empty.
        /// </summary>
        public IReadOnlyList<UninstallScanResult> Uninstalls { get; }

        /// <summary>
        /// Split migration groups — one entry per legacy npm id that appears in the
        /// <c>legacyNpmIds</c> list of two or more catalog <see cref="PackageRecord"/>s.
        /// Derived entirely at scan time; no catalog schema change required.
        /// Never null; may be empty.
        /// </summary>
        public IReadOnlyList<MigrationGroup> SplitGroups { get; }

        /// <summary>
        /// Stable, order-independent hash of the full report state.
        /// Changes only when the set of (id, state) pairs changes — not on timestamp changes.
        /// Used by Phase 3 to suppress the auto-popup when nothing has changed.
        /// Includes <see cref="Uninstalls"/> pairs so adding a legacy package to manifest
        /// triggers the auto-popup. Includes <see cref="SplitGroups"/> ids so adding a
        /// new split group triggers the auto-popup.
        /// </summary>
        public string Hash { get; }

        /// <summary>Non-null when manifest.json could not be read/parsed — the report's package
        /// states are then meaningless and the UI should surface this instead.</summary>
        public string ManifestError { get; }

        internal ScanReport(
            string catalogVersion,
            DateTime scannedAtUtc,
            IReadOnlyList<PackageScanResult> packages,
            IReadOnlyList<ExternalScanResult> external,
            IReadOnlyList<UninstallScanResult> uninstalls,
            IReadOnlyList<MigrationGroup> splitGroups,
            string hash,
            string manifestError = null)
        {
            CatalogVersion = catalogVersion;
            ScannedAtUtc   = scannedAtUtc;
            Packages       = packages    ?? new List<PackageScanResult>();
            External       = external    ?? new List<ExternalScanResult>();
            Uninstalls     = uninstalls  ?? new List<UninstallScanResult>();
            SplitGroups    = splitGroups ?? new List<MigrationGroup>();
            Hash           = hash;
            ManifestError  = manifestError;
        }
    }
}
