using System;
using System.Text.RegularExpressions;
using CAS.Publishing.Installer.Catalog;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Per-platform CAS-ID validation for the Welcome screen. Android ids are app bundle ids
    /// (reverse-DNS); iOS ids are numeric App Store ids. The catalog may override the regex/hint
    /// per platform; otherwise these code-side defaults apply. The CAS test value "demo" matches
    /// neither pattern, so it cannot pass (strict validation).
    /// </summary>
    internal static class CasIdValidation
    {
        private const string CasId = "com.cleversolutions.ads.unity";

        internal const string AndroidRegex = @"^[a-zA-Z][a-zA-Z0-9_]*(\.[a-zA-Z][a-zA-Z0-9_]*)+$";
        internal const string IosRegex     = @"^[0-9]+$";
        internal const string AndroidHint  = "com.company.gamename";
        internal const string IosHint      = "1234567890";

        /// <summary>True when the trimmed value is non-empty and matches the pattern. Pure/testable.</summary>
        internal static bool IsValid(string value, string regex)
        {
            if (string.IsNullOrEmpty(value)) return false;
            var v = value.Trim();
            return v.Length > 0 && Regex.IsMatch(v, regex);
        }

        /// <summary>
        /// Effective regex/hint for a platform: a non-empty catalog value wins, else the platform
        /// default. Pure/testable.
        /// </summary>
        internal static (string regex, string hint) Resolve(string platform, string catalogRegex, string catalogHint)
        {
            var iosPlatform = string.Equals(platform, "iOS", StringComparison.OrdinalIgnoreCase);
            var defRegex = iosPlatform ? IosRegex : AndroidRegex;
            var defHint  = iosPlatform ? IosHint  : AndroidHint;
            return (string.IsNullOrEmpty(catalogRegex) ? defRegex : catalogRegex,
                    string.IsNullOrEmpty(catalogHint)  ? defHint  : catalogHint);
        }

        /// <summary>Reads CAS regex/hint from the catalog for a platform, falling back to defaults.</summary>
        public static (string regex, string hint) For(string platform)
        {
            string catRegex = null, catHint = null;
            var load = CatalogLoader.Load();
            if (load.Status == CatalogLoadStatus.Ok && load.Catalog?.External != null)
            {
                foreach (var e in load.Catalog.External)
                {
                    if (e == null || e.Id != CasId || e.Config == null) continue;
                    foreach (var req in e.Config)
                    {
                        if (req == null || req.Kind != "settingsAssetField") continue;
                        if (!string.Equals(req.Platform, platform, StringComparison.OrdinalIgnoreCase)) continue;
                        catRegex = req.Regex; catHint = req.Hint; break;
                    }
                }
            }
            return Resolve(platform, catRegex, catHint);
        }
    }
}
