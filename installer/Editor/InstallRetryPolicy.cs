using System;

namespace CAS.Publishing.Installer
{
    /// <summary>
    /// Classifies a UPM <c>Client.Add</c> failure as transient (retry on the next domain reload)
    /// versus terminal (throttle for the session). A brand-new project's initial import keeps the
    /// Package Manager busy, so the first metadata install often fails with an "exclusive access …
    /// in progress" collision — a transient error that must NOT latch the once-per-session throttle,
    /// or the metadata never installs and the wizard never auto-opens for the rest of the session.
    /// Pure/no-Unity so the classification is unit-testable.
    /// </summary>
    internal static class InstallRetryPolicy
    {
        // Substrings that identify a Package-Manager-busy collision (case-insensitive). Anything else
        // — auth, 404, offline, unknown — is treated as terminal so we don't spam retries every reload.
        private static readonly string[] TransientMarkers =
        {
            "exclusive access",  // "…requires exclusive access to the project is currently running…"
            "in progress",       // "An operation is already in progress."
        };

        public static bool IsTransient(string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage)) return false;
            foreach (var marker in TransientMarkers)
                if (errorMessage.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }
}
