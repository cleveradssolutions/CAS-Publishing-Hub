using UnityEditor;

namespace CAS.Publishing.Installer.Ui
{
    /// <summary>
    /// Thin wrapper around <see cref="EditorPrefs"/> for persisting the last-shown
    /// scan-report hash. All callers must use this class rather than scattering the
    /// key literal.
    /// </summary>
    internal static class ScanReportStore
    {
        private const string LastShownHashKey = "CAS.Publishing.Installer.LastShownScanHash";

        /// <summary>Returns the hash stored from the most recent auto-popup.</summary>
        public static string GetLastShownHash()
            => EditorPrefs.GetString(LastShownHashKey, "");

        /// <summary>Persists <paramref name="hash"/> so future reloads stay silent.</summary>
        public static void SetLastShownHash(string hash)
            => EditorPrefs.SetString(LastShownHashKey, hash ?? "");
    }
}
