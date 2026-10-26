using System;
using System.Collections.Generic;
using System.Linq;
using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Common;
using UnityEditor;
using UnityEditor.PackageManager;

namespace CAS.Publishing.Installer
{
    /// <summary>
    /// The metadata package install (catalog.json + package.json, no scripts) does NOT trigger a
    /// domain reload — so the session caches (<see cref="CatalogLoader"/>, and the Wizard
    /// assembly's ComponentStatusProvider) and any already-open wizard window all survive the
    /// install untouched, and nothing tells the UI to re-render. This watcher listens for the
    /// metadata package showing up in <see cref="Events.registeredPackages"/> and invalidates the
    /// catalog cache + nudges an open wizard window to refresh.
    ///
    /// Lives in the base Editor assembly (no reference to the Wizard assembly, mirroring
    /// <see cref="Bootstrap.ShowInstaller"/>) — the ComponentStatusProvider/
    /// InstallerWizardWindow.RefreshActive() half of the refresh is wired through
    /// <see cref="OnMetadataRegistered"/>, set by the Wizard assembly's WizardBootstrap.
    ///
    /// Subscribes exactly ONCE, in the static constructor, and never unsubscribes — this is a
    /// domain-lifetime watcher (unlike <see cref="MetadataInstallRetry"/>'s bounded arm/disarm).
    /// </summary>
    [InitializeOnLoad]
    internal static class MetadataRefreshWatcher
    {
        /// <summary>
        /// Wizard-assembly hook (set by WizardBootstrap, same pattern as
        /// <see cref="Bootstrap.ShowInstaller"/>). Invoked, after <see cref="CatalogLoader.InvalidateCache"/>,
        /// when the metadata package registers. Null when no UI assembly is present.
        /// </summary>
        internal static Action OnMetadataRegistered;

        static MetadataRefreshWatcher()
        {
            Events.registeredPackages += OnPackagesChanged;
        }

        private static void OnPackagesChanged(PackageRegistrationEventArgs args)
        {
            var names = args.added.Concat(args.changedTo).Select(p => p.name);
            if (!ContainsMetadataPackage(names)) return;

            CatalogLoader.InvalidateCache();
            OnMetadataRegistered?.Invoke();
        }

        /// <summary>
        /// Pure: true when <paramref name="names"/> contains the metadata package id. Extracted so
        /// the "is this our package" decision is unit-testable without constructing
        /// <see cref="PackageInfo"/>/<see cref="PackageRegistrationEventArgs"/> (both effectively
        /// un-constructable outside the Package Manager).
        /// </summary>
        internal static bool ContainsMetadataPackage(IEnumerable<string> names)
        {
            if (names == null) return false;
            foreach (var n in names)
                if (n == EditionInfo.MetadataPackageId) return true;
            return false;
        }
    }
}
