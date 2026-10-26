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
    }
}
