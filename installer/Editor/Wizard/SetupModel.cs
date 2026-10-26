using System.Collections.Generic;
using CAS.Publishing.Installer.Catalog;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Builds the per-component, per-platform readiness rows for the Setup screen by combining
    /// the catalog's declarative <c>config</c> requirements with live evaluation via
    /// <see cref="SetupChecker"/>. Config is only evaluated for INSTALLED components — you can't
    /// configure a package that isn't installed. Read-only.
    /// </summary>
    internal static class SetupModel
    {
        internal sealed class Cell
        {
            public ConfigRequirement Req;
            public ReqResult Result;
            public string Label;
        }

        internal sealed class Row
        {
            public string Id;
            public string Name;
            public string Logo;
            public bool Installed;
            public bool AdFormats;

            /// <summary>Every requirement of at least one platform in use is satisfied — both parts below.</summary>
            public bool Configured;

            /// <summary>
            /// The component's own settings — asset fields, config files, ad formats — are done for
            /// at least one platform. This is what the component's own configuration step closes
            /// (Placements for CAS, Firebase setup for Firebase).
            /// </summary>
            public bool SettingsConfigured;

            /// <summary>
            /// The Hub's publishing fields (the Tenjin key) are done for at least one platform, or the
            /// component declares none. Closed by the Tenjin step, which is why it is tracked apart
            /// from <see cref="SettingsConfigured"/>: a project with its formats picked but no key
            /// must queue the Tenjin step and not Placements again.
            /// </summary>
            public bool PublishingConfigured;

            public readonly List<Cell> Android = new List<Cell>();
            public readonly List<Cell> IOS = new List<Cell>();
        }

        /// <summary>Whether a cell belongs to the Hub's own publishing settings rather than the component's.</summary>
        internal static bool IsPublishingCell(Cell cell) => cell?.Req?.Kind == TenjinKeySettings.Kind;

        internal static List<Cell> Filter(List<Cell> cells, bool publishing)
        {
            var picked = new List<Cell>();
            if (cells == null) return picked;
            foreach (var c in cells)
                if (IsPublishingCell(c) == publishing) picked.Add(c);
            return picked;
        }

        public static bool TryBuild(out List<Row> rows, out string error)
        {
            rows = new List<Row>();
            error = null;

            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok)
            {
                error = load.Status == CatalogLoadStatus.NotInstalled
                    ? ComponentStatusProvider.MetadataUnavailableMessage(CAS.Publishing.Installer.MetadataAutoInstall.InstallInFlight,
                        "Catalog metadata package is not installed yet — configuration status is unavailable.")
                    : $"Catalog could not be read: {load.Error}";
                return false;
            }

            var cat = load.Catalog;
            var configById = new Dictionary<string, List<ConfigRequirement>>();
            if (cat.Packages != null)
                foreach (var p in cat.Packages)
                    if (p?.Id != null && p.Config != null) configById[p.Id] = p.Config;
            if (cat.External != null)
                foreach (var e in cat.External)
                    if (e?.Id != null && e.Config != null) configById[e.Id] = e.Config;

            // Install state per default component (so we only check config for installed ones).
            var installedById = new Dictionary<string, bool>();
            if (ComponentStatusProvider.TryGetStatuses(out var statuses, out _))
                foreach (var s in statuses) installedById[s.Id] = s.Installed;

            foreach (var id in ComponentStatusProvider.DefaultIds)
            {
                ComponentStatusProvider.TryGetDefaultDisplay(id, out var name, out var logo);
                var row = new Row { Id = id, Name = name, Logo = logo };
                installedById.TryGetValue(id, out row.Installed);

                // Only INSTALLED components have their per-platform config evaluated. Each cell
                // carries its own requirement (and thus its own per-platform action target).
                if (row.Installed && configById.TryGetValue(id, out var reqs) && reqs != null)
                {
                    foreach (var req in reqs)
                    {
                        if (req == null) continue;
                        // A hidden Tenjin step leaves the row with no publishing cells, so nothing
                        // queues the step, shows its panel or counts the key as missing.
                        if (TenjinKeySettings.IsHidden(req)) continue;
                        var cell = new Cell { Req = req, Result = SetupChecker.Evaluate(req), Label = req.Label };
                        if (req.Platform == "iOS") row.IOS.Add(cell);
                        else if (req.Platform == "Android") row.Android.Add(cell);
                        else { row.Android.Add(cell); row.IOS.Add(cell); }
                    }

                    // Detect if this component has CAS ad-format configuration
                    if (reqs != null)
                        foreach (var req in reqs)
                            if (req != null && req.AdFormats) { row.AdFormats = true; break; }
                }

                // The same rule as the Configuration screen: one platform is enough. Plus the ad
                // formats, which are NOT a catalog requirement but a separate adFormats flag —
                // without this check CAS counted as configured right after Welcome (its only
                // requirement, the CAS ID, is filled there) and the Placements step never queued.
                row.SettingsConfigured = row.Installed
                    && (AllConfigured(Filter(row.Android, false)) || AllConfigured(Filter(row.IOS, false)))
                    && IsAdFormatsSatisfied(row);
                row.PublishingConfigured = row.Installed
                    && PublishingSatisfied(Filter(row.Android, true), Filter(row.IOS, true));
                row.Configured = row.SettingsConfigured && row.PublishingConfigured;

                rows.Add(row);
            }

            return true;
        }

        /// <summary>
        /// For a row carrying the <c>adFormats</c> flag, configuration is finished only once at
        /// least one format is selected — the same call the Placements screen makes. Other rows are
        /// unconstrained. The platform is the one picked for this pass: the flow configures one at a time.
        /// </summary>
        private static bool IsAdFormatsSatisfied(Row row)
        {
            var platform = PlatformChoice.Selected;
            if (string.IsNullOrEmpty(platform)) platform = PlatformDetect.ActivePlatform();
            return AdFormatsSatisfiedFor(row, platform);
        }

        /// <summary>
        /// The same rule for an explicitly named platform, so a surface that renders one platform's
        /// column (the Configuration screen) judges that platform rather than the one this pass
        /// happens to be configuring. A row without the <c>adFormats</c> flag is unconstrained.
        /// </summary>
        /// <remarks>
        /// The Placements step also enables CAS's Tenjin MMP, so the rule asks for that too. A
        /// project whose formats were picked before the Hub arrived skipped Placements and never got
        /// the solution — yet counted as configured and read "mediation ready" on Done.
        /// </remarks>
        internal static bool AdFormatsSatisfiedFor(Row row, string platform)
        {
            if (row == null || !row.AdFormats) return true;
            if (string.IsNullOrEmpty(platform)) return true;
            return CasSettingsWriter.ReadAdFlags(platform) != 0 && CasMediation.TenjinMmpReady(platform);
        }

        /// <summary>
        /// The publishing part of a row: unconstrained when the component declares no publishing
        /// fields (Firebase, or an older catalog), otherwise the same one-platform rule as the rest.
        /// internal so the rule can be tested directly.
        /// </summary>
        internal static bool PublishingSatisfied(List<Cell> android, List<Cell> ios)
        {
            var declared = (android != null && android.Count > 0) || (ios != null && ios.Count > 0);
            if (!declared) return true;
            return AllConfigured(android) || AllConfigured(ios);
        }

        // ReqResult carries a ReqStatus, not a bool: only Configured counts as configured —
        // NotApplicable means there is nothing to check and does not make a platform ready.
        // internal rather than private so the rule can be tested directly.
        internal static bool AllConfigured(List<Cell> cells)
        {
            if (cells == null || cells.Count == 0) return false;
            foreach (var c in cells)
                if (c?.Result == null || c.Result.Status != ReqStatus.Configured) return false;
            return true;
        }
    }
}
