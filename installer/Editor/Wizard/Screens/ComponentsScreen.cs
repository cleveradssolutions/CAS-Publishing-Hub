using UnityEngine.UIElements;

namespace CAS.Publishing.Installer.Wizard
{
    internal sealed class ComponentsScreen : IWizardScreen
    {
        public string Id => "components";
        public VisualElement Root { get; }

        // Equal width for the action + Remove buttons so they line up in fixed slots across rows.
        private const float ActionButtonWidth = 100f;

        private bool _bound;
        private readonly VisualElement _rowsHost;
        private readonly VisualElement _additionalRowsHost;

        public ComponentsScreen()
        {
            Root = new VisualElement();
            WizardAssets.CloneInto("Components", Root);
            _rowsHost = Root.Q<VisualElement>("components-rows");
            _additionalRowsHost = Root.Q<VisualElement>("additional-rows");
        }

        public void OnEnter(WizardRouter router)
        {
            if (!_bound)
            {
                _bound = true;
                var refresh = Root.Q<Button>("components-refresh");
                if (refresh != null) refresh.clicked += OnRefresh;
            }

            // Live re-scan every time the screen is shown so the status reflects the
            // current project state.
            Rebuild();
        }

        // Refresh = also pull/update metadata from the registry (install if missing, check for a
        // newer catalog), then re-scan the local project state and re-render.
        private void OnRefresh()
        {
            // Explicit re-read: drop the session caches so the scan + catalog are re-evaluated from
            // disk (and EnsureMetadata may pull a newer catalog).
            CAS.Publishing.Installer.Catalog.CatalogLoader.InvalidateCache();
            ComponentStatusProvider.InvalidateCache();
            CAS.Publishing.Installer.Bootstrap.EnsureMetadata();
            Rebuild();
        }

        private void Rebuild()
        {
            // If CAS was installed from this tab (or anywhere outside the wizard flow), fill its
            // managerIds from any CAS ID captured on Welcome. Idempotent — only fills empty or
            // placeholder slots, never a real id.
            CasIdApplier.ApplyPending();

            // Both tables read from the same catalog/scan, so a catalog failure fails both
            // TryGet calls identically — show the error note once, on the main table only, or it
            // would render twice (same message) stacked under Main and Additional.
            Fill(_rowsHost, ComponentStatusProvider.TryGetStatuses, showErrorNote: true);
            AddRetiredRows(_rowsHost);
            Fill(_additionalRowsHost, ComponentStatusProvider.TryGetAdditionalStatuses, showErrorNote: false);
        }

        private delegate bool TryGetStatusesFn(out System.Collections.Generic.List<ComponentStatus> statuses, out string error);

        private void Fill(VisualElement host, TryGetStatusesFn tryGet, bool showErrorNote)
        {
            if (host == null) return;

            host.Clear();

            if (!tryGet(out var statuses, out var error))
            {
                if (showErrorNote)
                {
                    var note = new Label(error);
                    note.AddToClassList("cas-empty-note");
                    host.Add(note);

                    // While an install attempt is actually in flight there's nothing to trigger —
                    // the message itself already says "Installing…" (see ComponentStatusProvider).
                    if (!CAS.Publishing.Installer.MetadataAutoInstall.InstallInFlight)
                    {
                        var install = new Button(OnRefresh) { text = "Install Metadata" };
                        install.AddToClassList("cas-btn");
                        install.AddToClassList("cas-btn--soft");
                        install.AddToClassList("cas-btn--sm");
                        install.style.marginLeft = 12;
                        install.style.marginTop = 4;
                        host.Add(install);
                    }
                }
                return;
            }

            var i = 0;
            foreach (var st in statuses)
            {
                host.Add(BuildRow(st, alt: i % 2 == 1));
                i++;
            }
        }

