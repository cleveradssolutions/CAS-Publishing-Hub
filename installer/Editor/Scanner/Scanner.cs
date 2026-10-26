using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using CAS.Publishing.Installer.Catalog;

namespace CAS.Publishing.Installer.Scanner
{
    /// <summary>
    /// Entry point for the Phase-2 scanner subsystem.
    /// Reads the current project's manifest and asset layout, classifies every
    /// catalog entry, and returns an immutable <see cref="ScanReport"/>.
    /// </summary>
    public static class ProjectScanner
    {
        /// <summary>
        /// Scans the current Unity project against the supplied catalog and returns
        /// a fully-populated <see cref="ScanReport"/>. Never returns null; always
        /// returns a report (with empty lists when the catalog has no entries).
        /// </summary>
        /// <param name="catalog">The loaded catalog. Must not be null.</param>
        public static ScanReport Scan(PackageCatalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));

            var manifest = ManifestProbe.Read();
            var manifestError = manifest.Readable ? null : manifest.ReadError;

            // ── Classify packages ─────────────────────────────────────────────
            var packages = new List<PackageScanResult>();
            if (catalog.Packages != null)
            {
                foreach (var record in catalog.Packages)
                {
                    if (record == null) continue;
                    var existingLegacyPaths = AssetProbe.FindExisting(record.LegacyAssetPaths);
                    packages.Add(StateClassifier.Classify(record, manifest, existingLegacyPaths));
                }
            }

            // ── Classify externals ────────────────────────────────────────────
            var externals = new List<ExternalScanResult>();
            if (catalog.External != null)
            {
                // Detect non-UPM (.unitypackage / manual) installs so an installed-outside-UPM SDK
                // isn't reported as NotInstalled (which would let the hub duplicate it). Presence is
                // by loaded-type identifiers — namespace, or simple name for global-namespace types
                // (covers asmdef, DLL, and raw .cs with no asmdef). The identifier set is collected
                // ONCE, only when some external declares assetMarkers.
                HashSet<string> loadedIdentifiers = null;
                foreach (var record in catalog.External)
                {
                    if (record?.AssetMarkers != null && record.AssetMarkers.Count > 0)
                    {
                        loadedIdentifiers = AssetInstallProbe.CollectLoadedIdentifiers();
                        break;
                    }
                }

                // Packages registered with the Package Manager — INCLUDING transitive dependencies
                // (e.g. com.tenjin.sdk pulled in by the tenjin adapter), which are absent from
                // manifest.json. Without this, a transitive package's loaded types trip the
                // outside-UPM reflection signal and the SDK is misreported as installed manually.
                var registeredVersions = CollectRegisteredUpmVersions();

                foreach (var record in catalog.External)
                {
                    if (record == null) continue;

                    // Reflection over loaded types is the primary signal, but it is BLIND when the manual
                    // (.unitypackage) copy doesn't compile/load — exactly the messy projects that need
                    // migrating most. Fall back to disk (short-circuited, so the walk only runs when
                    // reflection misses): an SDK-IDENTITY folder on disk, or a signatured scattered file
                    // anywhere under Assets/ (catches an SDK moved to a non-standard path, e.g. Tenjin
                    // relocated to Assets/Scripts/Core/Tenjin). Without this the hub offers a duplicating
                    // Install instead of Migrate, and the manual copy is never removed.
                    var outsideUpm =
                        (loadedIdentifiers != null &&
                         AssetInstallProbe.IsPresentInIdentifiers(loadedIdentifiers, record.AssetMarkers)) ||
                        AssetInstallProbe.AnyIdentityRootExists(
                            AssetProbe.FindExisting(record.AssetRoots), record.AssetMarkers,
                            AssetInstallProbe.RootHasSdkPayload) ||
                        AssetInstallProbe.FindSignatureFiles(record.LegacyAssetFiles, record.AssetRoots).Count > 0;

                    registeredVersions.TryGetValue(record.Id, out var registeredVersion);
                    // A manual copy has no manifest entry to read a version from — ask the SDK
                    // itself, through the static member the catalog names for it.
                    var outsideUpmVersion = outsideUpm
                        ? AssetInstallProbe.ReadStaticVersion(record.VersionType, record.VersionField)
                        : null;
                    externals.Add(StateClassifier.Classify(
                        record, manifest, outsideUpm, registeredVersion, outsideUpmVersion));
                }
            }

            // ── Classify uninstalls ───────────────────────────────────────────
            // Only legacy ids that are actually present in manifest.json are emitted.
            var uninstalls = new List<UninstallScanResult>();
            if (catalog.Uninstall != null)
            {
                foreach (var record in catalog.Uninstall)
                {
                    if (record?.LegacyNpmIds == null) continue;
                    foreach (var legacyId in record.LegacyNpmIds)
                    {
                        if (string.IsNullOrEmpty(legacyId)) continue;
                        if (manifest.Dependencies.TryGetValue(legacyId, out var detectedVersion))
                        {
                            uninstalls.Add(new UninstallScanResult(
                                legacyId,
                                UninstallState.InstalledNeedsRemoval,
                                detectedVersion));
                        }
                        // Absent ids produce no scan result (NotInstalled — silent).
                    }
                }
            }

