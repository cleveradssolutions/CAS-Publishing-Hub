using UnityEditor;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Shared state for the About-tab "update available" badge. The window probes the registry
    /// and paints the badge; the About screen performs the self-update. They share these keys so
    /// the badge can be reset the moment an update is issued (SessionState survives the domain
    /// reload UPM triggers, so without a reset the badge would stay lit until the editor restarts).
    /// </summary>
    internal static class UpdateBadgeState
    {
        public const string PackageId    = CAS.Publishing.Installer.Common.EditionInfo.InstallerPackageId;
        public const string ProbedKey    = "CAS.Publishing.Installer.Wizard.UpdateBadgeProbed";
        public const string AvailableKey = "CAS.Publishing.Installer.Wizard.UpdateAvailable";

        /// <summary>Clear after a self-update so the next reload re-probes from scratch.</summary>
        public static void Reset()
        {
            SessionState.SetBool(ProbedKey, false);
            SessionState.SetBool(AvailableKey, false);
        }
    }
}