        private VisualElement BuildRow(ComponentStatus c, bool alt)
        {
            var recommended = ComponentStatusProvider.ResolveRecommendedVersion(c.Id);
            var vm = ComponentsViewMap.Map(c, recommended, ComponentStatusProvider.IsGitSourced(c.Id));

            var row = new VisualElement();
            row.AddToClassList("cas-row-grid");
            if (alt) row.AddToClassList("cas-row-grid--alt");

            // SDK column: status dot + logo + name/sub.
            var comp = new VisualElement();
            comp.AddToClassList("cas-col-comp");

            var dot = new VisualElement();
            dot.AddToClassList("cas-dot");
            dot.AddToClassList("cas-dot--sm");
            dot.AddToClassList("cas-dot--" + c.Tone);

            var logo = new VisualElement();
            logo.AddToClassList("cas-comp__logo");
            logo.AddToClassList("cas-logo");
            logo.AddToClassList("cas-logo--" + c.Logo);

            var txt = new VisualElement();
            var nameLabel = new Label(c.DisplayName);
            nameLabel.AddToClassList("cas-comp__name");
            var subLabel = new Label(c.Sub);
            subLabel.AddToClassList("cas-comp__sub");
            txt.Add(nameLabel);
            txt.Add(subLabel);

            comp.Add(dot);
            comp.Add(logo);
            comp.Add(txt);

            // Version column.
            var versionCol = new VisualElement();
            versionCol.AddToClassList("cas-col-version");
            var ver = FriendlyVersion(c.Version);
            var versionLabel = new Label(string.IsNullOrEmpty(ver) ? "—" : ver);
            versionLabel.AddToClassList("cas-comp__sub");
            versionLabel.AddToClassList("cas-comp__ver--" + c.Tone);
            versionCol.Add(versionLabel);

            // Status column — PDF terminology (ComponentsViewMap), tone classes stay driven by the
            // scanner's Tone (green/yellow/red/grey) since PDF wording maps 1:1 onto those buckets.
            var statusCol = new VisualElement();
            statusCol.AddToClassList("cas-col-status");
            var statusLabel = new Label(vm.StatusText);
            statusLabel.AddToClassList("cas-status");
            statusLabel.AddToClassList("cas-status--" + c.Tone);
            statusCol.Add(statusLabel);

            // Action column — real Install/Update/Connect-to-Hub/Fix via the migrator (behind a
            // confirm dialog); no button at all when there's nothing to do (RowAction.None).
            var actionCol = new VisualElement();
            actionCol.AddToClassList("cas-col-action");

            if (vm.Action != RowAction.None)
            {
                var btn = new Button(() =>
                {
                    // Dispatch is unchanged by the PDF-terminology rename: an out-of-UPM copy
                    // migrates, a git dependency switches, everything else is Install/Update/Fix
                    // via the migrator.
                    // For a record the catalog declares git-only, the move to the registry is never
                    // offered, and a tag update is planned by Apply — ResolveMethod steers it into
                    // AddGitPackage on its own.
                    var gitOnly = ComponentStatusProvider.IsGitSourced(c.Id);
                    var changed = (c.GitInstalled && !gitOnly)
                        ? WizardActions.SwitchToUpm(c.Id, c.DisplayName)
                        : c.OutsideUpm
                            ? WizardActions.MigrateExternal(c.Id, c.DisplayName)
                            : WizardActions.Apply(c.Id, c.DisplayName);
                    if (changed)
                    {
                        // The provider's session cache assumed installs always domain-reload before
                        // the next read — not true for pure manifest writes, so drop it explicitly
                        // or the row keeps its pre-action state until a manual Refresh.
                        ComponentStatusProvider.InvalidateCache();
                        Rebuild();
                    }
                })
                { text = vm.ActionText };
                btn.AddToClassList("cas-btn");
                btn.AddToClassList("cas-btn--sm");
                btn.AddToClassList(ActionVariantClass(vm.Action));
                btn.style.width = ActionButtonWidth;
                btn.style.flexShrink = 0;
                actionCol.Add(btn);
            }

            if (!string.IsNullOrEmpty(vm.ActionHint))
            {
                var hint = new Label(vm.ActionHint);
                hint.AddToClassList("cas-cfg__hint");
                hint.style.marginTop = vm.Action != RowAction.None ? 2 : 0;
                hint.style.width = ActionButtonWidth;
                actionCol.Add(hint);
            }

            // Remove column — writes directly to Packages/manifest.json (reversible via git), behind
            // WizardActions.Remove's own confirm dialog. Disabled (not hidden) when RemoveEnabled is
            // false, so the column stays aligned across rows.
            var removeCol = new VisualElement();
            removeCol.AddToClassList("cas-col-remove");
            var remove = new Button(() =>
            {
                // Target the id actually in manifest.json (legacy id when present under one),
                // else removal silently no-ops — the "delete does nothing" bug.
                if (WizardActions.Remove(c.InstalledId, c.DisplayName))
                {
                    ComponentStatusProvider.InvalidateCache();
                    Rebuild();
                }
            })
            { text = "Remove SDK" };
            remove.AddToClassList("cas-btn");
            remove.AddToClassList("cas-btn--sm");
            remove.AddToClassList("cas-btn--danger");
            remove.SetEnabled(vm.RemoveEnabled);
            removeCol.Add(remove);

            row.Add(comp);
            row.Add(versionCol);
            row.Add(statusCol);
            row.Add(actionCol);
            row.Add(removeCol);
            return row;
        }

