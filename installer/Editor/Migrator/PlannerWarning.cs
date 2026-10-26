using System.Collections.Generic;

namespace CAS.Publishing.Installer.Migrator
{
    /// <summary>
    /// A non-fatal diagnostic produced by <see cref="MigrationPlanner"/> when it cannot
    /// generate an action for a selected item (e.g. an external record with no version
    /// configured). Warnings are informational — they do not prevent the plan from being
    /// applied; they are returned alongside the action list for the caller to display.
    /// </summary>
    public class PlannerWarning
    {
        /// <summary>
        /// The package or legacy id that triggered the warning.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Human-readable description of why an action could not be generated.
        /// </summary>
        public string Message { get; }

        public PlannerWarning(string id, string message)
        {
            Id      = id;
            Message = message;
        }
    }

    /// <summary>
    /// Specialised warning emitted when a <see cref="RemovePackage"/> for a legacy id is
    /// in the plan but one or more sibling replacement packages in the same split group
    /// are NOT selected. Applying would leave the project without those replacements.
    /// The UI renders this as a blocking confirm dialog before allowing Apply to proceed.
    /// </summary>
    public sealed class PartialSplitWarning : PlannerWarning
    {
        /// <summary>The shared legacy npm id being removed (e.g. "com.psv.firebase.base").</summary>
        public string LegacyId { get; }

        /// <summary>Canonical ids of the replacement packages that ARE selected.</summary>
        public IReadOnlyList<string> SelectedSiblings { get; }

        /// <summary>Canonical ids of the replacement packages that are NOT selected.</summary>
        public IReadOnlyList<string> UnselectedSiblings { get; }

        public PartialSplitWarning(
            string legacyId,
            IReadOnlyList<string> selectedSiblings,
            IReadOnlyList<string> unselectedSiblings)
            : base(legacyId, BuildMessage(legacyId, unselectedSiblings))
        {
            LegacyId           = legacyId;
            SelectedSiblings   = selectedSiblings   ?? new List<string>();
            UnselectedSiblings = unselectedSiblings ?? new List<string>();
        }

        private static string BuildMessage(string legacyId, IReadOnlyList<string> unselected)
        {
            var missing = unselected != null ? string.Join(", ", unselected) : "";
            return $"Split migration for '{legacyId}' is partial — not selected: {missing}";
        }
    }
}
