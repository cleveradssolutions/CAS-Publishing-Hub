namespace CAS.Publishing.Installer.Common
{
    /// <summary>
    /// Package/external ids that must never appear as their own selectable row in ANY
    /// installer-visible component list — on any surface. Lives in the base editor assembly
    /// (<c>CAS.Publishing.Installer.Editor</c>, which has no asmdef references of its own) specifically so
    /// both consumers can reach it: <c>CAS.Publishing.Installer.Wizard.ComponentStatusProvider</c> (in the
    /// <c>CAS.Publishing.Installer.Wizard.Editor</c> assembly, which references <c>CAS.Publishing.Installer.Editor</c>)
    /// and <see cref="Ui.InstallerWindowReportView"/> (in <c>CAS.Publishing.Installer.Editor</c> itself, the
    /// legacy IMGUI report window). The reference direction only goes one way — Wizard.Editor →
    /// Editor — so a Wizard-side helper is unreachable from <c>Ui/</c>; this type is the shared
    /// home instead.
    ///
    /// Currently hides: EDM4U (<c>com.google.external-dependency-manager</c>) — it arrives
    /// transitively as a UPM dependency of the CAS SDK / Firebase packages, so surfacing it as a
    /// separate thing to install/select is confusing and redundant. This does NOT affect
    /// migration/detection behavior (scanner asset-root markers, the catalog record, or
    /// <c>FirebaseMigrationPlan</c>'s legacy <c>com.psv.unity.edm</c> removal) — only
    /// wizard/report-view-visible lists hide it.
    /// </summary>
    internal static class HiddenComponents
    {
        private static readonly string[] Ids =
        {
            "com.google.external-dependency-manager",
        };

        /// <summary>True when <paramref name="id"/> must never appear as its own row in any
        /// installer-visible component list (Components table, Additional components, Setup/
        /// Configuration rows, AutoInstaller plan, and the legacy IMGUI report window's
        /// install/update/migrate/current sections).</summary>
        public static bool IsHidden(string id)
        {
            foreach (var hidden in Ids)
                if (hidden == id) return true;
            return false;
        }
    }
}
