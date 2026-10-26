namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Pure logic for the Placements screen. Kept apart from the screen so the format rules are
    /// verifiable without a Unity environment and without a real CAS settings asset.
    /// </summary>
    internal static class PlacementsModel
    {
        /// <summary>The mock's default: Banner, Interstitial and Rewarded on, App Open off.</summary>
        public const int DefaultFlags = AdFlagsBits.Banner | AdFlagsBits.Interstitial | AdFlagsBits.Rewarded;

        /// <summary>
        /// The initial checkboxes. An empty set means CAS was never configured — apply the default.
        /// Any non-empty set belongs to the client and is kept as it is (owner's decision,
        /// 2026-09-03: a deliberate choice is never overwritten).
        /// </summary>
        public static int InitialFlags(int existingFlags) =>
            existingFlags == 0 ? DefaultFlags : existingFlags;

        /// <summary>CAS configuration is only finished once at least one format is selected.</summary>
        public static bool CanContinue(int flags) => flags != 0;

        /// <summary>
        /// The "switch to Publishing or keep your own" choice is shown only to a project that
        /// already has a solution enabled. A clean project gets no choice — Publishing is set.
        /// </summary>
        public static bool ShowSolutionChoice(bool optimalInstalled, bool familiesInstalled) =>
            optimalInstalled || familiesInstalled;
    }
}
