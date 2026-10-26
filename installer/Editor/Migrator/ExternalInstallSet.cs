using System.Collections.Generic;
using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Scanner;

namespace CAS.Publishing.Installer.Migrator
{
    /// <summary>
    /// Pure resolution of the UPM install set for an <see cref="ExternalRecord"/> — extracted from
    /// <c>WizardActions</c> so the core assembly (e.g. <see cref="FirebaseMigrationPlan"/>) can use it
    /// without referencing the Wizard assembly.
    /// </summary>
    internal static class ExternalInstallSet
    {
        /// <summary>
        /// Resolves the set of UPM packages to install when migrating <paramref name="rec"/>. For a
        /// multi-module external it returns one <see cref="AddPackage"/> per module whose markers are
        /// detected among the loaded types (at <c>module.RecommendedVersion ?? baseVersion</c>); for a
        /// single-package external it returns just <c>rec.Id@baseVersion</c>. Never empty — if a
        /// multi-module record somehow matches no module (the external was still flagged as present),
        /// it falls back to the primary id so a migration never installs nothing.
        /// </summary>
        internal static List<AddPackage> Resolve(
            ExternalRecord rec, string baseVersion, ICollection<string> loadedIdentifiers)
        {
            var installs = new List<AddPackage>();

            if (rec.Modules != null && rec.Modules.Count > 0 && loadedIdentifiers != null)
            {
                var seen = new HashSet<string>();
                foreach (var m in rec.Modules)
                {
                    if (m == null || string.IsNullOrEmpty(m.Id)) continue;
                    if (!AssetInstallProbe.IsPresentInIdentifiers(loadedIdentifiers, m.AssetMarkers)) continue;
                    if (!seen.Add(m.Id)) continue;
                    var v = !string.IsNullOrEmpty(m.RecommendedVersion) ? m.RecommendedVersion : baseVersion;
                    installs.Add(new AddPackage(m.Id, v));
                }
            }

            if (installs.Count == 0)
                installs.Add(new AddPackage(rec.Id, baseVersion)); // single-package or safety fallback

            return installs;
        }
    }
}
