using System.Collections.Generic;
using UnityEngine.UIElements;

using CAS.Publishing.Installer.Telemetry;
namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// The wizard's first screen. The user picks a platform (defaulting to the project's active
    /// build target) and enters the CAS ID for it. The field starts empty unless CAS already has a
    /// real managerId for the selected platform, in which case it is prefilled. Input is validated
    /// per platform (Android = bundle id, iOS = numeric); Continue stays locked until it is valid.
    /// On Continue the choice and the id are only persisted — they reach
    /// <c>CASSettings&lt;platform&gt;.asset</c> later, via <see cref="CasIdApplier.ApplyPending"/>,
    /// because on a clean project the CAS package does not exist yet at this point.
    /// </summary>
    internal sealed class WelcomeScreen : IWizardScreen
    {
        private const string CasId = "com.cleversolutions.ads.unity";

        public string Id => "welcome";
        public VisualElement Root { get; }

        private WizardRouter _router;
        private bool _bound;

        private readonly TextField _field;
        private readonly Button _tabAndroid, _tabIos, _next;
        private readonly Label _placeholder;

        private string _platform;   // the single platform this pass configures
        private string _regex;      // active platform's validation pattern

        // The per-platform validation regex/hint comes from the catalog (CatalogLoader.Load() is
        // uncached). Memoise it per platform so toggling the Android/iOS segment back and forth doesn't
        // re-parse the catalog on every click. (The seed/existing-id is intentionally NOT cached — it
        // must reflect the asset's current managerId.)
        private readonly Dictionary<string, (string regex, string hint)> _valCache =
            new Dictionary<string, (string regex, string hint)>();

        public WelcomeScreen()
        {
            Root = new VisualElement();
            WizardAssets.CloneInto("Welcome", Root);

            _field      = Root.Q<TextField>("welcome-casid");
            _tabAndroid = Root.Q<Button>("welcome-tab-android");
            _tabIos     = Root.Q<Button>("welcome-tab-ios");
            _next       = Root.Q<Button>("welcome-next");
            _placeholder = Root.Q<Label>("welcome-casid-ph");

            // Default to the project's active build target; the user may switch.
            _platform = PlatformDetect.ActivePlatform();

            var version = Root.Q<Label>("welcome-version");
            if (version != null) version.text = "v" + WizardAssets.InstallerVersion;
        }

        // Memoised per-platform catalog regex/hint lookup (see _valCache).
        private (string regex, string hint) ValidationFor(string platform)
        {
            if (!_valCache.TryGetValue(platform, out var v))
            {
                v = CasIdValidation.For(platform);
                _valCache[platform] = v;
            }
            return v;
        }

        // The field starts EMPTY on (re)open: a previously-typed-but-unapplied id is NOT restored
        // (feedback #2). Only a REAL existing CAS managerId prefills it (feedback #2.2).
        private static string Seed(string platform) =>
            ResolveSeed(InstallerKeyStore.Get(CasId, platform), CasIdApplier.ReadExisting(platform));

        /// <summary>
        /// Seed policy for the CAS-ID field: the real existing CAS managerId only. The stored value
        /// is intentionally ignored — passed in solely to document (and lock via test) that it is
        /// dropped, not forgotten. Pure/testable.
        /// </summary>
        internal static string ResolveSeed(string storedValue, string existingCasId) =>
            existingCasId ?? "";

        // A platform asked for by another screen (Configuration's CAS ID cell) — taken on the next
        // OnEnter instead of the default, so the field opens for the platform being fixed.
        private static string _requestedPlatform;

        internal static void RequestPlatform(string platform) => _requestedPlatform = platform;

        public void OnEnter(WizardRouter router)
        {
            _router = router;
            if (!string.IsNullOrEmpty(_requestedPlatform))
            {
                var requested = _requestedPlatform;
                _requestedPlatform = null;
                if (_bound) ShowPlatform(requested);
                else _platform = requested;
            }
            if (!_bound)
            {
                _bound = true;

                _tabAndroid?.RegisterCallback<ClickEvent>(_ => SelectPlatform("Android"));
                _tabIos?.RegisterCallback<ClickEvent>(_ => SelectPlatform("iOS"));
                _field?.RegisterValueChangedCallback(_ => { RefreshNext(); UpdatePlaceholder(); });
                if (_next != null) _next.clicked += OnNext;
            }

            ShowPlatform(_platform);
        }

        private void SelectPlatform(string platform)
        {
            if (_platform == platform) return;
            ShowPlatform(platform);
        }

        private void ShowPlatform(string platform)
        {
            var platformChanged = _platform != platform;
            _platform = platform;

            // Resolve this platform's validation pattern + prefill its real existing id (if any).
            var (regex, hint) = ValidationFor(platform);
            _regex = regex;
            if (_placeholder != null) _placeholder.text = hint;

            // Only re-seed when the platform actually changed (each has its own id) or the field is
            // empty. Without this guard a second OnEnter — coming back from another tab, a window
            // rebuild — would wipe whatever the user had typed but not yet submitted.
            if (_field != null && (platformChanged || string.IsNullOrEmpty(_field.value)))
                _field.SetValueWithoutNotify(Seed(platform));

            UpdatePlaceholder();

            SetSegActive(_tabAndroid, platform == "Android");
            SetSegActive(_tabIos, platform == "iOS");
            RefreshNext();
        }

        private static void SetSegActive(Button seg, bool active)
        {
            if (seg == null) return;
            if (active) seg.AddToClassList("cas-seg__item--active");
            else        seg.RemoveFromClassList("cas-seg__item--active");
        }

        private void RefreshNext()
        {
            _next?.SetEnabled(CasIdValidation.IsValid(_field?.value, _regex));
        }

        // The placeholder hint shows only while the field is empty.
        private void UpdatePlaceholder()
        {
            if (_placeholder == null) return;
            _placeholder.style.display =
                string.IsNullOrEmpty(_field?.value) ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void OnNext()
        {
            // The pass configures the picked platform, and everything after Welcome is drawn for the
            // active build target — so a mismatch is settled here, not discovered on Configuration
            // after Placements and Tenjin were already walked for the other platform (QA 57).
            var active = PlatformDetect.ActivePlatformOrOther();
            if (!string.Equals(active, _platform, System.StringComparison.OrdinalIgnoreCase))
            {
                var otherTarget = active == "Other";
                var choice = UnityEditor.EditorUtility.DisplayDialogComplex("CAS.AI Publishing Hub",
                    otherTarget
                        ? $"The active build target is {UnityEditor.EditorUserBuildSettings.activeBuildTarget}, " +
                          $"not {_platform}. Switch it to {_platform} now?"
                        : $"Active build target is {active}, but {_platform} was selected. " +
                          $"Switch the build target to {_platform} now?",
                    "Switch to " + _platform,
                    "Back",
                    otherTarget ? "Continue without switching" : "Set up " + active + " instead");

                if (choice == 1) return; // Back
                if (choice == 2 && !otherTarget)
                {
                    // The id typed was for the other platform (formats differ), so stay here.
                    ShowPlatform(active);
                    return;
                }
                if (choice == 0)
                {
                    var group = _platform == "iOS" ? UnityEditor.BuildTargetGroup.iOS : UnityEditor.BuildTargetGroup.Android;
                    var target = _platform == "iOS" ? UnityEditor.BuildTarget.iOS : UnityEditor.BuildTarget.Android;
                    var supported = UnityEditor.BuildPipeline.IsBuildTargetSupported(group, target);
                    BuildTargetSwitcher.Switch(group, target);
                    // No Build Support module: Switch only explained that. Going on would set up a
                    // platform the editor can't switch to — the mismatch this dialog exists to settle.
                    if (!supported) return;
                }
            }

            // Deliberately NOT writing to the CAS settings asset here: on a clean project the CAS
            // package isn't installed yet, and the design promises the id is applied AFTER install.
            // Both values go to the key store; CasIdApplier.ApplyPending() moves the id into the
            // asset once the package exists (ProgressScreen / ComponentsScreen / window open).
            PlatformChoice.Select(_platform);
            InstallerKeyStore.Set(CasId, _platform, CasIdApplier.NormalizeManagerId(_field?.value));
            HubTelemetry.Track("step_completed", new System.Collections.Generic.Dictionary<string, object>
                { ["screen"] = Id, ["platform"] = _platform });

            // CAS already in the project: no install pass follows to apply the id, so apply it now
            // (fills an empty/"demo" slot only). A no-op on a clean project.
            CasIdApplier.ApplyPending();

            _router.GoTo("ready");
        }
    }
}
