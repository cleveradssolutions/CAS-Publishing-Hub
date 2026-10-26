using System.IO;
using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Telemetry;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// The "firebase" step of the configuration phase: accepts the Firebase config file, copies it
    /// into Assets/ and checks the application id inside it against what the project builds.
    ///
    /// A mismatched id is a warning, not a veto: the file is copied either way and the decision stays
    /// with the user. This catches the most common case — a config from another project. It stays
    /// quiet when any one client matches: a single Firebase project can serve several apps.
    ///
    /// Choosing the file is primarily a click on the drop zone, which opens the file panel. Dragging
    /// works too, but only from Unity's Project window: <c>DragAndDrop.paths</c> carries no paths for
    /// files dragged out of the file explorer, and the config usually sits in Downloads.
    /// </summary>
    internal sealed class FirebaseSetupScreen : IWizardScreen
    {
        private const string ConsoleUrl = "https://console.firebase.google.com/";
        private const string FirebaseId = "com.google.firebase.analytics";

        // Fallback for an unreadable catalog — the same names are declared there as assetFile.
        private const string AndroidFileName = "google-services.json";
        private const string IosFileName = "GoogleService-Info.plist";

        public string Id => "firebase-setup";
        public VisualElement Root { get; }

        private WizardRouter _router;
        private bool _bound;

        private readonly VisualElement _drop, _warn;
        private readonly Label _dropSub, _warnText, _ok;
        private readonly Button _console, _skip, _continue;

        private string _platform;

        public FirebaseSetupScreen()
        {
            Root = new VisualElement();
            WizardAssets.CloneInto("FirebaseSetup", Root);

            _drop     = Root.Q<VisualElement>("fb-drop");
            _dropSub  = Root.Q<Label>("fb-drop-sub");
            _warn     = Root.Q<VisualElement>("fb-warn");
            _warnText = Root.Q<Label>("fb-warn-text");
            _ok       = Root.Q<Label>("fb-ok");
            _console  = Root.Q<Button>("fb-console");
            _skip     = Root.Q<Button>("fb-skip");
            _continue = Root.Q<Button>("fb-continue");
        }

        public void OnEnter(WizardRouter router)
        {
            _router = router;
            if (!_bound)
            {
                _bound = true;

                if (_console != null) _console.clicked += () => Application.OpenURL(ConsoleUrl);
                if (_skip != null) _skip.clicked += () => { HubTelemetry.Track("step_skipped", "screen", Id); Leave(); };
                if (_continue != null) _continue.clicked += () => { HubTelemetry.Track("step_completed", "screen", Id); Leave(); };

                _drop?.RegisterCallback<ClickEvent>(_ => PickFile());
                _drop?.RegisterCallback<DragUpdatedEvent>(_ =>
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy);
                _drop?.RegisterCallback<DragPerformEvent>(_ =>
                {
                    DragAndDrop.AcceptDrag();
                    var paths = DragAndDrop.paths;
                    if (paths != null && paths.Length > 0) Accept(paths[0]);
                });
            }

            _platform = PlatformChoice.Selected;
            if (string.IsNullOrEmpty(_platform)) _platform = PlatformDetect.ActivePlatform();

            if (_dropSub != null) _dropSub.text = ExpectedFileName(_platform);
            SetWarning(null);
            SetOk(null);
        }

        /// <summary>
        /// The expected file name comes from the catalog requirement (<c>kind: assetFile</c>) for
        /// this platform — it is declared there for both. The constant is only a fallback for when
        /// the catalog can't be read.
        /// </summary>
        internal static string ExpectedFileName(string platform)
        {
            var load = CatalogLoader.Load();
            if (load.Status == CatalogLoadStatus.Ok && load.Catalog?.External != null)
            {
                foreach (var e in load.Catalog.External)
                {
                    if (e == null || e.Id != FirebaseId || e.Config == null) continue;
                    foreach (var req in e.Config)
                        if (req != null && req.Kind == "assetFile" &&
                            string.Equals(req.Platform, platform, System.StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrEmpty(req.FileName))
                            return req.FileName;
                }
            }
            return platform == "iOS" ? IosFileName : AndroidFileName;
        }

        private void PickFile()
        {
            var path = EditorUtility.OpenFilePanel(
                "Select " + ExpectedFileName(_platform), "", "json,plist");
            if (!string.IsNullOrEmpty(path)) Accept(path);
        }

        private void Accept(string sourcePath)
        {
            var error = FirebaseConfigFile.ValidateAndCopy(sourcePath, "Assets");
            if (error != null)
            {
                SetOk(null);
                SetWarning(error);
                HubTelemetry.Track("validation_error", new System.Collections.Generic.Dictionary<string, object>
                    { ["screen"] = Id, ["field"] = "configFile", ["error"] = error });
                return;
            }

            var fileName = Path.GetFileName(sourcePath);
            string contents = null;
            try { contents = File.ReadAllText(sourcePath); }
            catch { /* the check is optional — the file is already copied */ }

            var warning = FirebaseConfigFile.MismatchWarningFor(
                fileName, contents, PlayerSettings.applicationIdentifier);

            if (warning != null)
            {
                SetOk(fileName + " added to Assets/.");
                SetWarning(warning);
                HubTelemetry.Track("validation_error", new System.Collections.Generic.Dictionary<string, object>
                    { ["screen"] = Id, ["field"] = "configFileMismatch" });
            }
            else { SetWarning(null); SetOk(fileName + " added to Assets/."); }
        }

        private void SetWarning(string text)
        {
            if (_warn == null) return;
            if (_warnText != null) _warnText.text = text ?? "";
            _warn.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void SetOk(string text)
        {
            if (_ok == null) return;
            _ok.text = text ?? "";
            _ok.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // Skip and Continue leave the same way: the user supplies the file and the wizard does not
        // demand it — an unfinished step simply stays open on the Configuration screen.
        private void Leave()
        {
            var queue = ConfigStepQueue.Without(
                InstallerKeyStore.GetConfigQueue(), ConfigStepQueue.StepFirebase);
            InstallerKeyStore.SetConfigQueue(queue);

            var returnTo = InstallerKeyStore.GetConfigReturnTo();
            InstallerKeyStore.SetConfigReturnTo(null);
            ConfigPhase.Advance(_router, queue, returnTo);
        }
    }
}
