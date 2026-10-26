namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>What to do when the metadata catalog package registers mid-session.</summary>
    internal enum MetadataArrival
    {
        /// <summary>A wizard window is already up — re-render it in place.</summary>
        RefreshOpenWindow,

        /// <summary>No window is up — this is the session's last chance to show one (see
        /// <see cref="MetadataArrivalRoute"/>), so open it.</summary>
        OpenWizard,
    }

    /// <summary>
    /// Routes the metadata-package-registered event. The metadata package is data-only, so
    /// installing it triggers NO domain reload: <c>Bootstrap.RunOnce</c> has already run for this
    /// session and returned early at <c>CatalogLoadStatus.NotInstalled</c>, and nothing will run it
    /// again until some unrelated reload happens. That makes this event the only remaining chance
    /// to show the wizard after a first-time bootstrap install — refreshing an already-open window
    /// is not enough, because on a first run there is no window to refresh.
    /// </summary>
    internal static class MetadataArrivalRoute
    {
        /// <summary>Pure: the decision itself, so it is unit-testable without an EditorWindow.</summary>
        internal static MetadataArrival Decide(bool wizardOpen) =>
            wizardOpen ? MetadataArrival.RefreshOpenWindow : MetadataArrival.OpenWizard;
    }
}
