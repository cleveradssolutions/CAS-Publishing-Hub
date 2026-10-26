using System;

namespace CAS.Publishing.Installer.Common
{
    /// <summary>
    /// Minimal SemVer 2.0.0 comparison sufficient for UPM version strings.
    /// Tolerates 1- and 2-component cores (padded with zeros), a leading 'v',
    /// and surrounding whitespace. Build metadata (<c>+...</c>) is ignored per spec.
    /// Non-version dependency specs (<c>file:</c>, git/https URLs, <c>latest</c>) are
    /// reported by <see cref="IsVersion"/> so callers never feed them to ordinal compare.
    /// </summary>
    public static class SemVer
    {
        /// <summary>True when <paramref name="value"/> parses as a numeric semver core.</summary>
        public static bool IsVersion(string value)
        {
            return TryParse(value, out _, out _, out _, out _);
        }

        /// <summary>
        /// Returns &lt;0 if a &lt; b, 0 if equal, &gt;0 if a &gt; b.
        /// Non-versions sort below versions; two non-versions compare ordinally.
        /// </summary>
        public static int Compare(string a, string b)
        {
            var aOk = TryParse(a, out var aMaj, out var aMin, out var aPat, out var aPre);
            var bOk = TryParse(b, out var bMaj, out var bMin, out var bPat, out var bPre);

            if (!aOk && !bOk) return string.CompareOrdinal(a ?? string.Empty, b ?? string.Empty);
            if (!aOk) return -1;
            if (!bOk) return 1;

            if (aMaj != bMaj) return aMaj.CompareTo(bMaj);
            if (aMin != bMin) return aMin.CompareTo(bMin);
            if (aPat != bPat) return aPat.CompareTo(bPat);

            // Equal cores: a version with no prerelease outranks one with prerelease.
            var aHasPre = !string.IsNullOrEmpty(aPre);
            var bHasPre = !string.IsNullOrEmpty(bPre);
            if (aHasPre != bHasPre) return aHasPre ? -1 : 1;
            if (!aHasPre) return 0;

            return ComparePrerelease(aPre, bPre);
        }

        // ── Parsing ────────────────────────────────────────────────

        private static bool TryParse(string raw, out int major, out int minor, out int patch, out string prerelease)
        {
            major = minor = patch = 0;
            prerelease = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var s = raw.Trim();
            if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);

            // Drop build metadata.
            var plus = s.IndexOf('+');
            if (plus >= 0) s = s.Substring(0, plus);

            // Split off prerelease.
            var dash = s.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = s.Substring(dash + 1);
                s = s.Substring(0, dash);
            }

            var parts = s.Split('.');
            if (parts.Length == 0 || parts.Length > 3) return false;

            if (!TryComponent(parts, 0, out major)) return false;
            if (!TryComponent(parts, 1, out minor)) return false;
            if (!TryComponent(parts, 2, out patch)) return false;
            return true;
        }

        private static bool TryComponent(string[] parts, int index, out int value)
        {
            value = 0;
            if (index >= parts.Length) return true; // missing component → 0 (pad)
            var p = parts[index];
            if (p.Length == 0) return false;
            return int.TryParse(p, out value) && value >= 0;
        }

        private static int ComparePrerelease(string a, string b)
        {
            var aIds = a.Split('.');
            var bIds = b.Split('.');
            var n = Math.Min(aIds.Length, bIds.Length);

            for (var i = 0; i < n; i++)
            {
                var cmp = ComparePrereleaseId(aIds[i], bIds[i]);
                if (cmp != 0) return cmp;
            }
            // All shared identifiers equal: fewer identifiers sorts lower.
            return aIds.Length.CompareTo(bIds.Length);
        }

        private static int ComparePrereleaseId(string a, string b)
        {
            var aNum = int.TryParse(a, out var ai);
            var bNum = int.TryParse(b, out var bi);

            if (aNum && bNum) return ai.CompareTo(bi);   // numeric identifiers compare numerically
            if (aNum != bNum) return aNum ? -1 : 1;      // numeric identifiers sort below alphanumeric
            return string.CompareOrdinal(a, b);          // both alphanumeric → ASCII order
        }
    }
}