        // Retired packages (catalog uninstall rules) still in the manifest. They have no other row —
        // without these the Hub could not remove the old Tenjin wrapper once CAS and Firebase were
        // in place, and the wrapper's outdated Unity IAP kept breaking the build.
        private void AddRetiredRows(VisualElement host)
        {
            if (host == null) return;
            var i = host.childCount;
            foreach (var r in ComponentStatusProvider.GetRetired())
            {
                host.Add(BuildRetiredRow(r, alt: i % 2 == 1));
                i++;
            }
        }

        private VisualElement BuildRetiredRow(ComponentStatusProvider.RetiredPackage r, bool alt)
        {
            var row = new VisualElement();
            row.AddToClassList("cas-row-grid");
            if (alt) row.AddToClassList("cas-row-grid--alt");

            var comp = new VisualElement();
            comp.AddToClassList("cas-col-comp");
            var dot = new VisualElement();
            dot.AddToClassList("cas-dot");
            dot.AddToClassList("cas-dot--sm");
            dot.AddToClassList("cas-dot--yellow");
            var logo = new VisualElement();
            logo.AddToClassList("cas-comp__logo");
            logo.AddToClassList("cas-logo");
            logo.AddToClassList("cas-logo--generic");
            var txt = new VisualElement();
            var nameLabel = new Label(r.Id);
            nameLabel.AddToClassList("cas-comp__name");
            var subLabel = new Label(r.IsManualCopy
                ? "Retired, manual copy in Assets/ — CAS does its job now"
                : "Retired — CAS does its job now");
            subLabel.AddToClassList("cas-comp__sub");
            if (!string.IsNullOrEmpty(r.Reason)) subLabel.tooltip = r.Reason;
            txt.Add(nameLabel);
            txt.Add(subLabel);
            comp.Add(dot);
            comp.Add(logo);
            comp.Add(txt);

            var versionCol = new VisualElement();
            versionCol.AddToClassList("cas-col-version");
            var ver = r.IsManualCopy ? "manual" : FriendlyVersion(r.Version);
            var versionLabel = new Label(string.IsNullOrEmpty(ver) ? "—" : ver);
            versionLabel.AddToClassList("cas-comp__sub");
            versionCol.Add(versionLabel);

            var statusCol = new VisualElement();
            statusCol.AddToClassList("cas-col-status");
            var statusLabel = new Label("To remove");
            statusLabel.AddToClassList("cas-status");
            statusLabel.AddToClassList("cas-status--yellow");
            statusCol.Add(statusLabel);

            var actionCol = new VisualElement();
            actionCol.AddToClassList("cas-col-action");

            var removeCol = new VisualElement();
            removeCol.AddToClassList("cas-col-remove");
            var remove = new Button(() =>
            {
                var removed = r.IsManualCopy
                    ? WizardActions.RemoveManualCopy(r.Id, r.Paths)
                    : WizardActions.Remove(r.Id, r.Id);
                if (removed)
                {
                    ComponentStatusProvider.InvalidateCache();
                    Rebuild();
                }
            })
            { text = "Remove SDK" };
            remove.AddToClassList("cas-btn");
            remove.AddToClassList("cas-btn--sm");
            remove.AddToClassList("cas-btn--warn");
            removeCol.Add(remove);

            row.Add(comp);
            row.Add(versionCol);
            row.Add(statusCol);
            row.Add(actionCol);
            row.Add(removeCol);
            return row;
        }

        // Non-semver specs get a short source label so the status cell stays readable:
        // a git dependency reads "git" (matches the "Installed (git)" status); file:/embedded → "local".
        internal static string FriendlyVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            if (CAS.Publishing.Installer.Scanner.StateClassifier.IsGitSpec(v)) return "git";
            if (v.StartsWith("file:") || v.Contains("://")) return "local";
            return v;
        }

        private static string ActionVariantClass(RowAction action)
        {
            switch (action)
            {
                case RowAction.Install: return "cas-btn--primary";
                case RowAction.Update:
                case RowAction.ConnectToHub:
                case RowAction.Fix:      return "cas-btn--warn";
                default:                 return "cas-btn--muted";
            }
        }
    }
}
