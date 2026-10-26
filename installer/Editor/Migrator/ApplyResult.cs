using System.Collections.Generic;

namespace CAS.Publishing.Installer.Migrator
{
    /// <summary>
    /// Outcome of <see cref="MigrationRunner.Apply"/>.
    /// </summary>
    public sealed class ApplyResult
    {
        /// <summary>True when all actions executed without error.</summary>
        public bool Success { get; }

        /// <summary>Number of actions actually executed before the result was returned.</summary>
        public int ExecutedCount { get; }

        /// <summary>Error messages collected during execution (empty when Success is true).</summary>
        public IReadOnlyList<string> Failures { get; }

        public ApplyResult(bool success, int executedCount, IReadOnlyList<string> failures)
        {
            Success       = success;
            ExecutedCount = executedCount;
            Failures      = failures ?? new List<string>();
        }
    }
}
