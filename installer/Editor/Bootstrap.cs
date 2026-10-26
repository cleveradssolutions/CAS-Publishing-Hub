using System;
using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Common;
using CAS.Publishing.Installer.Scanner;
using CAS.Publishing.Installer.Ui;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer
{
    [InitializeOnLoad]
    internal static class Bootstrap
    {
        private const string LogPrefix = "[CAS Hub]";
        private const string UpdateProbedKey = "CAS.Publishing.Installer.UpdateProbedThisSession";

        /// <summary>
        /// The UI layer plugs in here to open the installer when the scan report changes.
        /// This keeps core free of any reference to the wizard assembly (the dependency only
        /// goes wizard→core). When no UI has registered, we fall back to the legacy IMGUI window.
        /// </summary>
        internal static Action<ScanReport> ShowInstaller;

        static Bootstrap()
        {
            EditorApplication.delayCall += RunOnce;
        }

        private static void RunOnce()
        {
            EditorApplication.delayCall -= RunOnce;

            // Never run network/UI/manifest mutation in headless CI — it stalls and
            // mutates the committed manifest mid-build.
            if (Application.isBatchMode) return;

            var load = CatalogLoader.Load();
            switch (load.Status)
            {
                case CatalogLoadStatus.NotInstalled:
                    MetadataAutoInstall.Run();
                    return;

                case CatalogLoadStatus.Unreadable:
                    // Metadata IS present but catalog.json is broken/too-new — surface it,
                    // do NOT reinstall (that would loop every reload).
                    Debug.LogError(
                        $"{LogPrefix} Metadata catalog present but unreadable: {load.Error}. " +
                        "Not reinstalling — fix or manually reinstall " + EditionInfo.MetadataPackageId + ".");
                    return;
            }

            var catalog = load.Catalog;
            Debug.Log(
                $"{LogPrefix} Catalog v{catalog.CatalogVersion} loaded from {load.Source} " +
                $"({(catalog.Packages?.Count ?? 0)} packages, {(catalog.External?.Count ?? 0)} external).");

            MaybeAutoUpdate(load.Source, catalog);

            var report = ProjectScanner.Scan(catalog);

            // Open the registered UI (the wizard). Fall back to the legacy IMGUI window
            // only if no UI registered a handler (e.g. wizard assembly absent).
            if (ShowInstaller != null)
                ShowInstaller(report);
            else
                InstallerWindow.ShowIfReportChanged(report);
        }

        /// <summary>
        /// Manual "make metadata current" entry, used by the wizard (open + Refresh button):
        /// installs the metadata package if absent, otherwise re-checks the registry for a newer
        /// catalog. Bypasses the once-per-session throttles because the user explicitly asked.
        /// </summary>
        /// <summary>
        /// Marks the once-per-session metadata update-probe as done, WITHOUT probing. Called right
        /// after a fresh git metadata install so <see cref="MaybeAutoUpdate"/> doesn't immediately
        /// re-resolve the same git URL on the post-install reload — that redundant Client.Add queues
        /// an extra domain reload that can tear down the just-auto-opened wizard.
        /// </summary>
        internal static void SuppressMetadataUpdateThisSession() =>
            SessionState.SetBool(UpdateProbedKey, true);

        internal static void EnsureMetadata()
        {
            if (Application.isBatchMode) return;

            var load = CatalogLoader.Load();
            if (load.Status == CatalogLoadStatus.NotInstalled)
                MetadataAutoInstall.Run(force: true);
            else if (load.Status == CatalogLoadStatus.Ok)
                MaybeAutoUpdate(load.Source, load.Catalog, force: true);
        }

        private static void MaybeAutoUpdate(string path, PackageCatalog catalog, bool force = false)
        {
            // Embedded metadata (dev project) always wins over the registry copy →
            // Client.Add would loop. Skip auto-update there.
            var isEmbedded = path != null && !path.Replace('\\', '/').Contains("/Library/PackageCache/");
            if (isEmbedded)
            {
                Debug.Log($"{LogPrefix} Embedded metadata detected; skipping Verdaccio auto-update.");
                return;
            }

            // Git-installed installer → metadata comes from the git mirror's main branch. "Make
            // current" = re-add the git URL so Unity re-resolves main; there's no Verdaccio version
            // to probe. Honour the same once-per-session throttle (force bypasses it).
            if (CAS.Publishing.Installer.Common.InstallerSource.IsGit())
            {
                if (!force && SessionState.GetBool(UpdateProbedKey, false)) return;
                SessionState.SetBool(UpdateProbedKey, true);
                Debug.Log($"{LogPrefix} Re-resolving metadata from git mirror (main).");
                CatalogUpdater.TrackInstall(CatalogUpdater.InstallGit(), "Metadata (git)");
                return;
            }

            // Probe the registry at most once per editor session — prevents a warning on
            // every domain reload while offline. A manual trigger (force) re-checks anyway.
            if (!force && SessionState.GetBool(UpdateProbedKey, false)) return;
            SessionState.SetBool(UpdateProbedKey, true);

            CatalogUpdater.CheckRemoteLatestVersion(
                onSuccess: latest =>
                {
                    if (CatalogUpdater.IsNewer(latest, catalog.CatalogVersion))
                    {
                        Debug.Log($"{LogPrefix} Newer catalog available: {latest} (installed: {catalog.CatalogVersion}). Updating…");
                        CatalogUpdater.TrackInstall(CatalogUpdater.InstallVersion(latest), "Catalog");
                    }
                    else
                    {
                        Debug.Log($"{LogPrefix} Catalog is up to date ({catalog.CatalogVersion}).");
                    }
                },
                onFailure: err =>
                {
                    Debug.LogWarning($"{LogPrefix} Could not check registry for catalog updates: {err}");
                });
        }
    }
}
