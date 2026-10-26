using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CAS.Publishing.Installer.Common;
using UnityEditor;
using UnityEngine.Networking;

namespace CAS.Publishing.Installer.Catalog
{
    /// <summary>
    /// The highest version each <c>registryFirst</c> record's scoped registry publishes, looked up
    /// once per Editor session. The planner reads it to decide registry-or-git: a record that falls
    /// back to git (a CAS prerelease openupm doesn't carry) moves to the registry by itself once a
    /// version at or above its <c>minVersion</c> appears there — no catalog change needed.
    /// Kept in SessionState so the step-by-step install plans the same way across domain reloads.
    /// Not checked yet, or the lookup failed → no version → the record keeps its git source.
    /// </summary>
    internal static class RegistryVersions
    {
        private const string Key = "CAS.Publishing.Installer.RegistryVersions";
        private const double TimeoutSeconds = 5;

        // "<registry url>|<package id>" → highest published version ("" = checked, nothing usable).
        private static Dictionary<string, string> _cache;
        private static bool _refreshing;

        /// <summary>The registry version to install <paramref name="rec"/> from, or null to keep git:
        /// set only for a <c>registryFirst</c> record whose registry has a version ≥ its minVersion.</summary>
        public static string For(ExternalRecord rec, PackageCatalog catalog)
        {
            if (rec == null || !rec.RegistryFirst || string.IsNullOrEmpty(rec.Id)) return null;
            if (!Cache().TryGetValue(CacheKey(RegistryUrl(rec, catalog), rec.Id), out var best)) return null;
            if (string.IsNullOrEmpty(best)) return null;
            if (!string.IsNullOrEmpty(rec.MinVersion) && SemVer.IsVersion(rec.MinVersion) &&
                SemVer.Compare(best, rec.MinVersion) < 0)
                return null;
            return best;
        }

        /// <summary>Looks up every <c>registryFirst</c> record not checked this session, waiting up to
        /// a few seconds. Called right before a plan is built, so Install never races the lookup.</summary>
        public static void EnsureChecked(PackageCatalog catalog)
        {
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);
                var pending = new List<(string key, UnityWebRequest request)>();
                foreach (var (url, id) in Pending(catalog))
                    pending.Add((CacheKey(url, id), Send(url, id).webRequest));
                if (pending.Count == 0) return;

                foreach (var (_, request) in pending)
                    while (!request.isDone && DateTime.UtcNow < deadline)
                        System.Threading.Thread.Sleep(20);
                foreach (var (key, request) in pending)
                    Store(key, request);
            }
            catch (Exception) { /* a failed lookup only means "install from git" */ }
        }

        /// <summary>Non-blocking <see cref="EnsureChecked"/> for the hub window: <paramref name="onChanged"/>
        /// runs when a lookup finished, so rows can re-read whether a record still installs from git.</summary>
        public static void RefreshAsync(PackageCatalog catalog, Action onChanged)
        {
            if (_refreshing) return;
            List<(string url, string id)> pending;
            try { pending = Pending(catalog); }
            catch (Exception) { return; }
            if (pending.Count == 0) return;

            _refreshing = true;
            var left = pending.Count;
            foreach (var (url, id) in pending)
            {
                var key = CacheKey(url, id);
                UnityWebRequestAsyncOperation op;
                try { op = Send(url, id); }
                catch (Exception) { if (--left == 0) _refreshing = false; continue; }
                var request = op.webRequest;
                op.completed += _ =>
                {
                    Store(key, request);
                    if (--left > 0) return;
                    _refreshing = false;
                    try { onChanged?.Invoke(); }
                    catch (Exception) { }
                };
            }
        }

        /// <summary>Highest version in an npm registry document, or null.</summary>
        internal static string HighestVersion(string registryJson)
        {
            var versions = JObject.Parse(registryJson)["versions"] as JObject;
            if (versions == null) return null;
            string best = null;
            foreach (var p in versions.Properties())
                if (SemVer.IsVersion(p.Name) && (best == null || SemVer.Compare(p.Name, best) > 0))
                    best = p.Name;
            return best;
        }

        internal static void SetForTests(string registryUrl, string id, string version)
        {
            Cache()[CacheKey(registryUrl, id)] = version ?? "";
            Save();
        }

        internal static void ResetForTests()
        {
            _cache = new Dictionary<string, string>();
            SessionState.EraseString(Key);
        }

        /// <summary>The URL a record's <c>registry</c> key names (the raw value when it isn't a key).</summary>
        internal static string RegistryUrl(ExternalRecord rec, PackageCatalog catalog)
        {
            var key = rec?.Registry;
            if (string.IsNullOrEmpty(key)) return string.Empty;
            return catalog?.Registries != null && catalog.Registries.TryGetValue(key, out var url)
                ? url ?? string.Empty
                : key;
        }

        private static List<(string url, string id)> Pending(PackageCatalog catalog)
        {
            var list = new List<(string, string)>();
            if (catalog?.External == null) return list;
            foreach (var rec in catalog.External)
            {
                if (rec == null || !rec.RegistryFirst || string.IsNullOrEmpty(rec.Id)) continue;
                var url = RegistryUrl(rec, catalog);
                if (string.IsNullOrEmpty(url) || Cache().ContainsKey(CacheKey(url, rec.Id))) continue;
                list.Add((url, rec.Id));
            }
            return list;
        }

        private static UnityWebRequestAsyncOperation Send(string registryUrl, string id)
        {
            var request = UnityWebRequest.Get(registryUrl.TrimEnd('/') + "/" + id);
            request.timeout = (int)TimeoutSeconds;
            request.SetRequestHeader("Accept", "application/json");
            return request.SendWebRequest();
        }

        private static void Store(string key, UnityWebRequest request)
        {
            try
            {
                // Unfinished or failed: leave it unchecked, so the next plan asks again.
                if (!request.isDone || request.result != UnityWebRequest.Result.Success) return;
                string best = null;
                try { best = HighestVersion(request.downloadHandler.text); }
                catch (Exception) { }
                Cache()[key] = best ?? "";
                Save();
            }
            catch (Exception) { }
            finally
            {
                try { if (!request.isDone) request.Abort(); request.Dispose(); }
                catch (Exception) { }
            }
        }

        private static string CacheKey(string registryUrl, string id) =>
            (registryUrl ?? "").TrimEnd('/') + "|" + id;

        private static Dictionary<string, string> Cache()
        {
            if (_cache != null) return _cache;
            try
            {
                _cache = JsonConvert.DeserializeObject<Dictionary<string, string>>(SessionState.GetString(Key, ""));
            }
            catch (Exception) { }
            return _cache ??= new Dictionary<string, string>();
        }

        private static void Save()
        {
            try { SessionState.SetString(Key, JsonConvert.SerializeObject(_cache)); }
            catch (Exception) { }
        }
    }
}
