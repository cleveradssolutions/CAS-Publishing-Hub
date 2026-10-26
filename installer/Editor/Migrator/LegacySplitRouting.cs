using System.Collections.Generic;
using CAS.Publishing.Installer.Scanner;

namespace CAS.Publishing.Installer.Migrator
{
    /// <summary>
    /// Decides when a per-component wizard action must run the COMPOUND legacy migration
    /// instead of the generic single-component plan: the manifest still contains a split-group
    /// legacy id (e.g. com.psv.firebase.base) and the clicked component is either one of the
    /// group's replacement packages OR the external whose scan detected that legacy id
    /// (both wizard entry points → one migration). Pure.
    /// </summary>
    internal static class LegacySplitRouting
    {
        internal static MigrationGroup FindGroupFor(
            ScanReport report,
            IReadOnlyDictionary<string, string> dependencies,
            string componentId)
        {
            if (report?.SplitGroups == null || dependencies == null || string.IsNullOrEmpty(componentId))
                return null;

            foreach (var group in report.SplitGroups)
            {
                if (group == null || !dependencies.ContainsKey(group.LegacyId)) continue;

                foreach (var pkgId in group.PackageIds)
                    if (pkgId == componentId) return group;

                if (report.External != null)
                    foreach (var e in report.External)
                        if (e != null && e.Id == componentId && e.DetectedLegacyId == group.LegacyId)
                            return group;
            }
            return null;
        }
    }
}
