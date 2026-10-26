namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// The action a Main-Components row button performs, decoupled from the scanner's raw
    /// <see cref="ComponentStatus.StatusText"/>/flags so the PDF terminology can change without
    /// touching the click-dispatch logic in <c>ComponentsScreen</c>.
    /// </summary>
    internal enum RowAction
    {
        /// <summary>Nothing to do — no action button is rendered.</summary>
        None,
        Install,
        Update,
        /// <summary>Connects a manually-installed or git-installed copy to the UPM registry
        /// (renders as "Connect to Hub"). Dispatch is unchanged: MigrateExternal for an
        /// out-of-UPM copy, SwitchToUpm for a git-URL dependency, Apply otherwise.</summary>
        ConnectToHub,
        Fix,
    }

    /// <summary>
    /// Presentation view-model for one Main/Additional-Components row, produced by
    /// <see cref="ComponentsViewMap.Map"/>.
    /// </summary>
    internal sealed class ComponentRowVm
    {
        public string StatusText;
        /// <summary>Action-button label. Null when <see cref="Action"/> is <see cref="RowAction.None"/>
        /// and there is nothing to show as a button (the status column already says it all).</summary>
        public string ActionText;
        /// <summary>Small label rendered under the action button (e.g. "to v1.12.0"), or the
        /// detected legacy id for the "Installed (legacy)" deviation below. Null → hidden.</summary>
        public string ActionHint;
        public RowAction Action;
        public bool RemoveEnabled;
    }

    /// <summary>
    /// Pure mapping from the scanner's <see cref="ComponentStatus"/> (internal state machine
    /// vocabulary: "Update available", "Too old", "Needs migration", …) to the PDF's Main
    /// Components terminology ("Update required", "Manual install", …). No side effects, no
    /// catalog/scan access — <paramref name="recommendedVersion"/> is resolved by the caller
    /// (see <see cref="ComponentStatusProvider.ResolveRecommendedVersion"/>) so this stays testable
    /// with plain <see cref="ComponentStatus"/> instances.
    /// </summary>
    internal static class ComponentsViewMap
    {
        public static ComponentRowVm Map(ComponentStatus status, string recommendedVersion,
            bool gitSourced = false)
        {
            var vm = new ComponentRowVm
            {
                // General rule (brief): remove is offered for anything actually installed. One
                // exception is carved out below (out-of-UPM has no manifest entry to remove — see
                // the "Installed (manual)" case) and one is added back (legacy — see below).
                RemoveEnabled = status.Installed && !status.OutsideUpm,
            };

            switch (status.StatusText)
            {
                case "Installed":
                    vm.StatusText = "Up to date";
                    vm.Action = RowAction.None;
                    break;

                case "Update available":
                case "Too old":
                    vm.StatusText = "Update required";
                    vm.ActionText = "Update";
                    vm.Action = RowAction.Update;
                    vm.ActionHint = string.IsNullOrEmpty(recommendedVersion) ? null : "to v" + recommendedVersion;
                    break;

                case "Installed (git)":
                    // The catalog itself demands git for this record — offering a move to the
                    // registry is wrong: it would remove a working dependency in exchange for a
                    // version the registry lacks. Updating within git stays available, or a client
                    // stuck on an old tag would have no way forward at all. The installed version
                    // goes through Normalize — for a git dependency it arrives as the whole "url#tag".
                    if (gitSourced)
                    {
                        // Ordered comparison, not inequality: the pin is a floor. A tag NEWER than the
                        // pin (a stable release against a prerelease pin) is not something to "update"
                        // — offering it would downgrade a working project, and the wizard's own gate
                        // already refuses to, so the two surfaces would disagree.
                        var installedTag = CasVersionGate.Normalize(status.Version);
                        if (!string.IsNullOrEmpty(recommendedVersion) &&
                            installedTag != null &&
                            CAS.Publishing.Installer.Common.SemVer.Compare(installedTag, recommendedVersion) < 0)
                        {
                            vm.StatusText = "Update required";
                            vm.ActionText = "Update";
                            vm.Action = RowAction.Update;
                            vm.ActionHint = "to v" + recommendedVersion;
                        }
                        else
                        {
                            vm.StatusText = "Installed from git";
                            vm.Action = RowAction.None;
                        }
                        break;
                    }
                    vm.StatusText = "Manual install";
                    vm.ActionText = "Connect to Hub";
                    vm.Action = RowAction.ConnectToHub;
                    break;

                case "Installed (manual)":
                    vm.StatusText = "Manual install";
                    vm.ActionText = "Connect to Hub";
                    vm.Action = RowAction.ConnectToHub;
                    break;

                case "Needs migration":
                    // Split out from the "manual → Connect to Hub" case above: every producer of
                    // this StatusText (PackageState.LegacyUpm/LegacyAssets in
                    // ComponentStatusProvider.FromPackage, and the promoted split-group case in
                    // ComponentStatusProvider.PromoteLegacySplit) already sets ComponentStatus.ActionText
                    // to "Migrate" — so it's safe to surface that label directly here instead of the
                    // generic "Connect to Hub". Dispatch (RowAction.ConnectToHub) is unchanged: it
                    // still falls through to WizardActions.Apply, which is what routes a promoted
                    // Firebase/adapter row into the compound migration via LegacySplitRouting.
                    vm.StatusText = "Manual install";
                    vm.ActionText = status.ActionText ?? "Migrate";
                    vm.Action = RowAction.ConnectToHub;
                    break;

                case "Installed (legacy)":
                    // Deviation from the naive "manual → Connect to Hub" rule: a legacy wrapper
                    // already provides this SDK under a DIFFERENT manifest id (e.g. com.psv.tenjin).
                    // Connecting/installing the canonical id here would duplicate the SDK, so this
                    // row offers no action — just surfaces which legacy id is providing it, as a
                    // hint (ComponentStatus.ActionText already carries the detected legacy id for
                    // this status — see ComponentStatusProvider.FromExternal). Remove IS meaningful
                    // here (the legacy id is a real manifest entry), so it's forced back on.
                    vm.StatusText = "Manual install";
                    vm.Action = RowAction.None;
                    vm.ActionHint = status.ActionText;
                    vm.RemoveEnabled = true;
                    break;

                case "Mixed install":
                    vm.StatusText = "Mixed install";
                    vm.ActionText = "Fix";
                    vm.Action = RowAction.Fix;
                    break;

                case "Needs registry":
                    vm.StatusText = "Needs registry";
                    vm.ActionText = "Fix";
                    vm.Action = RowAction.Fix;
                    break;

                case "Not Installed":
                    vm.StatusText = "Not installed";
                    vm.ActionText = "Install";
                    vm.Action = RowAction.Install;
                    break;

                case "Not in catalog":
                    vm.StatusText = "Not in catalog";
                    vm.Action = RowAction.None;
                    vm.RemoveEnabled = false;
                    break;

                default:
                    // Unknown/future StatusText: pass it through rather than hide it silently.
                    vm.StatusText = status.StatusText;
                    vm.Action = RowAction.None;
                    break;
            }

            return vm;
        }
    }
}
