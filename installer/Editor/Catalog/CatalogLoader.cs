using System;
using System.IO;
using Newtonsoft.Json;
using UnityEditor.PackageManager;
using UnityEngine;

namespace CAS.Publishing.Installer.Catalog
{
    /// <summary>Outcome of locating + reading the metadata catalog.</summary>
    public enum CatalogLoadStatus
    {
        /// <summary>Catalog found, parsed, and schema-compatible.</summary>
        Ok,
        /// <summary>Metadata package is not registered in this project (first run → install).</summary>
        NotInstalled,
        /// <summary>Metadata package IS present but catalog.json is missing/malformed/too-new.
        /// Must NOT trigger a reinstall — that would loop forever.</summary>
        Unreadable,
    }

    /// <summary>Result of <see cref="CatalogLoader.Load"/>.</summary>
    public readonly struct CatalogLoadResult
    {
        public CatalogLoadStatus Status { get; }
        public PackageCatalog Catalog { get; }
        public string Source { get; }
        public string Error { get; }

        private CatalogLoadResult(CatalogLoadStatus s, PackageCatalog c, string src, string err)
        { Status = s; Catalog = c; Source = src; Error = err; }

        public static CatalogLoadResult Ok(PackageCatalog c, string src) => new CatalogLoadResult(CatalogLoadStatus.Ok, c, src, null);
        public static CatalogLoadResult NotInstalled() => new CatalogLoadResult(CatalogLoadStatus.NotInstalled, null, null, null);
        public static CatalogLoadResult Unreadable(string src, string err) => new CatalogLoadResult(CatalogLoadStatus.Unreadable, null, src, err);
    }

    /// <summary>Outcome of parsing+validating raw catalog JSON (pure).</summary>
    public enum CatalogParseStatus { Ok, Malformed, UnsupportedSchema }

    internal static class CatalogLoader
    {
        public const string MetadataPackageName = CAS.Publishing.Installer.Common.EditionInfo.MetadataPackageId;
        public const string CatalogFileName = "catalog.json";

        /// <summary>Highest catalog schemaVersion this installer build understands.
        /// A catalog declaring a higher number is surfaced as Unreadable rather than parsed
        /// against assumptions that may no longer hold.</summary>
        public const int SupportedSchemaVersion = 1;

        // Session cache: GetAllRegisteredPackages() + the catalog.json read/parse are expensive and
        // Load() is called many times per wizard open (SetupModel, ComponentStatusProvider, the CAS
        // writers, validation…). The catalog only changes when the metadata package is installed/
        // updated, which triggers a domain reload that resets this static — so the cache is safe.
        // An explicit user Refresh calls InvalidateCache() to force a re-read.
        private static CatalogLoadResult? _cache;

        /// <summary>
        /// Locates the metadata package, reads catalog.json, and classifies the outcome.
        /// Cached for the session (see <see cref="InvalidateCache"/>). Never throws.
        /// NotInstalled → caller may install; Unreadable → caller must surface the error and NOT
        /// reinstall; Ok → use <see cref="CatalogLoadResult.Catalog"/>.
        /// </summary>
        public static CatalogLoadResult Load()
        {
            if (_cache.HasValue) return _cache.Value;
            _cache = LoadUncached();
            return _cache.Value;
        }

        /// <summary>Drops the cached catalog so the next <see cref="Load"/> re-reads from disk. Call
        /// after an explicit user Refresh. (Static caches also reset on domain reload after installs.)</summary>
        public static void InvalidateCache() => _cache = null;

        private static CatalogLoadResult LoadUncached()
        {
            foreach (var pkg in PackageInfo.GetAllRegisteredPackages())
            {
                if (pkg.name != MetadataPackageName) continue;

                var path = Path.Combine(pkg.resolvedPath, CatalogFileName);
                if (!File.Exists(path))
                    return CatalogLoadResult.Unreadable(path, $"{CatalogFileName} missing at {pkg.resolvedPath}");

                string json;
                try { json = File.ReadAllText(path); }
                catch (Exception e) { return CatalogLoadResult.Unreadable(path, $"read failed: {e.Message}"); }

                var status = ParseAndValidate(json, out var cat, out var err);
                return status == CatalogParseStatus.Ok
                    ? CatalogLoadResult.Ok(cat, path)
                    : CatalogLoadResult.Unreadable(path, err);
            }

            return CatalogLoadResult.NotInstalled();
        }

        /// <summary>
        /// Pure parse + schema-compatibility check. <paramref name="catalog"/> is non-null only
        /// when the return is <see cref="CatalogParseStatus.Ok"/>.
        /// </summary>
        public static CatalogParseStatus ParseAndValidate(string json, out PackageCatalog catalog, out string error)
        {
            catalog = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json)) { error = "catalog file is empty"; return CatalogParseStatus.Malformed; }

            PackageCatalog parsed;
            try { parsed = JsonConvert.DeserializeObject<PackageCatalog>(json); }
            catch (JsonException e) { error = e.Message; return CatalogParseStatus.Malformed; }

            if (parsed == null) { error = "catalog deserialised to null"; return CatalogParseStatus.Malformed; }

            if (parsed.SchemaVersion < 0)
            {
                error = $"catalog schemaVersion {parsed.SchemaVersion} is invalid (negative)";
                return CatalogParseStatus.Malformed;
            }

            if (parsed.SchemaVersion > SupportedSchemaVersion)
            {
                error = $"catalog schemaVersion {parsed.SchemaVersion} is newer than supported ({SupportedSchemaVersion}); update the installer";
                return CatalogParseStatus.UnsupportedSchema;
            }

            catalog = parsed;
            return CatalogParseStatus.Ok;
        }
    }
}
