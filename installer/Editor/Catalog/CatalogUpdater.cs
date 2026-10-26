using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using CAS.Publishing.Installer.Common;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine.Networking;

namespace CAS.Publishing.Installer.Catalog
{
    internal static class CatalogUpdater
    {
        public const string CasRegistryRoot = CAS.Publishing.Installer.Common.EditionInfo.RegistryUrl;

        /// <summary>
        /// Git mirror of the metadata catalog (an edition value — a test build can point it at a
        /// test mirror). Untagged → Unity resolves the repo's default branch (main = the latest
        /// mirror release), so new packages/rules reach git clients without an installer release
        /// (the same role the registry "latest" plays).
        /// </summary>
        public const string MetadataGitUrl = CAS.Publishing.Installer.Common.EditionInfo.MetadataGitUrl;

        private const int TimeoutSeconds = 10;

        // Reports the HIGHEST published version of the metadata package. Delegates to
        // CheckLatestVersion so first-time install (MetadataAutoInstall) and auto-update
        // (Bootstrap) both pick the newest version — robust against the dist-tags.latest pin
        // not advancing for prereleases on Verdaccio. Non-blocking.
        public static void CheckRemoteLatestVersion(Action<string> onSuccess, Action<string> onFailure = null)
        {
            CheckLatestVersion(CatalogLoader.MetadataPackageName, onSuccess, onFailure);
        }

        // Queues a UPM Add for the given metadata version. Caller may track the
        // request status if needed; for the auto-refresh path we fire-and-forget.
        public static AddRequest InstallVersion(string version)
        {
            return Client.Add($"{CatalogLoader.MetadataPackageName}@{version}");
        }

        // Queues a UPM Add for the metadata package via its git mirror (no registry, no version).
        public static AddRequest InstallGit()
        {
            return Client.Add(MetadataGitUrl);
        }

        /// <summary>
        /// Fetches the registry document for <paramref name="packageName"/> and reports the
        /// HIGHEST published version (by SemVer) — robust against the dist-tags.latest pin not
        /// advancing for prereleases. Non-blocking.
        /// </summary>
        public static void CheckLatestVersion(string packageName, Action<string> onSuccess, Action<string> onFailure = null)
        {
            var url = CasRegistryRoot + packageName;
            var request = UnityWebRequest.Get(url);
            request.timeout = TimeoutSeconds;
            request.SetRequestHeader("Accept", "application/json");

            var op = request.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        onFailure?.Invoke(request.error ?? request.result.ToString());
                        return;
                    }

                    var doc = JsonConvert.DeserializeObject<RegistryPackageDocument>(request.downloadHandler.text);
                    string best = null;
                    if (doc?.Versions != null)
                        foreach (var v in doc.Versions.Keys)
                            if (SemVer.IsVersion(v) && (best == null || SemVer.Compare(v, best) > 0))
                                best = v;

                    if (string.IsNullOrEmpty(best)) onFailure?.Invoke("no published versions found");
                    else onSuccess(best);
                }
                catch (Exception e)
                {
                    onFailure?.Invoke(e.Message);
                }
                finally
                {
                    request.Dispose();
                }
            };
        }

        // Compares two semver-ish strings via the shared SemVer comparator.
        // Non-version specs (file:/git/https/latest) are never "newer".
        public static bool IsNewer(string remote, string local)
        {
            if (string.IsNullOrEmpty(remote) || string.IsNullOrEmpty(local)) return false;
            if (!SemVer.IsVersion(remote)) return false;
            return SemVer.Compare(remote, local) > 0;
        }

        // Polls an AddRequest to completion on the editor update loop and logs the outcome,
        // so a failed/slow Client.Add surfaces instead of vanishing (fire-and-forget).
        // The subscription is bounded: exactly one delegate per call, removed on completion,
        // and any never-completing request's delegate is cleared by the next domain reload.
        //
        // onComplete is an optional completion hook (success flag), invoked exactly once when the
        // request settles — success OR failure. Used by MetadataAutoInstall to clear its
        // InstallInFlight flag: the async Client.Add completion happens entirely inside this
        // Poll() closure, so without this hook a terminal async failure would leave InstallInFlight
        // stuck true until the next domain reload.
        public static void TrackInstall(AddRequest request, string label, Action<bool> onComplete = null)
        {
            if (request == null) { onComplete?.Invoke(false); return; }

            void Poll()
            {
                if (!request.IsCompleted) return;
                UnityEditor.EditorApplication.update -= Poll;

                var success = request.Status == StatusCode.Success;
                if (success)
                {
                    UnityEngine.Debug.Log($"[CAS Hub] {label} installed: {request.Result?.packageId}");
                }
                else
                {
                    // Metadata installs (labels "Metadata" / "Metadata (git)") get the same
                    // transient self-heal as the synchronous Client.Add throw in
                    // MetadataAutoInstall — an async install failure due to a busy Package
                    // Manager must not strand metadata (and the wizard auto-open) for the rest
                    // of the session.
                    var message = request.Error?.message;
                    var isMetadata = label != null &&
                                      label.StartsWith("Metadata", StringComparison.Ordinal);
                    var transient = isMetadata && CAS.Publishing.Installer.InstallRetryPolicy.IsTransient(message);
                    if (transient) CAS.Publishing.Installer.MetadataInstallRetry.Arm();

                    UnityEngine.Debug.LogWarning($"[CAS Hub] {label} install failed: {message}" +
                        (transient ? " — retrying shortly" : ""));
                }

                onComplete?.Invoke(success);
            }

            UnityEditor.EditorApplication.update += Poll;
        }

        private sealed class RegistryPackageDocument
        {
            [JsonProperty("name")]
            public string Name;

            [JsonProperty("dist-tags")]
            public Dictionary<string, string> DistTags;

            /// <summary>All published versions (keys are the version strings).</summary>
            [JsonProperty("versions")]
            public Dictionary<string, object> Versions;
        }
    }
}
