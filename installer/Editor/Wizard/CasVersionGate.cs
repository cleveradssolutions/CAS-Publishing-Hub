using System.Text.RegularExpressions;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Semver comparison (prereleases included) and the decision of whether an installed version
    /// blocks the rest of the wizard flow. Pure logic, no Unity API.
    ///
    /// The block applies to CAS ONLY: a build below the catalog pin has neither TenjinMMP nor
    /// ReportPurchase, so there is no attribution to set up and configuring it is pointless. For
    /// every other component a version below the pin is an ordinary entry in the update plan.
    /// </summary>
    internal static class CasVersionGate
    {
        public const string CasComponentId = "com.cleversolutions.ads.unity";

        /// <summary>
        /// Negative — <paramref name="a"/> is older, 0 — equal, positive — newer. An empty or
        /// unparsable version compares as equal (0); whether to block is decided separately in
        /// <see cref="IsBelowPin"/>.
        /// </summary>
        public static int Compare(string a, string b)
        {
            SplitVersion(a, out var numsA, out var preA);
            SplitVersion(b, out var numsB, out var preB);

            for (var i = 0; i < 3; i++)
            {
                var cmp = numsA[i].CompareTo(numsB[i]);
                if (cmp != 0) return cmp;
            }

            // A prerelease ranks below the release with the same numbers; two prereleases compare
            // as strings, which is what the semver spec prescribes for alphanumeric identifiers.
            var hasPreA = !string.IsNullOrEmpty(preA);
            var hasPreB = !string.IsNullOrEmpty(preB);
            if (hasPreA && !hasPreB) return -1;
            if (!hasPreA && hasPreB) return 1;
            if (!hasPreA) return 0;
            return string.CompareOrdinal(preA, preB);
        }

        // What counts as a version. A dependency value is not always a number: for a git
        // dependency it is a URL, and the number lives in the fragment after '#' — that is the
        // tag. Anything else (a URL with no tag, "file:", a path on disk) is not a version, and
        // comparing it against the pin is meaningless.
        private static readonly Regex VersionShape =
            new Regex(@"^\d+(\.\d+)*(-[0-9A-Za-z.\-]+)?$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Reduces a dependency value to a version number, or returns null when it holds none.
        /// null means "version unknown" — the flow is never blocked on that basis.
        /// </summary>
        internal static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            var s = raw.Trim();
            var hash = s.LastIndexOf('#');
            if (hash >= 0) s = s.Substring(hash + 1).Trim();

            return VersionShape.IsMatch(s) ? s : null;
        }

        /// <summary>
        /// The installed version is strictly below the pin. An unknown (unparsable) version or an
        /// empty pin yields false: there is nothing to justify a block. That is what saves a
        /// project on a git dependency from being blocked for no reason.
        /// </summary>
        public static bool IsBelowPin(string installed, string pin)
        {
            var a = Normalize(installed);
            var b = Normalize(pin);
            if (a == null || b == null) return false;
            return Compare(a, b) < 0;
        }

        /// <summary>The flow is not allowed to continue: this is CAS and it is below the pin.</summary>
        public static bool BlocksFlow(string componentId, string installed, string pin) =>
            componentId == CasComponentId && IsBelowPin(installed, pin);

        private static void SplitVersion(string v, out int[] nums, out string prerelease)
        {
            nums = new int[3];
            prerelease = "";
            if (string.IsNullOrEmpty(v)) return;

            var s = v.Trim();
            var dash = s.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = s.Substring(dash + 1);
                s = s.Substring(0, dash);
            }

            var parts = s.Split('.');
            for (var i = 0; i < 3 && i < parts.Length; i++)
                int.TryParse(parts[i], out nums[i]);
        }
    }
}
