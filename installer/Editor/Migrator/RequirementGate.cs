using System;
using System.Collections.Generic;

namespace CAS.Publishing.Installer.Migrator
{
    /// <summary>
    /// Presence gate for catalog packages that need another package already in the project
    /// (e.g. adapters need com.psv.core — a git-distributed legacy package the installer can
    /// never install itself). Pure: the caller supplies manifest dependencies and an
    /// embedded-folder probe.
    /// </summary>
    internal static class RequirementGate
    {
        /// <summary>Null when every requirement is present (manifest dependency OR embedded
        /// package folder); otherwise the first missing id, for the row hint.</summary>
        internal static string FirstMissing(
            IReadOnlyList<string> requires,
            IReadOnlyDictionary<string, string> dependencies,
            Func<string, bool> embeddedExists)
        {
            if (requires == null || requires.Count == 0) return null;
            foreach (var id in requires)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (dependencies != null && dependencies.ContainsKey(id)) continue;
                if (embeddedExists != null && embeddedExists(id)) continue;
                return id;
            }
            return null;
        }
    }
}
