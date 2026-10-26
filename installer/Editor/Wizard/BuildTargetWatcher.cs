using UnityEditor;
using UnityEditor.Build;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// When the active build target switches to Android or iOS and CAS is installed but that
    /// platform's CAS id isn't configured yet, auto-opens the wizard on the Configuration screen so
    /// the user can configure it (the screen re-scans and shows per-platform status itself — no
    /// platform id needs to be threaded through). Other targets are ignored. Implements Unity's
    /// build-target-changed callback (invoked by the Editor on a successful target switch).
    /// </summary>
    internal sealed class BuildTargetWatcher : IActiveBuildTargetChanged
    {
        public int callbackOrder => 0;

        // Coalesce rapid multi-switches (Android→iOS→Android within one tick) into a single deferred
        // check for the LATEST platform: store the pending platform and enqueue delayCall only once,
        // instead of += a fresh closure per switch (which would run the scan + open N times in a tick).
        private static string _pendingPlatform;
        private static bool _queued;

        public void OnActiveBuildTargetChanged(BuildTarget previous, BuildTarget newTarget)
        {
            // Any target, Other included: an open wizard redraws for it (below). Only Android/iOS
            // may also open the window.
            _pendingPlatform = newTarget == BuildTarget.Android || newTarget == BuildTarget.iOS
                ? PlatformDetect.FromBuildTarget(newTarget) // "Android" | "iOS"
                : "";
            if (_queued) return;
            _queued = true;

            // Defer the scan + window open to the next editor tick. Doing them synchronously inside the
            // build-target-change callback runs while the editor is mid-switch and triggers a flood of
            // off-main-thread UI Toolkit text errors (UITKTextJobSystem/GetFontAsset). delayCall fires
            // once, on the main thread, after the switch settles.
            EditorApplication.delayCall += Flush;
        }

        private static void Flush()
        {
            _queued = false;
            var platform = _pendingPlatform;
            _pendingPlatform = null;

            // The open window shows the old target until the user re-opened it (QA 59): Configure
            // and the switch prompt are drawn per active platform, so redraw the current screen.
            InstallerWizardWindow.RefreshIfShowing("configure");

            if (string.IsNullOrEmpty(platform)) return;

            if (!BuildSwitchPolicy.ShouldOpenOnSwitch(
                    CasPresence.IsInstalled(),
                    CasSettingsReader.ReadExisting(platform)))
                return;

            InstallerWizardWindow.OpenAtConfigure();
        }
    }
}
