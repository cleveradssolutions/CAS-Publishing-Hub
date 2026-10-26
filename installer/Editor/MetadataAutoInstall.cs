using System;
using System.IO;
using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Common;
using CAS.Publishing.Installer.Migrator;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace CAS.Publishing.Installer
{
    internal static class MetadataAutoInstall
    {
        private const string LogPrefix = "[CAS Hub]";
        private const string CasRegistryName = EditionInfo.RegistryName;
        private const string CasRegistryUrl = CatalogUpdater.CasRegistryRoot;
        private const string RequiredScope = EditionInfo.RegistryScope;
        private const string InstallAttemptedKey = "CAS.Publishing.Installer.MetadataInstallAttemptedThisSession";

        // Reload-proof registry-probe failure counter. MetadataInstallRetry's _attempts/_armed are
        // per-domain-reload statics (its own comments call this out) — they reset on EVERY domain
        // reload, so under frequent reloads (active recompilation while offline, fresh-project
        // import churn — the exact scenario that file is written for) its bounded per-epoch budget
        // would never actually exhaust, and the InstallAttemptedKey throttle below (which only
        // engages once that budget is exhausted) would never engage either — probing the registry
        // unboundedly for the whole offline session instead of the old once-per-session behaviour.
        // SessionState survives domain reloads (resets only on editor restart, same lifetime as
        // InstallAttemptedKey), so THIS counter tracks the TOTAL probe-failure count across the
        // whole session regardless of how many reloads happened in between.
        private const string ProbeFailuresKey = "CAS.Publishing.Installer.MetadataProbeFailures";

        // Matches MetadataInstallRetry.MaxAttempts by design intent (both bound "how many times do
        // we hit the registry before giving up for a while") but is kept as its own constant since
        // the two caps bound different things — MetadataInstallRetry.MaxAttempts bounds retries
        // WITHIN one domain-reload epoch, this bounds probe failures across the WHOLE session. If
        // the retry budget is ever retuned, change both.
        private const int MaxProbeFailures = 5;

        /// <summary>
        /// True while a metadata install attempt (git or registry path) is in flight — set right
        /// before the Client.Add call is issued, cleared on the synchronous throw path, the
        /// registry-probe failure, and the async Client.Add completion (success or failure alike,
        /// via <see cref="CatalogUpdater.TrackInstall"/>'s completion hook). Domain reload also
        /// resets it (static) — acceptable: the attempt either finished or was interrupted, and
        /// Bootstrap re-enters on the next reload either way. The wizard UI (ComponentStatusProvider/
        /// SetupModel) reads this to show "Installing catalog metadata…" instead of the
        /// not-installed error+button while NotInstalled.
        /// </summary>
        internal static bool InstallInFlight { get; private set; }

        /// <summary>
        /// Called by Bootstrap when <see cref="CatalogLoader.Load"/> reports NotInstalled (metadata
        /// package not yet registered in this project). Ensures the hub scoped registry is present in
        /// Packages/manifest.json, then queries Verdaccio for the latest metadata version and
        /// fires a UPM Client.Add. Fire-and-forget — Unity will reimport and Bootstrap will
        /// succeed on the next domain reload. Idempotent across multiple calls within the same
        /// domain reload.
        /// </summary>
        /// <param name="force">A manual trigger (wizard Refresh / open) clears the once-per-session
        /// failure throttle so the install is actually re-attempted now.</param>
        public static void Run(bool force = false)
        {
            // Already registered → nothing to do.
            if (IsMetadataInstalled()) return;

            if (force)
            {
                SessionState.SetBool(InstallAttemptedKey, false);
                // A manual trigger (wizard "Install Metadata" / Refresh) gets a fresh probe-failure
                // budget too — otherwise a session that already burned through MaxProbeFailures
                // offline would have the button silently re-latch on its very first click.
                SessionState.SetInt(ProbeFailuresKey, 0);
            }

            // The once-per-session guard exists ONLY to stop re-probing the registry on every domain
            // reload when offline/auth fails — it is set only on a genuine failure (below), never up
            // front, so a successful install or a remove+reinstall in the same session isn't blocked
            // until an editor restart.
            if (SessionState.GetBool(InstallAttemptedKey, false))
                return;

            // Git-installed installer → fetch metadata over git too (no scoped registry, no Verdaccio
            // probe). Keeps a fully-git client free of any EditionInfo.RegistryScope scoped registry.
            if (InstallerSource.IsGit())
            {
                Debug.Log($"{LogPrefix} metadata not detected; installing via git mirror…");
                try
                {
                    InstallInFlight = true;
                    // Success: do NOT set the guard — IsMetadataInstalled() short-circuits future calls.
                    CatalogUpdater.TrackInstall(CatalogUpdater.InstallGit(), "Metadata (git)",
                        onComplete: _ => InstallInFlight = false);
                    // We just resolved metadata from the git mirror's main — don't let the post-install
                    // reload's MaybeAutoUpdate re-resolve the same URL and queue a redundant reload that
                    // could tear down the just-auto-opened wizard.
                    CAS.Publishing.Installer.Bootstrap.SuppressMetadataUpdateThisSession();
                }
                catch (Exception e)
                {
                    InstallInFlight = false;
                    // A brand-new project's initial import keeps the Package Manager busy, so this
                    // first Add often throws a transient "exclusive access … in progress" collision.
                    // Latching the session throttle on that would strand metadata (and the auto-open)
                    // for the whole session — only throttle on a terminal failure; retry transient ones
                    // on the next domain reload.
                    var transient = InstallRetryPolicy.IsTransient(e.Message);
                    if (!transient) SessionState.SetBool(InstallAttemptedKey, true);
                    if (transient) MetadataInstallRetry.Arm();
                    Debug.LogWarning($"{LogPrefix} Metadata git Client.Add failed " +
                                     $"({(transient ? "transient — retrying shortly" : "terminal — throttled until restart")}): {e.Message}");
                }
                return;
            }

            Debug.Log($"{LogPrefix} metadata package not detected; installing…");

            // Step 1 — ensure scoped registry in manifest.json.
            if (!EnsureScopedRegistry())
            {
                SessionState.SetBool(InstallAttemptedKey, true); // failed — throttle until restart
                return; // warning already logged inside
            }

            // Step 2 — query Verdaccio for latest version, then install.
            InstallInFlight = true;
            CatalogUpdater.CheckRemoteLatestVersion(
                onSuccess: version =>
                {
                    // The registry probe itself succeeded — reset the reload-proof failure budget
                    // (a subsequent Client.Add failure below is a different, already-independently-
                    // classified transient/terminal failure, not a probe failure).
                    SessionState.SetInt(ProbeFailuresKey, 0);
                    Debug.Log($"{LogPrefix} Installing metadata package: {CatalogLoader.MetadataPackageName}@{version}…");
                    try
                    {
                        // Success path: do NOT set the guard — once installed, IsMetadataInstalled()
                        // short-circuits future calls; if it's later removed we want to retry.
                        CatalogUpdater.TrackInstall(
                            CatalogUpdater.InstallVersion(version), "Metadata",
                            onComplete: _ => InstallInFlight = false);
                    }
                    catch (Exception e)
                    {
                        InstallInFlight = false;
                        // Same transient/terminal split as the git path — a PM-busy collision must not
                        // burn the session's one install attempt.
                        var transient = InstallRetryPolicy.IsTransient(e.Message);
                        if (!transient) SessionState.SetBool(InstallAttemptedKey, true);
                        if (transient) MetadataInstallRetry.Arm();
                        Debug.LogWarning($"{LogPrefix} Client.Add failed " +
                                         $"({(transient ? "transient — retrying shortly" : "terminal — throttled until restart")}): {e.Message}");
                    }
                },
                onFailure: err =>
                {
                    InstallInFlight = false;

                    // A registry probe failure is typically transient (offline / DNS hiccup) — arm a
                    // short bounded auto-retry (see MetadataInstallRetry: ≤5 attempts/5s) so
                    // recovering network doesn't require an editor restart to notice. But
                    // MetadataInstallRetry's attempt count is a per-domain-reload static, so under
                    // frequent reloads it would never itself exhaust — track the TOTAL failure count
                    // in SessionState (reload-proof) and use THAT to decide when to give up for the
                    // session, instead of trusting Arm()/IsArmed (which a reload can silently reset
                    // back to "still armable", letting probing continue unbounded across reloads).
                    var failures = SessionState.GetInt(ProbeFailuresKey, 0) + 1;
                    SessionState.SetInt(ProbeFailuresKey, failures);

                    var latch = ShouldLatchAfterProbeFailure(failures, MaxProbeFailures);
                    if (latch)
                        // Reload-proof budget exhausted — throttle until restart. Deliberately does NOT
                        // also call Arm(): the whole point is to stop probing, and Run()'s own throttle
                        // check at the top would make an armed retry no-op here anyway.
                        SessionState.SetBool(InstallAttemptedKey, true);
                    else
                        MetadataInstallRetry.Arm();

                    Debug.LogWarning(
                        $"{LogPrefix} Could not query registry for metadata version " +
                        $"({failures}/{MaxProbeFailures} probe failures this session). " +
                        (latch
                            ? "Throttled until an editor restart. "
                            : "Will retry shortly. ") +
                        $"Reason: {err}");
                });
        }

        // -----------------------------------------------------------------------
        //  Helpers
        // -----------------------------------------------------------------------

        /// <summary>Pure: true once the reload-proof probe-failure count has reached
        /// <paramref name="cap"/> — the point at which <see cref="Run"/> gives up on
        /// <see cref="MetadataInstallRetry"/>'s bounded auto-retry and falls back to the
        /// once-per-session <see cref="InstallAttemptedKey"/> throttle. Extracted from the
        /// SessionState read/write so the decision itself is unit-testable.</summary>
        internal static bool ShouldLatchAfterProbeFailure(int failuresSoFar, int cap) => failuresSoFar >= cap;

        private static bool IsMetadataInstalled()
        {
            try
            {
                foreach (var pkg in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (pkg.name == CatalogLoader.MetadataPackageName)
                        return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogPrefix} Could not enumerate registered packages: {e.Message}");
            }
            return false;
        }

        /// <summary>
        /// Delegates to <see cref="ManifestWriter.ApplyActions"/> to ensure the hub
        /// scoped registry is registered in Packages/manifest.json.
        /// <see cref="ManifestWriter"/> is idempotent: if the registry and scope are
        /// already present it does not rewrite the file.
        /// Returns false on any I/O error (warning already logged).
        /// </summary>
        private static bool EnsureScopedRegistry()
        {
            var manifestPath = GetManifestPath();
            if (manifestPath == null)
            {
                Debug.LogWarning($"{LogPrefix} Could not locate Packages/manifest.json. Aborting bootstrap.");
                return false;
            }

            try
            {
                // AddScopedRegistry is idempotent in ManifestWriter: if the URL already
                // exists with the required scope, nothing is written.
                ManifestWriter.ApplyActions(manifestPath, new MigrationAction[]
                {
                    new AddScopedRegistry(CasRegistryName, CasRegistryUrl, RequiredScope),
                });
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogPrefix} Failed to ensure scoped registry in manifest.json: {e.Message}");
                return false;
            }
        }

        private static string GetManifestPath()
        {
            // Application.dataPath is "<project>/Assets". manifest.json is one level up under "Packages/".
            try
            {
                var projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath);
                if (projectRoot == null) return null;
                var path = Path.Combine(projectRoot, "Packages", "manifest.json");
                return File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
