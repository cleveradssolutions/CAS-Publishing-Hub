namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>Decides whether a build-target switch should auto-open the wizard at Welcome.</summary>
    internal static class BuildSwitchPolicy
    {
        /// <summary>
        /// Open iff CAS is installed AND the newly-active platform's CAS id is unconfigured
        /// (null or empty — i.e. <see cref="CasSettingsReader.ReadExisting"/> found no real value).
        /// Pure/testable.
        /// </summary>
        internal static bool ShouldOpenOnSwitch(bool casInstalled, string existingId) =>
            casInstalled && string.IsNullOrEmpty(existingId);
    }
}
