using System.Collections.Generic;

namespace CAS.Publishing.Installer.Wizard
{
    internal sealed class PlatformReadiness
    {
        public string Platform;
        public bool Used;   // the platform is in play (it has requirements and the component is installed)
        public bool AllOk;  // every active requirement is satisfied
    }

    internal static class ConfigureGate
    {
        // The "one platform is enough" rule (Configuration complete mock, #8).
        public static bool CanContinue(IReadOnlyList<PlatformReadiness> platforms)
        {
            foreach (var p in platforms)
                if (p != null && p.Used && p.AllOk) return true;
            return false;
        }

        /// <summary>
        /// Whether one component row still has configuration work on the platform being shown.
        /// The catalog requirements are only half of it: a row that also carries the
        /// <c>adFormats</c> flag is unfinished until a format is actually selected. CAS declares a
        /// single requirement — the CAS ID — and Welcome fills it before anything is installed, so
        /// requirements alone read as configured with no ad format chosen at all.
        /// </summary>
        public static bool RowNeedsAttention(
            bool installed, bool cellsNeedAttention, bool adFormatsRequired, bool adFormatsSatisfied)
        {
            if (!installed) return false;
            return cellsNeedAttention || (adFormatsRequired && !adFormatsSatisfied);
        }
    }
}
