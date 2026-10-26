using UnityEngine;
using UnityEngine.UIElements;

using CAS.Publishing.Installer.Telemetry;
namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// The "tenjin" step of the configuration phase: the Tenjin SDK key for the platform this pass
    /// configures. From CAS 4.8.1 the Tenjin SDK ships inside CAS and CAS initializes it, but the
    /// key has no field anywhere in CAS — it only goes in at initialization. The step stores it in
    /// the Hub's <c>PublishingSettings</c> asset and shows the one line of code that hands it over.
    ///
    /// Continue needs a valid key; Skip leaves the step open on the Configuration screen, the
    /// same way the Firebase step does — the wizard asks, it does not demand.
    /// </summary>
    internal sealed class TenjinSetupScreen : IWizardScreen
    {
        public string Id => "tenjin-setup";
        public VisualElement Root { get; }

        private WizardRouter _router;
        private bool _bound;

        private readonly TextField _field;
        private readonly Label _label, _placeholder, _warnText;
        private readonly VisualElement _warn;
        private readonly Button _dashboard, _skip, _continue;

        private string _platform;
        private string _regex;

        public TenjinSetupScreen()
        {
            Root = new VisualElement();
            WizardAssets.CloneInto("TenjinSetup", Root);

            _field       = Root.Q<TextField>("tj-key");
            _label       = Root.Q<Label>("tj-label");
            _placeholder = Root.Q<Label>("tj-key-ph");
            _warn        = Root.Q<VisualElement>("tj-warn");
            _warnText    = Root.Q<Label>("tj-warn-text");
            _dashboard   = Root.Q<Button>("tj-dashboard");
            _skip        = Root.Q<Button>("tj-skip");
            _continue    = Root.Q<Button>("tj-continue");
        }

        public void OnEnter(WizardRouter router)
        {
            _router = router;
            if (!_bound)
            {
                _bound = true;
                if (_dashboard != null)
                    _dashboard.clicked += () => Application.OpenURL(TenjinKeySettings.HelpUrlFor(_platform));
                if (_skip != null) _skip.clicked += () =>
                {
                    HubTelemetry.Track("step_skipped", "screen", Id);
                    Leave();
                };
                if (_continue != null) _continue.clicked += OnContinue;
                _field?.RegisterValueChangedCallback(_ => { RefreshContinue(); UpdatePlaceholder(); });
            }

            _platform = PlatformChoice.Selected;
            if (string.IsNullOrEmpty(_platform)) _platform = PlatformDetect.ActivePlatform();

            _regex = TenjinKeySettings.RegexFor(_platform);
            if (_label != null) _label.text = _platform.ToUpperInvariant() + " KEY";
            if (_placeholder != null) _placeholder.text = TenjinKeySettings.HintFor(_platform);

            // Prefill the key already stored for this platform; a point return from Configuration
            // edits it in place.
            _field?.SetValueWithoutNotify(TenjinKeySettings.ReadExisting(_platform) ?? "");

            SetWarning(null);
            UpdatePlaceholder();
            RefreshContinue();
        }

        private void RefreshContinue() =>
            _continue?.SetEnabled(TenjinKeySettings.IsValid(_field?.value, _regex));

        private void UpdatePlaceholder()
        {
            if (_placeholder == null) return;
            _placeholder.style.display =
                string.IsNullOrEmpty(_field?.value) ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void SetWarning(string text)
        {
            if (_warn == null) return;
            if (_warnText != null) _warnText.text = text ?? "";
            _warn.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void OnContinue()
        {
            if (!TenjinKeySettings.Set(_platform, _field?.value))
            {
                SetWarning("The key could not be saved — see the Console for the reason.");
                HubTelemetry.Track("validation_error", new System.Collections.Generic.Dictionary<string, object>
                    { ["screen"] = Id, ["field"] = "tenjinKey" });
                return;
            }
            HubTelemetry.Track("step_completed", "screen", Id);
            Leave();
        }

        private void Leave()
        {
            var queue = ConfigStepQueue.Without(
                InstallerKeyStore.GetConfigQueue(), ConfigStepQueue.StepTenjin);
            InstallerKeyStore.SetConfigQueue(queue);

            var returnTo = InstallerKeyStore.GetConfigReturnTo();
            InstallerKeyStore.SetConfigReturnTo(null);
            ConfigPhase.Advance(_router, queue, returnTo);
        }
    }
}