            // ── Derive split migration groups ─────────────────────────────────
            // A split group is any legacy npm id shared by ≥2 catalog PackageRecords.
            // We build a map legacyId → [canonicalIds] then keep only groups with Count≥2.
            var splitGroups = new List<MigrationGroup>();
            if (catalog.Packages != null)
            {
                var legacyToPackageIds = new Dictionary<string, List<string>>();
                foreach (var record in catalog.Packages)
                {
                    if (record?.LegacyNpmIds == null) continue;
                    foreach (var legacyId in record.LegacyNpmIds)
                    {
                        if (string.IsNullOrEmpty(legacyId)) continue;
                        if (!legacyToPackageIds.TryGetValue(legacyId, out var ids))
                        {
                            ids = new List<string>();
                            legacyToPackageIds[legacyId] = ids;
                        }
                        ids.Add(record.Id);
                    }
                }

                foreach (var kvp in legacyToPackageIds)
                    if (kvp.Value.Count >= 2)
                        splitGroups.Add(new MigrationGroup(kvp.Key, kvp.Value));
            }

            // ── Compute stable hash ───────────────────────────────────────────
            var hash = ComputeHash(packages, externals, uninstalls, splitGroups);

            return new ScanReport(
                catalog.CatalogVersion,
                DateTime.UtcNow,
                packages,
                externals,
                uninstalls,
                splitGroups,
                hash,
                manifestError);
        }

        // ── Hash computation ─────────────────────────────────────────────────

        /// <summary>
        /// Produces a stable, order-independent hex hash over the (id, state) pairs
        /// of all package, external, and uninstall results.
        ///
        /// Order-independent: we XOR per-entry MD5 digests so that reordering the
        /// catalog entries produces the same hash (XOR is commutative and associative).
        /// Per-entry input is "<id>|<state>" — only identity and state matter,
        /// not timestamps, detected versions, or path details, keeping the hash
        /// mutable-state-agnostic for Phase 3's "nothing changed" check.
        ///
        /// Backward-compatible: when <paramref name="uninstalls"/> is empty the XOR
        /// loop is a no-op and the resulting hash is identical to the pre-4a value.
        /// </summary>
        /// <summary>
        /// name → version of every package the Unity Package Manager has registered — direct AND
        /// transitive dependencies alike (manifest.json only lists the direct ones). Best-effort:
        /// enumeration failure degrades to an empty map (the scan then behaves as before this
        /// signal existed) rather than failing the whole scan.
        /// </summary>
        private static Dictionary<string, string> CollectRegisteredUpmVersions()
        {
            var map = new Dictionary<string, string>();
            try
            {
                foreach (var pkg in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                    if (pkg != null && !string.IsNullOrEmpty(pkg.name))
                        map[pkg.name] = pkg.version;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning(
                    $"[CAS Hub] Could not enumerate registered packages: {e.Message}");
            }
            return map;
        }

        private static string ComputeHash(
            IReadOnlyList<PackageScanResult> packages,
            IReadOnlyList<ExternalScanResult> externals,
            IReadOnlyList<UninstallScanResult> uninstalls,
            IReadOnlyList<MigrationGroup> splitGroups)
        {
            using var md5 = MD5.Create();
            // Accumulator: start at all-zeros (XOR identity).
            var accumulator = new byte[16];

            foreach (var p in packages)
            {
                XorInto(accumulator, md5.ComputeHash(
                    Encoding.UTF8.GetBytes($"{p.Id}|{p.State}")));
            }

            foreach (var e in externals)
            {
                XorInto(accumulator, md5.ComputeHash(
                    Encoding.UTF8.GetBytes($"{e.Id}|{e.State}")));
            }

            // Uninstall entries use the legacy id as the identity key.
            foreach (var u in uninstalls)
            {
                XorInto(accumulator, md5.ComputeHash(
                    Encoding.UTF8.GetBytes($"{u.LegacyNpmId}|{u.State}")));
            }

            // Split groups: include "legacyId|split" so adding a new split group triggers auto-popup.
            if (splitGroups != null)
            {
                foreach (var g in splitGroups)
                {
                    XorInto(accumulator, md5.ComputeHash(
                        Encoding.UTF8.GetBytes($"{g.LegacyId}|split")));
                }
            }

            return BytesToHex(accumulator);
        }

        private static void XorInto(byte[] target, byte[] source)
        {
            for (var i = 0; i < target.Length && i < source.Length; i++)
                target[i] ^= source[i];
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
