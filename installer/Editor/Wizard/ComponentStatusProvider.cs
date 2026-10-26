using System.Collections.Generic;
using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Common;
using CAS.Publishing.Installer.Migrator;
using CAS.Publishing.Installer.Scanner;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// View-model for one component row on the Components Overview screen,
    /// derived from a live project scan (read-only this iteration).
    /// </summary>
    internal sealed class ComponentStatus
    {
        public string Id;
        /// <summary>
        /// The package id ACTUALLY present in manifest.json — equals <see cref="Id"/> for a canonical
        /// install, but the detected legacy id when the SDK is present under a legacy id (e.g.
        /// <c>com.psv.tenjin</c> for an InstalledLegacy Tenjin, or a legacy npm id for a LegacyUpm
        /// package). The wizard "Remove" button targets THIS id so removal isn't a silent no-op.
        /// </summary>
        public string InstalledId;
        public string DisplayName;
        public string Sub;
        public string Logo;          // logo modifier suffix → .cas-logo--<Logo>
        public string Tone;          // "green" | "yellow" | "red" | "grey"
        public string StatusText;
        public string ActionText;
        public string ActionVariant; // "muted" | "primary" | "warn"
        public string Version;       // detected version, may be null
        public bool   Actionable;    // false when nothing to do (button disabled)
        public bool   Installed;     // true when the package is present (any non-NotInstalled state)
        public bool   OutsideUpm;    // detected via .unitypackage/manual, not UPM → action = Migrate
        public bool   GitInstalled;  // installed via a git-URL dependency → offer Switch to UPM
    }

    /// <summary>
    /// Reads the default client component set (CAS SDK, Tenjin, Firebase Analytics) from the
    /// live catalog + project scan. Read-only: no project mutation. Reuses the existing
    /// <see cref="CatalogLoader"/> / <see cref="ProjectScanner"/> backend.
    ///
    /// Note: install state is the package-manager truth (manifest.json). A leftover settings
    /// folder under Assets/ (e.g. CAS config) is NOT treated as "installed" — only the actual
    /// package counts.
    /// </summary>
    internal static class ComponentStatusProvider
    {
        // Default client components. Display name / sub / logo are wizard-side (presentation);
        // installation state + version come from the live scan against the catalog.
        private static readonly (string Id, string Name, string Sub, string Logo)[] Defaults =
        {
            // Tenjin is deliberately absent: from CAS 4.8.1 the native Tenjin SDK ships with CAS as
            // the TenjinMMP mediation entry, so the catalog no longer carries com.tenjin.sdk. Leaving
            // it here made the installer plan a step with no catalog source, which failed the whole
            // install run ("didn't add a manifest entry ... so it can't resolve").
            ("com.cleversolutions.ads.unity", "CAS SDK",            "Ads / Mediation", "cas"),
            ("com.google.firebase.analytics", "Firebase Analytics", "Analytics",       "firebase"),
        };

        // EDM4U (External Dependency Manager) is never shown as an installable row anywhere in the
        // wizard (Components table, Additional components, Setup/Configuration rows, AutoInstaller
        // plan) — it arrives transitively as a dependency of the CAS SDK / Firebase packages above,
        // so surfacing it as a separate thing to "install" is confusing and redundant. Scanner
        // detection, the catalog record (used for legacy-migration detection), and the
        // FirebaseMigrationPlan legacy-uninstall path are untouched — only wizard-visible lists hide
        // it. The hidden-id set itself lives in CAS.Publishing.Installer.Common.HiddenComponents (base editor
        // assembly, no asmdef references) rather than here, so the legacy IMGUI report view
        // (CAS.Publishing.Installer.Ui, same assembly as Common but NOT referencing this Wizard.Editor
        // assembly) can apply the same filter — see that type for the full rationale.
        internal static bool IsHiddenComponent(string id) => CAS.Publishing.Installer.Common.HiddenComponents.IsHidden(id);

        /// <summary>Shown instead of the normal "not installed yet" text while an auto-install
        /// attempt is actually running (<see cref="CAS.Publishing.Installer.MetadataAutoInstall.InstallInFlight"/>)
        /// — the screens hide their Install/Retry button for this message since there is nothing to
        /// retry mid-attempt.</summary>
        internal const string InstallingMessage = "Installing catalog metadata…";

        /// <summary>Pure: picks the in-flight message over <paramref name="notInstalledText"/> when
        /// <paramref name="inFlight"/> is true. Extracted so the NotInstalled message selection is
        /// unit-testable without touching <see cref="CAS.Publishing.Installer.MetadataAutoInstall"/> state.</summary>
        internal static string MetadataUnavailableMessage(bool inFlight, string notInstalledText) =>
            inFlight ? InstallingMessage : notInstalledText;

        /// <summary>Package ids of the default component set, in display order.</summary>
        public static IReadOnlyList<string> DefaultIds
        {
            get
            {
                var ids = new List<string>(Defaults.Length);
                foreach (var d in Defaults) ids.Add(d.Id);
                return ids;
            }
        }

        /// <summary>Display name + logo suffix for a default component id (presentation-side).</summary>
        public static bool TryGetDefaultDisplay(string id, out string name, out string logo)
        {
            foreach (var d in Defaults)
                if (d.Id == id) { name = d.Name; logo = d.Logo; return true; }
            name = id;
            logo = null;
            return false;
        }

        // Session cache: a status read runs ProjectScanner.Scan (reflection over loaded assemblies +
        // disk probes) and is called repeatedly per wizard open (SetupModel, AnyComponentInstalled,
        // CasPresence, each tab). Project state only changes via installs/migrations, which trigger a
        // domain reload that resets these statics — so caching is safe. An explicit Refresh calls
        // InvalidateCache(). The cached list is treated as read-only by all callers.
        private static List<ComponentStatus> _cachedStatuses;
        private static string _cachedError;
        private static bool _cachedOk;
        private static bool _hasCache;

        // Same session-cache treatment for the "Additional components" set (see TryGetAdditionalStatuses).
        private static List<ComponentStatus> _cachedAdditional;
        private static string _cachedAdditionalError;
        private static bool _cachedAdditionalOk;
        private static bool _hasAdditionalCache;

        // Shared raw scan, cached separately from the two view-level caches above so BuildStatuses
        // and BuildAdditionalStatuses — both cold on the same Refresh — run ProjectScanner.Scan
        // (reflection over loaded assemblies + disk probes) exactly ONCE between them, not twice.
        // Keyed by catalog identity: the catalog can be reloaded independently (e.g. by
        // CatalogLoader.InvalidateCache), so we guard the cache against stale scans computed
        // against an old catalog object.
        private static ScanReport _cachedScan;
        private static PackageCatalog _cachedScanCatalog;
        private static bool _hasScanCache;

        /// <summary>Drops the cached scan so the next <see cref="TryGetStatuses"/>/
        /// <see cref="TryGetAdditionalStatuses"/> re-scans. Call after an explicit user Refresh.
        /// (Static caches also reset on domain reload after installs.)</summary>
        public static void InvalidateCache()
        {
            _hasCache = false;
            _cachedStatuses = null;
            _cachedError = null;

            _hasAdditionalCache = false;
            _cachedAdditional = null;
            _cachedAdditionalError = null;

            _hasScanCache = false;
            _cachedScan = null;
            _cachedScanCatalog = null;

            _cachedManualRetired = null;
        }

        /// <summary>Runs (or reuses) the one project scan a cold render needs. Not part of the
        /// public API — <see cref="BuildStatuses"/> and <see cref="BuildAdditionalStatuses"/> are
        /// the only callers, and both are only reached once each per <see cref="InvalidateCache"/>
        /// window (the outer <c>_hasCache</c>/<c>_hasAdditionalCache</c> checks already skip a
        /// re-scan on every call after the first), so this is a plain lazy cache keyed by catalog identity.</summary>
        private static ScanReport GetScan(PackageCatalog catalog)
        {
            // Cache is valid only when both _hasScanCache is set AND the catalog object is the same
            // (by reference). This guards against stale scans when the catalog is reloaded independently.
            if (!_hasScanCache || !ReferenceEquals(_cachedScanCatalog, catalog))
            {
                _cachedScan = ProjectScanner.Scan(catalog);
                _cachedScanCatalog = catalog;
                _hasScanCache = true;
            }
            return _cachedScan;
        }

        /// <summary>
        /// Returns one <see cref="ComponentStatus"/> per default component (cached for the session;
        /// see <see cref="InvalidateCache"/>). On catalog failure returns false with a human-readable
        /// <paramref name="error"/>. The returned list is shared — treat it as read-only.
        /// </summary>
        public static bool TryGetStatuses(out List<ComponentStatus> statuses, out string error)
        {
            if (_hasCache)
            {
                statuses = _cachedStatuses;
                error = _cachedError;
                return _cachedOk;
            }

            _cachedOk = BuildStatuses(out _cachedStatuses, out _cachedError);
            _hasCache = true;
            statuses = _cachedStatuses;
            error = _cachedError;
            return _cachedOk;
        }

        private static bool BuildStatuses(out List<ComponentStatus> statuses, out string error)
        {
            statuses = new List<ComponentStatus>();
            error = null;

            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok)
            {
                error = load.Status == CatalogLoadStatus.NotInstalled
                    ? MetadataUnavailableMessage(CAS.Publishing.Installer.MetadataAutoInstall.InstallInFlight,
                        "Catalog metadata package is not installed yet — live component status is unavailable.")
                    : $"Catalog could not be read: {load.Error}";
                return false;
            }

            var report = GetScan(load.Catalog);

            var pkgById = new Dictionary<string, PackageScanResult>();
            if (report.Packages != null)
                foreach (var p in report.Packages)
                    if (p != null) pkgById[p.Id] = p;

            var extById = new Dictionary<string, ExternalScanResult>();
            if (report.External != null)
                foreach (var e in report.External)
                    if (e != null) extById[e.Id] = e;

            foreach (var d in Defaults)
            {
                if (pkgById.TryGetValue(d.Id, out var p))
                    statuses.Add(FromPackage(d, p));
                else if (extById.TryGetValue(d.Id, out var e))
                {
                    var s = FromExternal(d, e);
                    PromoteLegacySplit(s, e, report);
                    statuses.Add(s);
                }
                else
                    statuses.Add(NotInCatalog(d));
            }

            return true;
        }

        /// <summary>
        /// Returns one <see cref="ComponentStatus"/> per catalog entry that is NOT part of the
        /// default set (see <see cref="DefaultIds"/>) and is not the installer's own package(s)
        /// (the hub's own packages (installer + metadata) aren't
        /// user-facing "components"). Cached for the session like <see cref="TryGetStatuses"/>; see
        /// <see cref="InvalidateCache"/>. The returned list is shared — treat it as read-only.
        /// </summary>
        public static bool TryGetAdditionalStatuses(out List<ComponentStatus> statuses, out string error)
        {
            if (_hasAdditionalCache)
            {
                statuses = _cachedAdditional;
                error = _cachedAdditionalError;
                return _cachedAdditionalOk;
            }

            _cachedAdditionalOk = BuildAdditionalStatuses(out _cachedAdditional, out _cachedAdditionalError);
            _hasAdditionalCache = true;
            statuses = _cachedAdditional;
            error = _cachedAdditionalError;
            return _cachedAdditionalOk;
        }

        private static bool BuildAdditionalStatuses(out List<ComponentStatus> statuses, out string error)
        {
            statuses = new List<ComponentStatus>();
            error = null;

            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok)
            {
                error = load.Status == CatalogLoadStatus.NotInstalled
                    ? MetadataUnavailableMessage(CAS.Publishing.Installer.MetadataAutoInstall.InstallInFlight,
                        "Catalog metadata package is not installed yet — live component status is unavailable.")
                    : $"Catalog could not be read: {load.Error}";
                return false;
            }

            var report = GetScan(load.Catalog);

            var pkgById = new Dictionary<string, PackageScanResult>();
            if (report.Packages != null)
                foreach (var p in report.Packages)
                    if (p != null) pkgById[p.Id] = p;

            var extById = new Dictionary<string, ExternalScanResult>();
            if (report.External != null)
                foreach (var e in report.External)
                    if (e != null) extById[e.Id] = e;

            // Category id → human display name, so the Sub line reads like the defaults' hand-written
            // one ("Ads / Mediation") instead of a raw catalog category id ("ads").
            var categoryNames = new Dictionary<string, string>();
            if (load.Catalog.Categories != null)
                foreach (var cat in load.Catalog.Categories)
                    if (cat != null && !string.IsNullOrEmpty(cat.Id))
                        categoryNames[cat.Id] = cat.DisplayName;

            var defaultIds = new HashSet<string>(DefaultIds);
            // First-wins dedup: an id could in principle appear in both catalog.Packages and
            // catalog.External (catalog authoring mistake) — never list it twice.
            var seenIds = new HashSet<string>();

            var manifest = ManifestProbe.Read();
            System.Func<string, bool> embedded = id =>
                System.IO.Directory.Exists(System.IO.Path.Combine(
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "..")),
                    "Packages", id));

            if (load.Catalog.Packages != null)
                foreach (var rec in load.Catalog.Packages)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.Id)) continue;
                    if (defaultIds.Contains(rec.Id) || IsOwnPackage(rec.Id) || IsHiddenComponent(rec.Id)) continue;
                    if (!seenIds.Add(rec.Id)) continue;
                    var d = ToDescriptor(rec.Id, rec.DisplayName, rec.Category, categoryNames);
                    var status = pkgById.TryGetValue(rec.Id, out var p) ? FromPackage(d, p) : NotInCatalog(d);
                    var missing = RequirementGate.FirstMissing(rec.Requires, manifest.Dependencies, embedded);
                    if (IsHiddenForMissingRequirement(missing, status.Installed))
                        continue;
                    statuses.Add(status);
                }

            if (load.Catalog.External != null)
                foreach (var rec in load.Catalog.External)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.Id)) continue;
                    if (defaultIds.Contains(rec.Id) || IsOwnPackage(rec.Id) || IsHiddenComponent(rec.Id)) continue;
                    if (!seenIds.Add(rec.Id)) continue;
                    var d = ToDescriptor(rec.Id, rec.DisplayName, rec.Category, categoryNames);
                    if (extById.TryGetValue(rec.Id, out var e))
                    {
                        var s = FromExternal(d, e);
                        PromoteLegacySplit(s, e, report);
                        statuses.Add(s);
                    }
                    else
                        statuses.Add(NotInCatalog(d));
                }

            return true;
        }

        /// <summary>A package the catalog retires (its <c>uninstall</c> rules) that is still in manifest.json.</summary>
        internal sealed class RetiredPackage
        {
            public string Id;
            public string Version;
            public string Reason;

            /// <summary>
            /// Non-empty for a manual (.unitypackage) copy in Assets/ — removing it means deleting these
            /// Assets-relative paths, not editing manifest.json.
            /// </summary>
            public List<string> Paths;

            public bool IsManualCopy => Paths != null && Paths.Count > 0;
        }

        /// <summary>
        /// Retired packages still in the project — e.g. the old Tenjin wrapper, whose job CAS does
        /// itself now and which drags in an outdated Unity IAP. The Ready and Components screens list
        /// them and the recommended install removes them: before, only the legacy-Firebase migration
        /// ever applied these rules, so a project with CAS and Firebase in place kept them forever.
        /// Empty when the catalog is unavailable.
        /// </summary>
        public static List<RetiredPackage> GetRetired()
        {
            var retired = new List<RetiredPackage>();
            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok) return retired;
            retired = RetiredFrom(load.Catalog, GetScan(load.Catalog));
            // A file walk over Assets/ — cached like the scan, dropped by InvalidateCache / a reload.
            if (_cachedManualRetired == null) _cachedManualRetired = ManualRetiredCopies(load.Catalog);
            retired.AddRange(_cachedManualRetired);
            return retired;
        }

        private static List<RetiredPackage> _cachedManualRetired;

        /// <summary>
        /// Manual (.unitypackage) copies of retired packages under Assets/ — e.g. the Tenjin Unity
        /// plugin in Assets/Tenjin, which never shows up in manifest.json (QA 32). A copy counts only
        /// when at least one of the record's signature files is found: a folder name alone could be the
        /// game's own folder. The paths are the declared roots that hold a signature file, plus the
        /// signature files scattered elsewhere.
        /// </summary>
        internal static List<RetiredPackage> ManualRetiredCopies(PackageCatalog catalog)
        {
            var copies = new List<RetiredPackage>();
            if (catalog?.Uninstall == null) return copies;

            foreach (var record in catalog.Uninstall)
            {
                if (record?.LegacyAssetFiles == null || record.LegacyAssetFiles.Count == 0) continue;
                if (record.LegacyNpmIds == null || record.LegacyNpmIds.Count == 0) continue;

                var hits = AssetInstallProbe.FindSignatureFiles(record.LegacyAssetFiles, null);
                if (hits.Count == 0) continue;

                var paths = new List<string>();
                foreach (var root in AssetProbe.FindExisting(record.AssetRoots))
                {
                    var prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
                    if (hits.Exists(h => h.Replace('\\', '/').StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)))
                        paths.Add(root);
                }
                foreach (var f in AssetInstallProbe.FindSignatureFiles(record.LegacyAssetFiles, paths))
                    if (!paths.Contains(f)) paths.Add(f);

                copies.Add(new RetiredPackage
                {
                    Id = record.LegacyNpmIds[0],
                    Reason = record.Reason,
                    Paths = paths,
                });
            }
            return copies;
        }

        internal static List<RetiredPackage> RetiredFrom(PackageCatalog catalog, ScanReport report)
        {
            var retired = new List<RetiredPackage>();
            if (report?.Uninstalls == null) return retired;

            foreach (var u in report.Uninstalls)
            {
                if (u == null || u.State != UninstallState.InstalledNeedsRemoval) continue;
                retired.Add(new RetiredPackage
                {
                    Id = u.LegacyNpmId,
                    Version = u.DetectedVersion,
                    Reason = ReasonFor(catalog, u.LegacyNpmId),
                });
            }
            return retired;
        }

        private static string ReasonFor(PackageCatalog catalog, string legacyId)
        {
            if (catalog?.Uninstall == null) return null;
            foreach (var record in catalog.Uninstall)
                if (record?.LegacyNpmIds != null && record.LegacyNpmIds.Contains(legacyId))
                    return record.Reason;
            return null;
        }

        /// <summary>An additional component whose prerequisite is absent and which isn't installed
        /// in any form is a dead end — the Hub can never install the prerequisite (git-distributed
        /// legacy package), so the row is hidden entirely rather than shown greyed-out. Anything
        /// already installed (including under a legacy id, i.e. a pending migration) keeps its real
        /// state visible.</summary>
        internal static bool IsHiddenForMissingRequirement(string missingRequirement, bool installed) =>
            missingRequirement != null && !installed;

        /// <summary>The installer's own packages (hub + metadata catalog) aren't user-facing
        /// "components" — never list them under Additional components.</summary>
        private static bool IsOwnPackage(string id) =>
            id.StartsWith(CAS.Publishing.Installer.Common.EditionInfo.InstallerPackageId, System.StringComparison.Ordinal);

        private static (string Id, string Name, string Sub, string Logo) ToDescriptor(
            string id, string displayName, string category, Dictionary<string, string> categoryNames)
        {
            var name = string.IsNullOrEmpty(displayName) ? id : displayName;
            var sub = string.Empty;
            if (!string.IsNullOrEmpty(category))
                sub = categoryNames.TryGetValue(category, out var catName) && !string.IsNullOrEmpty(catName)
                    ? catName
                    : category;
            return (id, name, sub, "generic"); // no per-package logo data in the catalog — generic icon
        }

        /// <summary>
        /// Best-effort "recommended" (falling back to "min") version for <paramref name="id"/> from
        /// the live catalog — used only to render the Update-row hint ("to v&lt;version&gt;"). Returns
        /// null when the catalog is unavailable or <paramref name="id"/> isn't a catalog entry, which
        /// simply hides the hint (no hard failure for a cosmetic detail).
        /// </summary>
        /// <summary>
        /// The catalog requires this package to install from git (<c>"installMethod": "git"</c>).
        /// Used to NOT offer "Connect to Hub": for such a record, moving to the registry means
        /// removing a working git dependency in exchange for a version the registry doesn't have.
        /// A <c>registryFirst</c> record stops counting once the registry has a version that qualifies.
        /// </summary>
        public static bool IsGitSourced(string id)
        {
            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok || load.Catalog == null) return false;

            if (load.Catalog.Packages != null)
                foreach (var p in load.Catalog.Packages)
                    if (p != null && p.Id == id)
                        return MigrationPlanner.ResolveMethod(p.InstallMethodName, InstallMethod.Upm) == InstallMethod.Git;

            if (load.Catalog.External != null)
                foreach (var e in load.Catalog.External)
                    if (e != null && e.Id == id)
                        return MigrationPlanner.InstallsFromGit(e, load.Catalog);

            return false;
        }

        public static string ResolveRecommendedVersion(string id)
        {
            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok || load.Catalog == null) return null;

            if (load.Catalog.Packages != null)
                foreach (var p in load.Catalog.Packages)
                    if (p != null && p.Id == id)
                        return !string.IsNullOrEmpty(p.RecommendedVersion) ? p.RecommendedVersion : p.MinVersion;

            if (load.Catalog.External != null)
                foreach (var e in load.Catalog.External)
                    if (e != null && e.Id == id)
                        return !string.IsNullOrEmpty(e.RecommendedVersion) ? e.RecommendedVersion : e.MinVersion;

            return null;
        }

        private static ComponentStatus Base(in (string Id, string Name, string Sub, string Logo) d) =>
            new ComponentStatus { Id = d.Id, InstalledId = d.Id, DisplayName = d.Name, Sub = d.Sub, Logo = d.Logo };

        internal static ComponentStatus FromPackage(in (string Id, string Name, string Sub, string Logo) d, PackageScanResult p)
        {
            var s = Base(d);
            s.Version = p.DetectedVersion;
            s.Installed = p.State != PackageState.NotInstalled;
            // When the package is present under a legacy npm id (LegacyUpm), Remove must target that
            // id — removing the canonical id would be a silent no-op.
            if (!string.IsNullOrEmpty(p.DetectedLegacyNpmId))
                s.InstalledId = p.DetectedLegacyNpmId;
            switch (p.State)
            {
                case PackageState.UpmCurrent:
                    s.Tone = "green";  s.StatusText = "Installed";       s.ActionText = "Up to date"; s.ActionVariant = "muted";   s.Actionable = false; break;
                case PackageState.UpmOutdated:
                    s.Tone = "yellow"; s.StatusText = "Update available"; s.ActionText = "Update";    s.ActionVariant = "warn";    s.Actionable = true;  break;
                case PackageState.UpmBelowMin:
                    s.Tone = "red";    s.StatusText = "Too old";          s.ActionText = "Update";    s.ActionVariant = "warn";    s.Actionable = true;  break;
                case PackageState.LegacyUpm:
                case PackageState.LegacyAssets:
                    s.Tone = "yellow"; s.StatusText = "Needs migration";  s.ActionText = "Migrate";   s.ActionVariant = "warn";    s.Actionable = true;  break;
                case PackageState.Conflict:
                    s.Tone = "red";    s.StatusText = "Mixed install";    s.ActionText = "Fix";       s.ActionVariant = "warn";    s.Actionable = true;  break;
                case PackageState.ScopeMissing:
                    s.Tone = "yellow"; s.StatusText = "Needs registry"; s.ActionText = "Fix";      s.ActionVariant = "warn";    s.Actionable = true;  break;
                case PackageState.NotInstalled:
                default:
                    s.Tone = "red";    s.StatusText = "Not Installed";    s.ActionText = "Install";   s.ActionVariant = "primary"; s.Actionable = true;  break;
            }
            if (CAS.Publishing.Installer.Scanner.StateClassifier.IsGitSpec(s.Version))
            {
                // A deliberate git install is valid however its ref compares to the catalog pin.
                // Evaluate this regardless of State so an UpmOutdated/UpmBelowMin git package never
                // shows an "Update"/"Too old" action that would overwrite the git URL with a semver.
                s.StatusText = "Installed (git)";
                s.ActionText = "Up to date"; s.ActionVariant = "muted"; s.Actionable = false;
            }
            return s;
        }

        internal static ComponentStatus FromExternal(in (string Id, string Name, string Sub, string Logo) d, ExternalScanResult e)
        {
            var s = Base(d);
            s.Version = e.DetectedVersion;
            s.Installed = e.State != ExternalState.NotInstalled;
            // A legacy package provides the SDK under a different manifest id (e.g. com.psv.tenjin).
            // Remove must target the id actually in the manifest, not the canonical catalog id.
            if (e.State == ExternalState.InstalledLegacy && !string.IsNullOrEmpty(e.DetectedLegacyId))
                s.InstalledId = e.DetectedLegacyId;
            switch (e.State)
            {
                case ExternalState.UpmCurrent:
                    s.Tone = "green";  s.StatusText = "Installed";      s.ActionText = "Up to date"; s.ActionVariant = "muted";   s.Actionable = false; break;
                case ExternalState.ScopeMissing:
                    s.Tone = "yellow"; s.StatusText = "Needs registry"; s.ActionText = "Fix";        s.ActionVariant = "warn";    s.Actionable = true;  break;
                case ExternalState.InstalledOutsideUpm:
                    s.Tone = "yellow"; s.StatusText = "Installed (manual)"; s.ActionText = "Migrate to UPM"; s.ActionVariant = "warn"; s.Actionable = true;
                    s.OutsideUpm = true; break;
                case ExternalState.InstalledLegacy:
                    // A legacy package already provides this SDK — report it as installed, no action
                    // (installing the canonical id would duplicate the SDK / break the legacy wrapper).
                    s.Tone = "green"; s.StatusText = "Installed (legacy)";
                    s.ActionText = e.DetectedLegacyId ?? "legacy package"; s.ActionVariant = "muted"; s.Actionable = false; break;
                case ExternalState.NotInstalled:
                default:
                    s.Tone = "red";    s.StatusText = "Not Installed";  s.ActionText = "Install";    s.ActionVariant = "primary"; s.Actionable = true;  break;
            }
            if (CAS.Publishing.Installer.Scanner.StateClassifier.IsGitSpec(s.Version) && s.StatusText == "Installed")
            {
                // Git-installed: a valid install, but offer an explicit Switch to UPM rather than a
                // misleading "Up to date"/Fix. (Switch is optional — a muted, non-warning action.)
                s.StatusText = "Installed (git)";
                s.ActionText = "Switch to UPM";
                s.ActionVariant = "muted";
                s.Actionable = true;
                s.GitInstalled = true;
            }
            return s;
        }

        /// <summary>
        /// Overrides an <see cref="ExternalState.InstalledLegacy"/> status to an actionable
        /// migration when the detected legacy id is covered by a compound split group (e.g.
        /// <c>com.psv.firebase.base</c> → Firebase Analytics/RemoteConfig adapters). Without this,
        /// the Firebase Main-components row renders "Installed (legacy)" with no button — a dead
        /// end, since the wrapper still provides the SDK under a split group the user has no way
        /// to trigger from that row. <see cref="ComponentStatus.InstalledId"/> is left untouched
        /// (already set to the legacy id by <see cref="FromExternal"/>) so Remove keeps targeting
        /// it. No-op when <paramref name="e"/> isn't InstalledLegacy or no split group matches
        /// (e.g. Tenjin's legacy wrapper, which has no split) — that case keeps the existing
        /// "Installed (legacy)" / no-action behavior.
        /// </summary>
        internal static void PromoteLegacySplit(ComponentStatus s, ExternalScanResult e, ScanReport report)
        {
            if (s == null || e == null || report == null) return;
            if (e.State != ExternalState.InstalledLegacy) return;
            if (report.SplitGroups == null) return;

            foreach (var g in report.SplitGroups)
            {
                if (g == null || g.LegacyId != e.DetectedLegacyId) continue;
                s.Tone = "yellow";
                s.StatusText = "Needs migration";
                s.ActionText = "Migrate";
                s.ActionVariant = "warn";
                s.Actionable = true;
                return;
            }
        }

        private static ComponentStatus NotInCatalog(in (string Id, string Name, string Sub, string Logo) d)
        {
            var s = Base(d);
            s.Tone = "grey"; s.StatusText = "Not in catalog"; s.ActionText = "—"; s.ActionVariant = "muted"; s.Actionable = false;
            return s;
        }
    }
}
