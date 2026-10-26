using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

using CAS.Publishing.Installer.Telemetry;
namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// "Ready to install" — step 1 of the 3-step install flow. Lists the default component set
    /// with live install state (target version to install, or an "already installed" confirmation)
    /// and drives either the batch install (<see cref="AutoInstaller.StartAll"/>) or, once every
    /// default component is already present, hands off straight to the configure step.
    /// </summary>
    internal sealed class ReadyScreen : IWizardScreen
    {
        public string Id => "ready";
        public VisualElement Root { get; }

        // Sub-copy overrides for rows whose default catalog blurb doesn't fit this screen's framing
        // (both SDKs are wired automatically here — nothing for the developer to configure).
        private const string FirebaseId = "com.google.firebase.analytics";
        private const string FirebaseSubOverride = "Analytics — events wired automatically via CAS SDK";

        private bool _bound;
        private readonly VisualElement _list;
        private readonly Button _advanced;
        private readonly Button _primary;
        private readonly VisualElement _blockBox;
        private readonly Label _blockNote;
        private readonly Button _blockUpdate;
        private ComponentStatus _blockedStatus;
        private ReadyModel _model;
        private int _retiredCount;

        public ReadyScreen()
        {
            Root = new VisualElement();
            WizardAssets.CloneInto("Ready", Root);
            _list = Root.Q<VisualElement>("ready-list");
            _advanced = Root.Q<Button>("btn-advanced");
            _primary = Root.Q<Button>("btn-primary");
            _blockBox = Root.Q<VisualElement>("ready-block-note-box");
            _blockNote = Root.Q<Label>("ready-block-note");
            _blockUpdate = Root.Q<Button>("ready-block-update");
        }

        public void OnEnter(WizardRouter router)
        {
            if (!_bound)
            {
                _bound = true;

                if (_advanced != null)
                    _advanced.clicked += () =>
                    {
                        InstallerWizardWindow.IntroDone = true;
                        InstallerWizardWindow.WizardMode = false; // explicit exit to the hub, not a WizardMode re-entry
                        router.GoTo("components");
                    };

                if (_primary != null)
                    _primary.clicked += () =>
                    {
                        // Retired packages left in the project still need the install pass: it is
                        // what removes them (behind its own confirm dialog).
                        if (_model != null && _model.AllInstalled && _retiredCount == 0)
                            // Nothing to install, but something to configure: this path must go
                            // through the configuration steps too, not jump straight to the summary.
                            ConfigPhase.Enter(router);
                        else
                            AutoInstaller.StartAll(router); // drives install + navigates to progress itself
                    };

                // Demanding an update while offering no way to perform it is a dead end, so the
                // action sits inside the warning itself. On success the screen rebuilds unblocked.
                if (_blockUpdate != null)
                    _blockUpdate.clicked += OnBlockUpdate;
            }

            // Live re-scan every time the screen is shown so the status reflects the current
            // project state (mirrors ComponentsScreen).
            Rebuild();
        }

        private void Rebuild()
        {
            if (_list == null) return;
            _list.Clear();

            if (!ComponentStatusProvider.TryGetStatuses(out var statuses, out var error))
            {
                ShowError(error);
                if (_primary != null) _primary.text = "Install";
                return;
            }

            _model = ReadyModel.Build(statuses);
            TrackScan(statuses);

            // The update action depends on HOW the component is installed, so keep its live
            // status around, not just the model row.
            _blockedStatus = null;
            if (_model.Blocked)
                foreach (var s2 in statuses)
                    if (s2 != null && s2.Id == CasVersionGate.CasComponentId) { _blockedStatus = s2; break; }

            // Overlay presentation-only Sub text for the Firebase row. Mutating the freshly-built
            // ReadyRow list (not the ComponentStatus objects, which TryGetStatuses documents as a
            // shared, read-only, session-cached list) so this never leaks into other screens.
            for (var i = 0; i < statuses.Count && i < _model.Rows.Count; i++)
            {
                if (statuses[i].Id == FirebaseId) _model.Rows[i].Sub = FirebaseSubOverride;
            }

            var idx = 0;
            foreach (var row in _model.Rows)
            {
                _list.Add(BuildRow(row, alt: idx % 2 == 1));
                idx++;
            }

            // Retired packages (the old Tenjin wrapper and the like) are listed under the
            // components: the install pass removes them, and nobody should be surprised by that.
            var retired = ComponentStatusProvider.GetRetired();
            _retiredCount = retired.Count;
            foreach (var r in retired)
            {
                _list.Add(BuildRow(new ReadyRow
                {
                    Id = r.Id,
                    Name = r.Id,
                    Sub = r.IsManualCopy
                        ? "Retired, manual copy in Assets/ — CAS does its job now"
                        : "Retired — CAS does its job now",
                    RightText = "To remove",
                    Retired = true,
                }, alt: idx % 2 == 1));
                idx++;
            }

            // A manual copy gets the same "Connect to Hub" the Components tab offers, right here —
            // otherwise the first-run flow walks past it and the project never moves to UPM.
            for (var i = 0; i < _model.Rows.Count; i++)
            {
                var r = _model.Rows[i];
                if (!r.OutsideUpm || r.BlocksFlow) continue; // blocked rows use the warning's Update
                var connect = new Button(() => OnConnect(r.Id, r.Name)) { text = "Connect to Hub" };
                connect.AddToClassList("cas-btn");
                connect.AddToClassList("cas-btn--soft");
                connect.AddToClassList("cas-btn--sm");
                connect.style.marginLeft = 8;
                _list[i].Add(connect);
            }

            if (_primary != null)
            {
                _primary.text = _model.PrimaryButtonText;

                // CAS below the catalog pin: the update is mandatory, not suggested — without it
                // there is neither TenjinMMP nor ReportPurchase, so no attribution. No passage.
                // SetEnabled(true) in the else branch is required: the screen is reused between
                // visits, and without an explicit reset the button would stay dead after an update.
                if (_model.Blocked)
                {
                    _primary.text = "Update required";
                    _primary.SetEnabled(false);
                }
                else
                {
                    _primary.SetEnabled(true);
                }
            }

            // The WRAPPER is toggled: display:none sits on it, not on the label itself.
            if (_blockNote != null) _blockNote.text = _model.BlockMessage ?? "";
            if (_blockBox != null)
                _blockBox.style.display = _model.Blocked ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static VisualElement BuildRow(ReadyRow r, bool alt)
        {
            var row = new VisualElement();
            row.AddToClassList("cas-row-grid");
            if (alt) row.AddToClassList("cas-row-grid--alt");

            var comp = new VisualElement();
            comp.AddToClassList("cas-col-comp");

            var txt = new VisualElement();
            var nameLabel = new Label(r.Name);
            nameLabel.AddToClassList("cas-comp__name");
            var subLabel = new Label(r.Sub);
            subLabel.AddToClassList("cas-comp__sub");
            txt.Add(nameLabel);
            txt.Add(subLabel);
            comp.Add(txt);

            var right = new Label(r.RightText);
            right.AddToClassList("cas-status");
            // A blocked row is present in the project yet says "To update" — a green tone would be
            // a lie, hence the warning yellow.
            right.AddToClassList(r.BlocksFlow || r.OutsideUpm || r.Retired
                ? "cas-status--yellow"
                : r.AlreadyInstalled ? "cas-status--green" : "cas-status--grey");
            right.style.flexGrow = 1f;
            right.style.unityTextAlign = TextAnchor.MiddleRight;

            row.Add(comp);
            row.Add(right);
            return row;
        }

        private void ShowError(string error)
        {
            var note = new Label(error);
            note.AddToClassList("cas-empty-note");
            _list.Add(note);

            // While an install attempt is actually in flight there's nothing to retry — the
            // message itself already says "Installing…" (see ComponentStatusProvider).
            if (CAS.Publishing.Installer.MetadataAutoInstall.InstallInFlight) return;

            var retry = new Button(OnRetry) { text = "Install Metadata" };
            retry.AddToClassList("cas-btn");
            retry.AddToClassList("cas-btn--soft");
            retry.AddToClassList("cas-btn--sm");
            retry.style.marginLeft = 12;
            retry.style.marginTop = 4;
            _list.Add(retry);
        }

        // Retry = also pull/update metadata from the registry (install if missing, check for a
        // newer catalog), then re-scan the local project state and re-render. Mirrors
        // ComponentsScreen.OnRefresh.
        // The same dispatch as the Components tab: a git dependency switches to the registry, an
        // out-of-UPM copy migrates, everything else installs/updates through the migrator. Hard-coding
        // Apply here produced "Nothing to apply" on a git install — the planner had nothing to do.
        private void OnBlockUpdate()
        {
            var c = _blockedStatus;
            if (c == null) return;

            var gitOnly = ComponentStatusProvider.IsGitSourced(c.Id);
            var changed = (c.GitInstalled && !gitOnly)
                ? WizardActions.SwitchToUpm(c.Id, c.DisplayName)
                : c.OutsideUpm
                    ? WizardActions.MigrateExternal(c.Id, c.DisplayName)
                    : WizardActions.Apply(c.Id, c.DisplayName);

            if (!changed) return;
            ComponentStatusProvider.InvalidateCache();
            Rebuild();
        }

        // The Components tab's migration for a manual copy, reachable from the first-run screen.
        private void OnConnect(string id, string displayName)
        {
            if (!WizardActions.MigrateExternal(id, displayName)) return;
            ComponentStatusProvider.InvalidateCache();
            Rebuild();
        }

        private void OnRetry()
        {
            CAS.Publishing.Installer.Catalog.CatalogLoader.InvalidateCache();
            ComponentStatusProvider.InvalidateCache();
            CAS.Publishing.Installer.Bootstrap.EnsureMetadata();
            Rebuild();
        }

        // What the wizard found on first sight: per component "upm" / "git" / "manual" / "missing"
        // plus the detected version. Once per session — Ready re-scans on every entry.
        private static void TrackScan(System.Collections.Generic.IEnumerable<ComponentStatus> statuses)
        {
            if (!HubTelemetry.Once("scan")) return;
            var props = new System.Collections.Generic.Dictionary<string, object>();
            foreach (var s in statuses)
            {
                if (s == null || string.IsNullOrEmpty(s.Id)) continue;
                var state = !s.Installed ? "missing" : s.OutsideUpm ? "manual" : s.GitInstalled ? "git" : "upm";
                // Version only — for git/local installs the manifest value is a URL or a path.
                var version = TelemetryPayload.SafeVersion(s.Version);
                props[s.Id] = string.IsNullOrEmpty(version) ? state : state + "@" + version;
            }
            HubTelemetry.Track("scan_completed", props);
        }
    }
}
