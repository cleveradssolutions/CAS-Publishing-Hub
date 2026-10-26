using CAS.Publishing.Installer;
using UnityEditor;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Registers the wizard as the installer UI that <see cref="Bootstrap"/> opens, replacing
    /// the legacy IMGUI window for the auto-popup. The legacy window has no menu item of its own —
    /// it remains reachable only as <see cref="Bootstrap"/>'s auto-popup fallback, used when
    /// <see cref="Bootstrap.ShowInstaller"/> is still null (e.g. this Wizard assembly failed to
    /// compile, so this static constructor never ran to set the hook). Runs at editor init
    /// (InitializeOnLoad), before Bootstrap's delayCall fires, so the hook is set in time.
    ///
    /// Also wires <see cref="MetadataRefreshWatcher.OnMetadataRegistered"/> — same plug-in
    /// pattern, in the other direction: the base Editor assembly can't reference this Wizard
    /// assembly, so it exposes a delegate slot that this static constructor fills in.
    /// </summary>
    [InitializeOnLoad]
    internal static class WizardBootstrap
    {
        static WizardBootstrap()
        {
            Bootstrap.ShowInstaller = InstallerWizardWindow.ShowIfReportChanged;
            MetadataRefreshWatcher.OnMetadataRegistered = () =>
            {
                ComponentStatusProvider.InvalidateCache();

                // Refreshing alone is only right when there IS a window. On a first-time bootstrap
                // install there is none — and no domain reload follows a data-only package install
                // to re-run Bootstrap — so that case has to open one instead. See MetadataArrivalRoute.
                switch (MetadataArrivalRoute.Decide(InstallerWizardWindow.HasOpenInstance()))
                {
                    case MetadataArrival.RefreshOpenWindow:
                        InstallerWizardWindow.RefreshActive();
                        break;
                    case MetadataArrival.OpenWizard:
                        InstallerWizardWindow.ShowAfterMetadataArrival();
                        break;
                }
            };
        }
    }
}
