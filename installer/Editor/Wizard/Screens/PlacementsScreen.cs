using CAS.Publishing.Installer.Telemetry;
using UnityEditor;
using UnityEngine.UIElements;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// The "cas" step of the configuration phase: picking the ad formats and writing them into
    /// allowedAdFlags, plus the mandatory Publishing solution and Tenjin MMP.
    ///
    /// Tenjin MMP is always enabled and has no control of its own. If the project already carries
    /// the Tenjin Unity plugin the write won't land — that is the expected outcome, not an error:
    /// the native SDK then arrives with the plugin and a second copy isn't wanted.
    /// </summary>
    internal sealed class PlacementsScreen : IWizardScreen
    {
        public string Id => "placements";
        public VisualElement Root { get; }

        private WizardRouter _router;
        private bool _bound;

        private readonly Toggle _banner, _interstitial, _rewarded, _appOpen, _solutionSwitch;
        private readonly VisualElement _solutionBlock;
        private readonly Label _solutionTitle;
        private readonly Button _continue;

        private string _platform;
        private bool _showSolutionChoice;

        public PlacementsScreen()
        {
            Root = new VisualElement();
            WizardAssets.CloneInto("Placements", Root);

            _banner         = Root.Q<Toggle>("pl-banner");
            _interstitial   = Root.Q<Toggle>("pl-interstitial");
            _rewarded       = Root.Q<Toggle>("pl-rewarded");
            _appOpen        = Root.Q<Toggle>("pl-appopen");
            _solutionBlock  = Root.Q<VisualElement>("pl-solution-block");
            _solutionTitle  = Root.Q<Label>("pl-solution-title");
            _solutionSwitch = Root.Q<Toggle>("pl-solution-switch");
            _continue       = Root.Q<Button>("pl-continue");
        }

        public void OnEnter(WizardRouter router)
        {
            _router = router;
            if (!_bound)
            {
                _bound = true;
                _banner?.RegisterValueChangedCallback(_ => RefreshContinue());
                _interstitial?.RegisterValueChangedCallback(_ => RefreshContinue());
                _rewarded?.RegisterValueChangedCallback(_ => RefreshContinue());
                _appOpen?.RegisterValueChangedCallback(_ => RefreshContinue());
                if (_continue != null) _continue.clicked += OnContinue;
            }

            _platform = PlatformChoice.Selected;
            if (string.IsNullOrEmpty(_platform)) _platform = PlatformDetect.ActivePlatform();

            var flags = PlacementsModel.InitialFlags(CasSettingsWriter.ReadAdFlags(_platform));
            _banner?.SetValueWithoutNotify(AdFlagsBits.HasFlag(flags, AdFlagsBits.Banner));
            _interstitial?.SetValueWithoutNotify(AdFlagsBits.HasFlag(flags, AdFlagsBits.Interstitial));
            _rewarded?.SetValueWithoutNotify(AdFlagsBits.HasFlag(flags, AdFlagsBits.Rewarded));
            _appOpen?.SetValueWithoutNotify(AdFlagsBits.HasFlag(flags, AdFlagsBits.AppOpen));

            var target = BuildTargetFor(_platform);
            var hasOptimal  = CasMediation.IsSolutionInstalled(target, CasMediation.OptimalAds);
            var hasFamilies = CasMediation.IsSolutionInstalled(target, CasMediation.FamiliesAds);
            _showSolutionChoice = PlacementsModel.ShowSolutionChoice(hasOptimal, hasFamilies);

            if (_solutionTitle != null)
                _solutionTitle.text = hasFamilies
                    ? "This project already uses the Families solution"
                    : "This project already uses the Optimal solution";

            if (_solutionBlock != null)
                _solutionBlock.style.display = _showSolutionChoice ? DisplayStyle.Flex : DisplayStyle.None;

            // A clean project gets no choice and Publishing is set unconditionally; when the choice
            // is shown, switching is proposed by default but the decision stays with the user.
            _solutionSwitch?.SetValueWithoutNotify(true);

            RefreshContinue();
        }

        private int CurrentFlags()
        {
            var flags = 0;
            flags = AdFlagsBits.WithFlag(flags, AdFlagsBits.Banner, _banner?.value == true);
            flags = AdFlagsBits.WithFlag(flags, AdFlagsBits.Interstitial, _interstitial?.value == true);
            flags = AdFlagsBits.WithFlag(flags, AdFlagsBits.Rewarded, _rewarded?.value == true);
            flags = AdFlagsBits.WithFlag(flags, AdFlagsBits.AppOpen, _appOpen?.value == true);
            return flags;
        }

        private void RefreshContinue() =>
            _continue?.SetEnabled(PlacementsModel.CanContinue(CurrentFlags()));

        private static BuildTarget BuildTargetFor(string platform) =>
            platform == "iOS" ? BuildTarget.iOS : BuildTarget.Android;

        private void OnContinue()
        {
            var target = BuildTargetFor(_platform);
            var audience = CasSettingsWriter.ReadAudience(_platform);

            CasSettingsWriter.SetAdFlags(_platform, CurrentFlags());

            // Without the choice on screen Publishing is enabled unconditionally; with it, only
            // when the user agreed. Declining leaves their own solution untouched.
            var switchToPublishing = !_showSolutionChoice || _solutionSwitch?.value == true;

            if (switchToPublishing)
            {
                CasMediation.SetSolution(target, CasMediation.PublishingAds, audience, true);
                CasMediation.SetSolution(target, CasMediation.OptimalAds, audience, false);
                CasMediation.SetSolution(target, CasMediation.FamiliesAds, audience, false);
            }

            // Tenjin MMP always, regardless of the solution choice.
            CasMediation.SetSolution(target, CasMediation.TenjinMmp, audience, true);

            HubTelemetry.Track("step_completed", new System.Collections.Generic.Dictionary<string, object>
            {
                ["screen"] = Id,
                ["platform"] = _platform,
                ["adFlags"] = CurrentFlags(),
                ["publishingSolution"] = switchToPublishing,
            });

            var queue = ConfigStepQueue.Without(
                InstallerKeyStore.GetConfigQueue(), ConfigStepQueue.StepCas);
            InstallerKeyStore.SetConfigQueue(queue);

            var returnTo = InstallerKeyStore.GetConfigReturnTo();
            InstallerKeyStore.SetConfigReturnTo(null);
            ConfigPhase.Advance(_router, queue, returnTo);
        }
    }
}
