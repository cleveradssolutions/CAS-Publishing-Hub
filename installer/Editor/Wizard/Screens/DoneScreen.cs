using UnityEngine;
using UnityEngine.UIElements;

using CAS.Publishing.Installer.Telemetry;
namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// "Setup complete" — step 3 of the 3-step install flow. Shows a live checklist (CAS SDK /
    /// Firebase) for the project's active platform, plus a "Next steps" card. Purely
    /// informational: any row that isn't green can still be fixed later via Components/CAS Settings,
    /// so nothing here blocks Close/Components.
    /// </summary>
    internal sealed class DoneScreen : IWizardScreen
    {
        public string Id => "done";
        public VisualElement Root { get; }

        private const string FirebaseId = "com.google.firebase.analytics";

        private WizardRouter _router;
        private bool _bound;
        private readonly VisualElement _list;

        public DoneScreen()
        {
            Root = new VisualElement();
            WizardAssets.CloneInto("Done", Root);
            _list = Root.Q<VisualElement>("done-list");
        }

        public void OnEnter(WizardRouter router)
        {
            _router = router;

            // Reaching Done means the 3-step intro flow finished — later opens land on the tabs
            // instead of restarting at Ready. Deferred here (rather than set earlier in
            // AutoInstaller.StartAll) so the stepper header stays visible through the whole flow,
            // including this final "3 Done" step.
            InstallerWizardWindow.IntroDone = true;
            InstallerWizardWindow.WizardMode = false; // flow (first-run or WizardMode re-entry) finished

            if (!_bound)
            {
                _bound = true;

                var dashboard = Root.Q<Button>("done-dashboard");
                if (dashboard != null) dashboard.clicked += () => Application.OpenURL("https://cas.ai");

                var components = Root.Q<Button>("btn-components");
                if (components != null) components.clicked += () => _router.GoTo("components");

                var close = Root.Q<Button>("btn-close");
                if (close != null) close.clicked += InstallerWizardWindow.CloseActive;
            }

            Rebuild();
            TrackDone();
        }

        private static void TrackDone()
        {
            if (!HubTelemetry.Once("done")) return;
            var platform = PlatformDetect.ActivePlatform();
            var props = new System.Collections.Generic.Dictionary<string, object>
            {
                ["platform"] = platform,
                ["casId"] = string.IsNullOrEmpty(CasSettingsReader.ReadExisting(platform)) ? "missing" : "set",
                ["firebase"] = FirebaseConfigured(platform) ? "set" : "missing",
            };
            if (TenjinKeySettings.StepEnabled)
                props["tenjinKey"] = string.IsNullOrEmpty(TenjinKeySettings.ReadExisting(platform)) ? "missing" : "set";
            HubTelemetry.Track("done_shown", props);
        }

        private void Rebuild()
        {
            if (_list == null) return;
            _list.Clear();

            var platform = PlatformDetect.ActivePlatform();

            _list.Add(BuildRow(DoneChecklistModel.CasLine(
                CasSettingsReader.ReadExisting(platform), CasMediation.TenjinMmpReady(platform))));

            // Tenjin is not a component of its own — from CAS 4.8.1 the native SDK arrives inside
            // CAS as the TenjinMMP solution — but its key is, and the key is what the row reports.
            if (TenjinKeySettings.StepEnabled)
                _list.Add(BuildRow(DoneChecklistModel.TenjinLine(
                    !string.IsNullOrEmpty(TenjinKeySettings.ReadExisting(platform)))));

            _list.Add(BuildRow(DoneChecklistModel.FirebaseLine(FirebaseConfigured(platform))));
        }

        // Mirrors ConfigureScreen's per-cell readiness check (Configured or NotApplicable = fine),
        // scoped to the active platform's cells only. Any failure to read the catalog/rows (metadata
        // package missing, load error) or Firebase simply not being installed yet is reported as "not
        // configured" — Done is informational, so this only ever affects the row's tone.
        private static bool FirebaseConfigured(string platform)
        {
            if (!SetupModel.TryBuild(out var rows, out _)) return false;

            SetupModel.Row firebase = null;
            foreach (var row in rows)
                if (row.Id == FirebaseId) { firebase = row; break; }
            if (firebase == null || !firebase.Installed) return false;

            var cells = platform == "iOS" ? firebase.IOS : firebase.Android;
            foreach (var cell in cells)
            {
                var ok = cell.Result != null &&
                         (cell.Result.Status == ReqStatus.Configured || cell.Result.Status == ReqStatus.NotApplicable);
                if (!ok) return false;
            }
            return true;
        }

        private static VisualElement BuildRow(DoneChecklistModel.Line line)
        {
            var row = new VisualElement();
            row.AddToClassList("cas-setup-cell");
            row.style.marginBottom = 8;

            var tone = line.Warn ? "yellow" : "green";
            var dot = new VisualElement();
            dot.AddToClassList("cas-dot");
            dot.AddToClassList("cas-dot--sm");
            dot.AddToClassList("cas-dot--" + tone);
            dot.style.marginTop = 4;
            row.Add(dot);

            var txt = new Label(line.Text);
            txt.AddToClassList("cas-setup-cell__txt");
            txt.AddToClassList("cas-status--" + tone);
            row.Add(txt);

            return row;
        }
    }
}
