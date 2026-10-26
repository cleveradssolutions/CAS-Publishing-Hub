using System;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// The platform the user picked on Welcome, and whether it still matches the project's active
    /// build target. The choice lives in <see cref="InstallerKeyStore"/> (EditorPrefs), so it
    /// survives the domain reloads that happen between wizard steps.
    /// </summary>
    internal static class PlatformChoice
    {
        /// <summary>
        /// True when the user picked a platform and it differs from the active build target.
        /// An empty choice is never a mismatch: there is nothing to compare against, and the
        /// Configuration screen must stay on its existing "Other" branch rather than claim a
        /// mismatch it cannot name. Pure/testable.
        /// </summary>
        internal static bool IsMismatch(string selected, string activeOrOther)
        {
            if (string.IsNullOrEmpty(selected)) return false;
            return !string.Equals(selected, activeOrOther, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The picked platform ("Android" | "iOS"), or an empty string when never chosen.</summary>
        public static string Selected => InstallerKeyStore.GetSelectedPlatform();

        /// <summary>Persists the user's pick.</summary>
        /// <summary>
        /// Stores the chosen platform and clears the configuration step queue: requirements are
        /// evaluated per platform, so a queue built for the previous one no longer describes the work
        /// left. It is rebuilt after the install.
        /// </summary>
        public static void Select(string platform)
        {
            InstallerKeyStore.SetSelectedPlatform(platform);
            InstallerKeyStore.SetConfigQueue(null);
            InstallerKeyStore.SetConfigReturnTo(null);
        }
    }
}
