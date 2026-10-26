using UnityEditor;

namespace CAS.Publishing.Installer.Common
{
    /// <summary>
    /// Session-scoped, one-shot signal that an installer-driven manifest mutation occurred and a
    /// domain reload is expected. The wizard's auto-open consumes it to reopen after an install
    /// WITHOUT re-popping on unrelated manual UPM changes. Survives the reload (SessionState),
    /// resets on editor restart.
    /// </summary>
    public static class InstallReloadSignal
    {
        private const string Key = "CAS.Publishing.Installer.ExpectInstallReload";

        /// <summary>Mark that the installer just changed the manifest (a reload will follow).</summary>
        public static void MarkPending() => SessionState.SetBool(Key, true);

        /// <summary>Returns true if the flag was set, clearing it (one-shot).</summary>
        public static bool ConsumePending()
        {
            var pending = SessionState.GetBool(Key, false);
            if (pending) SessionState.EraseBool(Key);
            return pending;
        }
    }
}
