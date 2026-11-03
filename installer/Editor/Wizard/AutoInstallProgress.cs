using System.Collections.Generic;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Pure decision helpers for the step-by-step auto-install driver (<see cref="ProgressScreen"/>).
    /// Extracted from the screen so the stall-guard logic is unit-testable without the editor.
    /// No Unity, no I/O, no side effects.
    /// </summary>
    internal static class AutoInstallProgress
    {
        /// <summary>
        /// Index of the first target id not present in <paramref name="resolved"/>; -1 when every
        /// target is resolved (the run is done). A null/empty target list returns -1.
        /// </summary>
        internal static int FirstUnresolved(IReadOnlyList<string> targetIds, ICollection<string> resolved)
        {
            if (targetIds == null) return -1;
            for (var i = 0; i < targetIds.Count; i++)
            {
                var id = targetIds[i];
                if (id == null || resolved == null || !resolved.Contains(id)) return i;
            }
            return -1;
        }

        /// <summary>
        /// True when the current step was issued and still hasn't resolved by its deadline.
        /// A deadline of <= 0 means "no deadline armed" and is never overdue (so a freshly-entered
        /// screen, before any step is issued, can't false-trip the watchdog).
        /// </summary>
        internal static bool IsStepOverdue(double now, double deadline) =>
            deadline > 0 && now >= deadline;

        /// <summary>
        /// Splits a UPM package list into the names that really resolved and the ones UPM failed to
        /// fetch. <c>Client.List</c> returns a package whose git tag or version can't be fetched too —
        /// as version 0.0.0 with no resolved path and the error attached — so a name in the list alone
        /// does not mean installed. <paramref name="failed"/> receives name → first error message.
        /// </summary>
        internal static HashSet<string> Classify(
            IEnumerable<(string name, string resolvedPath, string error)> packages,
            IDictionary<string, string> failed)
        {
            var resolved = new HashSet<string>();
            foreach (var (name, path, error) in packages)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (!string.IsNullOrEmpty(error)) { failed[name] = error; continue; }
                if (!string.IsNullOrEmpty(path)) resolved.Add(name);
            }
            return resolved;
        }
    }
}
