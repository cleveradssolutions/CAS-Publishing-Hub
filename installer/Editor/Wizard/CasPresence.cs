namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>Whether the CAS package is installed (package-manager truth via the scanner).</summary>
    internal static class CasPresence
    {
        private const string CasId = "com.cleversolutions.ads.unity";

        public static bool IsInstalled()
        {
            if (ComponentStatusProvider.TryGetStatuses(out var statuses, out _))
                foreach (var s in statuses)
                    if (s != null && s.Id == CasId && s.Installed) return true;
            return false;
        }
    }
}
