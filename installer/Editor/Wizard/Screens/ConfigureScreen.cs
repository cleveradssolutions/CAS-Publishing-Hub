using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>Action a Configuration table cell performs when clicked (see <see cref="ConfigureScreen.ResolveCellAction"/>).</summary>
    internal enum ConfigCellAction
    {
        None,
        OpenCasSettings,
        PingFirebaseFile,
        LocateFirebaseFile,
        GoToPlacements,
        GoToTenjinSetup,
        GoToFirebaseSetup,
        GoToWelcome,
    }

    /// <summary>
    /// Configuration — a read-only, SINGLE-platform detection table for the INSTALLED components,
    /// scoped to the project's active build target: Android active → Android column/panels only, iOS
    /// active → iOS only. When the active target is neither (Standalone/WebGL/…), the table and panels
    /// are replaced by a "switch to Android/iOS" prompt (<see cref="BuildSwitchPromptCard"/>) — nothing
    /// here writes to the project either way — the row action and the panel button hand off to the
    /// wizard's own configuration steps (Placements / Firebase setup), which do the writing.
    /// On Android/iOS, Continue is gated by <see cref="ConfigureGate"/> applied to just the active
    /// platform's readiness; on Other, it falls back to ConfigureGate's original "any platform ready"
    /// rule over both, so a project that already finished the other platform isn't blocked.
    /// </summary>
    internal sealed class ConfigureScreen : IWizardScreen
    {
        public string Id => "configure";
        public VisualElement Root { get; }

        private const string CasId = "com.cleversolutions.ads.unity";
        private const string FirebaseId = "com.google.firebase.analytics";

        // Mirrors PlatformDetect.ActivePlatformOrOther()'s third state — the active build target is
        // neither Android nor iOS (Standalone/WebGL/…).
        private const string OtherPlatform = "Other";

        private const string SwitchPromptSubtitle =
            "Publishing setup targets Android or iOS. Switch the active build target below to configure it.";

        private WizardRouter _router;
        private bool _bound;
        private readonly VisualElement _rowsHost;
        private readonly VisualElement _panelsHost;
        private readonly VisualElement _gateNote;
        private readonly Label _title;
        private readonly Label _subtitle;
        private readonly Button _btnContinue;
        private readonly VisualElement _tableWrap;
        private readonly Label _hdrPlatform;
        private readonly VisualElement _switchPromptHost;

        public ConfigureScreen()
        {
            Root = new VisualElement();
            WizardAssets.CloneInto("Configure", Root);
            _rowsHost         = Root.Q<VisualElement>("config-table");
            _panelsHost       = Root.Q<VisualElement>("action-panels");
            _gateNote         = Root.Q<VisualElement>("gate-note");
            _title            = Root.Q<Label>("configure-title");
            _subtitle         = Root.Q<Label>("configure-subtitle");
            _btnContinue      = Root.Q<Button>("btn-continue");
            _tableWrap        = Root.Q<VisualElement>("configure-table-wrap");
            _hdrPlatform      = Root.Q<Label>("hdr-platform");
            _switchPromptHost = Root.Q<VisualElement>("switch-prompt");
        }

        public void OnEnter(WizardRouter router)
        {
            _router = router;
            if (!_bound)
            {
                _bound = true;
                var refresh = Root.Q<Button>("btn-refresh");
                if (refresh != null) refresh.clicked += RefreshFromDisk;
                var back = Root.Q<Button>("btn-back");
                if (back != null) back.clicked += () => _router.Back();
                if (_btnContinue != null) _btnContinue.clicked += () => _router.GoTo("done");
            }

            Rebuild();
        }

        // The Refresh button forces a re-read: drop the session caches so the scan + catalog are
        // re-evaluated from disk (otherwise Refresh would just re-render the already-cached state).
        private void RefreshFromDisk()
        {
            CAS.Publishing.Installer.Catalog.CatalogLoader.InvalidateCache();
            ComponentStatusProvider.InvalidateCache();
            Rebuild();
        }

        private void Rebuild()
        {
            if (_rowsHost == null) return;
            _rowsHost.Clear();
            _panelsHost?.Clear();
            _switchPromptHost?.Clear();

            var platform = PlatformDetect.ActivePlatformOrOther(); // "Android" | "iOS" | "Other"

            if (ShouldShowSwitchPrompt(platform, PlatformChoice.Selected))
            {
                RebuildOther(platform, PlatformChoice.Selected);
                return;
            }

            RebuildForPlatform(platform);
        }

        /// <summary>
        /// Whether to show the "switch your build target" prompt instead of the table. Two reasons:
        /// the target is not Android/iOS at all ("Other"), or it does not match the platform the
        /// user picked on Welcome. An empty pick keeps the original, pre-Welcome rule. Pure/testable.
        /// </summary>
        internal static bool ShouldShowSwitchPrompt(string activeOrOther, string selected) =>
            activeOrOther == OtherPlatform || PlatformChoice.IsMismatch(selected, activeOrOther);

        // Android/iOS: the table + action panels show ONLY the active platform's column/cells — see
        // BuildRow/RenderActionPanels below, both of which now take the single active platform instead
        // of building both columns and dimming one (superseded dim mechanics from commit 15949a6).
        private void RebuildForPlatform(string platform)
        {
            SetTableVisible(true);
            if (_panelsHost != null) _panelsHost.style.display = DisplayStyle.Flex;
            SetSwitchPromptVisible(false);

            if (_hdrPlatform != null) _hdrPlatform.text = platform;

            // Android build readiness: missing gradle/manifest templates make a fresh project fail to
            // build with a non-obvious cause — surface it with a one-click Enable. Only relevant when
            // Android is the active target: on an iOS project the banner (and its Enable button, which
            // would write Android templates) is noise/wrong, so it follows the screen's platform scope.
            if (platform == "Android" && AndroidBuildFix.MissingNow() is var missingAndroid && missingAndroid.Count > 0)
            {
                var banner = new VisualElement();
                banner.AddToClassList("cas-androidbanner");
                var msg = new Label($"⚠ {missingAndroid.Count} Android build template(s) missing — " +
                                    "the project may not build until they're enabled.");
                msg.AddToClassList("cas-androidbanner__msg");
                banner.Add(msg);
                var fix = new Button(() => { AndroidBuildFix.Ensure(); Rebuild(); })
                    { text = "Enable Android build settings" };
                fix.AddToClassList("cas-btn");
                fix.AddToClassList("cas-btn--primary");
                banner.Add(fix);
                _rowsHost.Add(banner);
            }

            if (!SetupModel.TryBuild(out var rows, out var error))
            {
                AddNote(error);
                SetHeader(false, string.Empty);
                SetContinueEnabled(false);
                if (_gateNote != null) _gateNote.style.display = DisplayStyle.None;
                return;
            }

            var installed = 0;
            var idx = 0;
            // Rows render generically from the catalog's config requirements (no
            foreach (var row in rows)
            {
                if (!row.Installed) continue; // Configuration lists only installed components
                _rowsHost.Add(BuildRow(row, alt: idx % 2 == 1, platform));
                idx++;
                installed++;
            }

            if (installed == 0)
            {
                AddNote("No components installed yet — install them on the Components tab first.");
                SetHeader(false, string.Empty);
                SetContinueEnabled(false);
                if (_gateNote != null) _gateNote.style.display = DisplayStyle.None;
                return;
            }

            // Gate/header are scoped to the active platform only — a project that already finished the
            // OTHER platform doesn't get a misleading "<platform> configuration is complete" here (that
            // would name the wrong platform); it's handled by RebuildOther's any-platform fallback instead.
            var readiness = FindReadiness(BuildReadiness(rows), platform);
            var gateOpen = ConfigureGate.CanContinue(new List<PlatformReadiness> { readiness });

            RenderActionPanels(rows, platform);
            UpdateHeader(gateOpen, platform);
            SetContinueEnabled(gateOpen);
            if (_gateNote != null) _gateNote.style.display = gateOpen ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // Other (Standalone/WebGL/…): table and action panels are hidden entirely in favor of the
        // switch-prompt card (BuildSwitchPromptCard) — there's no single active platform to show a
        // column/panels for. Continue still uses the pre-single-platform "any platform is ready enough"
        // rule (both PlatformReadiness entries) so a project that already finished Android or iOS earlier
        // isn't blocked just because the active build target is now something else — no separate "return
        // here after switching" mechanism is added; BuildTargetWatcher/BuildSwitchPolicy already reopens
        // Configure after a switch when needed.
        private void RebuildOther(string activeOrOther, string selected)
        {
            SetTableVisible(false);
            if (_panelsHost != null) _panelsHost.style.display = DisplayStyle.None;
            SetSwitchPromptVisible(true);
            _switchPromptHost?.Add(activeOrOther == OtherPlatform
                ? BuildSwitchPromptCard()
                : BuildMismatchCard(activeOrOther, selected));
            if (_gateNote != null) _gateNote.style.display = DisplayStyle.None;

            var gateOpen = SetupModel.TryBuild(out var rows, out _) && ConfigureGate.CanContinue(BuildReadiness(rows));
            SetHeader(gateOpen, SwitchPromptSubtitle);
            SetContinueEnabled(gateOpen);
        }

        private void SetTableVisible(bool visible)
        {
            if (_tableWrap != null) _tableWrap.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void SetSwitchPromptVisible(bool visible)
        {
            if (_switchPromptHost != null) _switchPromptHost.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void AddNote(string text)
        {
            var note = new Label(text);
            note.AddToClassList("cas-empty-note");
            _rowsHost.Add(note);
        }

        private void SetHeader(bool complete, string subtitle)
        {
            if (_title != null) _title.text = complete ? "Configuration complete" : "Configuration";
            if (_subtitle != null) _subtitle.text = subtitle;
        }

        private void SetContinueEnabled(bool enabled)
        {
            _btnContinue?.SetEnabled(enabled);
        }

        // Header/subtitle copy, scoped to the single active platform now that the table/panels only
        // ever show that one platform: gate closed → generic "resolve items for <platform>" prompt;
        // gate open → "<platform> configuration is complete" copy. The old "one platform is enough"
        // wording (OnePlatformReadyMessage, dim-era commit 15949a6) compared two visible columns — with
        // only one column in view there is nothing left to compare, so it's dropped entirely rather than
        // adapted.
        private void UpdateHeader(bool gateOpen, string platform)
        {
            if (!gateOpen)
            {
                SetHeader(false,
                    $"Review the {platform} configuration status below. Complete the required items " +
                    "to continue.");
                return;
            }

            SetHeader(true, $"{platform} configuration is complete. You can continue to the final check.");
        }

        // Finds the entry for `platform` in a BuildReadiness() list; falls back to a not-ready/not-used
        // entry if somehow absent (BuildReadiness always returns both Android and iOS, so this is
        // defensive only).
        private static PlatformReadiness FindReadiness(List<PlatformReadiness> readiness, string platform)
        {
            foreach (var r in readiness)
                if (r.Platform == platform) return r;
            return new PlatformReadiness { Platform = platform, Used = false, AllOk = false };
        }

        // Builds the two PlatformReadiness entries ConfigureGate needs, from the SAME rows the table
        // just rendered: Used = some installed component declares requirements for that platform;
        // AllOk = every one of those active requirements is Configured. NotApplicable is informational
        // only (nothing the checker knows how to verify) — it does NOT block AllOk, matching the old
        // SetupScreen.CountAttention semantics. (
        private static List<PlatformReadiness> BuildReadiness(List<SetupModel.Row> rows)
        {
            bool androidUsed = false, androidOk = true;
            bool iosUsed = false, iosOk = true;

            foreach (var row in rows)
            {
                if (!row.Installed) continue;

                if (row.Android.Count > 0)
                {
                    androidUsed = true;
                    if (ConfigureGate.RowNeedsAttention(
                            row.Installed, CellsNeedAttention(row.Android),
                            row.AdFormats, SetupModel.AdFormatsSatisfiedFor(row, "Android")))
                        androidOk = false;
                }
                if (row.IOS.Count > 0)
                {
                    iosUsed = true;
                    if (ConfigureGate.RowNeedsAttention(
                            row.Installed, CellsNeedAttention(row.IOS),
                            row.AdFormats, SetupModel.AdFormatsSatisfiedFor(row, "iOS")))
                        iosOk = false;
                }

            }

            return new List<PlatformReadiness>
            {
                new PlatformReadiness { Platform = "Android", Used = androidUsed, AllOk = androidOk },
                new PlatformReadiness { Platform = "iOS",     Used = iosUsed,     AllOk = iosOk },
            };
        }

        private static bool IsOkOrNotApplicable(ReqResult result) =>
            result != null && (result.Status == ReqStatus.Configured || result.Status == ReqStatus.NotApplicable);

        // ── Action panels (CAS / Firebase) ────────────────────────────────────

        private void RenderActionPanels(List<SetupModel.Row> rows, string platform)
        {
            if (_panelsHost == null) return;

            // The CAS row carries two kinds of cells with two different steps behind them: its own
            // settings (Placements) and the Hub's Tenjin key (Tenjin step). Each panel judges only
            // the cells its step can fix, so a project with formats picked but no key gets the
            // Tenjin panel alone.
            var casRow = FindRow(rows, CasId);
            if (casRow != null)
            {
                if (RowNeedsAttentionForPlatform(casRow, platform, publishing: false))
                    _panelsHost.Add(BuildCasPanel(platform));
                if (PublishingCellsNeedAttention(casRow, platform))
                    _panelsHost.Add(BuildTenjinPanel(platform));
            }

            var firebaseRow = FindRow(rows, FirebaseId);
            if (firebaseRow != null && RowNeedsAttentionForPlatform(firebaseRow, platform, publishing: false))
                _panelsHost.Add(BuildFirebasePanel(platform));
        }

        // True when this platform's own cells (not the other platform's) have at least one active
        // requirement that isn't Configured/NotApplicable. Uses the same IsOkOrNotApplicable rule as
        // BuildReadiness/AllOk — a NotApplicable cell is informational only, so it must not trigger a
        // warning panel while the gate/header already treat it as fine (otherwise the two would disagree).
        // `publishing` picks which of the row's cells are judged (see RenderActionPanels); the ad
        // formats belong to the component's own settings, so they only count on that side.
        private static bool RowNeedsAttentionForPlatform(SetupModel.Row row, string platform, bool publishing) =>
            ConfigureGate.RowNeedsAttention(
                row.Installed,
                CellsNeedAttention(SetupModel.Filter(PickForPlatform(row.Android, row.IOS, platform), publishing)),
                !publishing && row.AdFormats,
                SetupModel.AdFormatsSatisfiedFor(row, platform));

        private static bool PublishingCellsNeedAttention(SetupModel.Row row, string platform) =>
            RowNeedsAttentionForPlatform(row, platform, publishing: true);

        private static bool CellsNeedAttention(List<SetupModel.Cell> cells)
        {
            foreach (var c in cells)
                if (!IsOkOrNotApplicable(c.Result)) return true;
            return false;
        }

        private static SetupModel.Row FindRow(List<SetupModel.Row> rows, string id)
        {
            foreach (var r in rows)
                if (r.Id == id) return r;
            return null;
        }

        // The panel leads to a wizard step, not CAS's native window: the formats, the Publishing
        // solution and Tenjin MMP are set by the wizard itself. The Tenjin key left the copy — from
        // CAS 4.8.1 it arrives from the CAS dashboard or through MediationExtras, and is no longer a
        // settings field of its own.
        private VisualElement BuildCasPanel(string platform)
        {
            var card = ActionCard(
                "⚠ CAS configuration required",
                "Choose the ad formats to enable. The Publishing solution and Tenjin MMP are set up for you.");

            card.Add(ActionButton("Configure ad formats", () => EnterStep(ConfigStepQueue.StepCas)));
            return card;
        }

        // The key is the one thing CAS cannot take from its own settings: it only accepts it at
        // initialization, so the Hub keeps it and the game hands it over (see the Tenjin step).
        private VisualElement BuildTenjinPanel(string platform)
        {
            var card = ActionCard(
                "⚠ Tenjin SDK key required",
                "CAS initializes Tenjin with this key and reports attribution and purchases through it. " +
                "Without it attribution is off.");

            card.Add(ActionButton("Enter Tenjin key", () => EnterStep(ConfigStepQueue.StepTenjin)));
            return card;
        }

        private VisualElement BuildFirebasePanel(string platform)
        {
            var card = ActionCard(
                "⚠ Firebase Analytics — configuration file missing",
                FirebaseAttentionMessage(platform));

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;

            var openConsole = ActionButton("Open Firebase Console",
                () => Application.OpenURL("https://console.firebase.google.com/"));
            openConsole.style.marginRight = 8;
            buttons.Add(openConsole);

            var locate = ActionButton("Locate file…", () => LocateFirebaseConfigFile(platform));
            buttons.Add(locate);

            card.Add(buttons);
            return card;
        }

        // Pure: the Firebase action-panel body copy for the active platform. Adapted from the dim-era
        // FirebaseAttentionMessage (commit 15949a6), which took androidMissing/iosMissing bools to name
        // whichever platform(s) were missing across both visible columns — the single-platform view only
        // ever renders this panel for the active platform's own row cells (see
        // RenderActionPanels/RowNeedsAttentionForPlatform), so it now just takes that platform directly.
        internal static string FirebaseAttentionMessage(string platform) =>
            platform == "iOS"
                ? "GoogleService-Info.plist is required for iOS. Download it from Firebase Console → " +
                  "Project Settings → Your apps."
                : "google-services.json is required for Android. Download it from Firebase Console → " +
                  "Project Settings → Your apps.";

        // "Locate file…" — user picks google-services.json / GoogleService-Info.plist from anywhere on
        // disk; validated by name and copied into Assets/. Errors surface in a dialog; success re-runs
        // the same refresh path as the Refresh button so the table/panels reflect the new file. The
        // dialog title names the expected file for the given platform.
        private void LocateFirebaseConfigFile(string platform)
        {
            var path = EditorUtility.OpenFilePanel("Select " + FirebaseFileName(platform), "", "");
            if (string.IsNullOrEmpty(path)) return; // user cancelled the panel

            var err = FirebaseConfigFile.ValidateAndCopy(path, "Assets");
            if (err != null)
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub", err, "OK");
                return;
            }

            RefreshFromDisk();
        }

        private static VisualElement ActionCard(string title, string body)
        {
            var card = new VisualElement();
            card.AddToClassList("cas-card");
            card.AddToClassList("cas-card--warn");
            card.style.flexDirection = FlexDirection.Column;
            card.style.alignItems = Align.FlexStart;
            card.style.marginTop = 10;

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("cas-card__title");
            card.Add(titleLabel);

            var bodyLabel = new Label(body);
            bodyLabel.AddToClassList("cas-card__desc");
            bodyLabel.style.marginBottom = 10;
            card.Add(bodyLabel);

            return card;
        }

        private static Button ActionButton(string text, System.Action onClick)
        {
            var btn = onClick != null ? new Button(onClick) { text = text } : new Button { text = text };
            btn.AddToClassList("cas-btn");
            btn.AddToClassList("cas-btn--soft");
            btn.AddToClassList("cas-btn--sm");
            return btn;
        }

        // ── Switch prompt (Other build target) ────────────────────────────────

        // Styled like the CAS/Firebase action panels above (same ActionCard/ActionButton
        // helpers) rather than static UXML markup, so the buttons wire to the real switch action the
        // same way the other panels wire to their real actions.
        private static VisualElement BuildSwitchPromptCard()
        {
            var card = ActionCard(
                "⚠ Publishing setup targets Android or iOS",
                "This project's active build target isn't Android or iOS. Switch to one of them to " +
                "configure publishing for that platform.");

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;

            var toAndroid = ActionButton("Switch to Android", () => SwitchActiveTarget(BuildTargetGroup.Android, BuildTarget.Android));
            toAndroid.style.marginRight = 8;
            buttons.Add(toAndroid);
            buttons.Add(ActionButton("Switch to iOS", () => SwitchActiveTarget(BuildTargetGroup.iOS, BuildTarget.iOS)));

            card.Add(buttons);
            return card;
        }

        /// <summary>
        /// The active target is Android or iOS, just not the one picked on Welcome. The generic card
        /// claimed the target "isn't Android or iOS" — wrong, and it offered no way to keep the
        /// active platform.
        /// </summary>
        internal static (string title, string body) MismatchCopy(string active, string selected) =>
            ($"⚠ Active build target is {active}, but {selected} was selected",
             $"Switch to {selected} to configure it, or configure {active} instead.");

        private VisualElement BuildMismatchCard(string active, string selected)
        {
            var (title, body) = MismatchCopy(active, selected);
            var card = ActionCard(title, body);

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;

            var toSelected = ActionButton("Switch to " + selected, () =>
                SwitchActiveTarget(
                    selected == "iOS" ? BuildTargetGroup.iOS : BuildTargetGroup.Android,
                    selected == "iOS" ? BuildTarget.iOS : BuildTarget.Android));
            toSelected.style.marginRight = 8;
            buttons.Add(toSelected);
            buttons.Add(ActionButton("Configure " + active + " instead", () =>
            {
                PlatformChoice.Select(active);
                Rebuild();
            }));

            card.Add(buttons);
            return card;
        }

        // After the switch completes, BuildTargetWatcher's existing policy reopens Configure on its own
        // if CAS is installed and the newly-active platform isn't configured yet — no separate "return
        // here" bookkeeping is added.
        private static void SwitchActiveTarget(BuildTargetGroup group, BuildTarget target) =>
            BuildTargetSwitcher.Switch(group, target);

        // ── Table rows ─────────────────────────────────────────────────────────

        // Pure: which of a pair of per-platform values to use for the given platform string.
        // ("iOS" → ios, everything else (including "Android") → android — mirrors PickForPlatform from
        // the reverted 2026-06-29 implementation.)
        internal static T PickForPlatform<T>(T android, T ios, string platform) =>
            platform == "iOS" ? ios : android;

        // Pure: which action (if any) a Configuration table cell performs when clicked. Configure
        // hands off to the wizard's own step rather than CAS's native window or a file picker: on
        // the updated map the wizard performs the configuration, not the user by hand. The route
        // depends on the row and on the requirement kind — a CAS row's Tenjin-key cell leads to the
        // Tenjin step, its other cells to Placements — never on the configured state: the
        // Configured mark stays interactive. One exception: a CAS ID that is not there yet leads to
        // Welcome, the only screen with an ID field — Placements has none, so a second platform's ID
        // (or one left at "demo") could not be entered from the wizard at all (QA 58).
        internal static ConfigCellAction ResolveCellAction(string rowId, string reqKind, bool configured = true)
        {
            if (rowId == CasId)
            {
                if (reqKind == TenjinKeySettings.Kind) return ConfigCellAction.GoToTenjinSetup;
                if (reqKind == "settingsAssetField" && !configured) return ConfigCellAction.GoToWelcome;
                return ConfigCellAction.GoToPlacements;
            }
            if (rowId == FirebaseId) return ConfigCellAction.GoToFirebaseSetup;
            return ConfigCellAction.None;
        }

        private const string AndroidFirebaseFileName = "google-services.json";
        private const string IosFirebaseFileName = "GoogleService-Info.plist";

        private static string FirebaseFileName(string platform) =>
            platform == "iOS" ? IosFirebaseFileName : AndroidFirebaseFileName;

        // Finds a Firebase config file by exact file name anywhere under Assets/ and pings it in the
        // Project window (first match wins — mirrors SetupChecker.CheckAssetFile's own lookup, which
        // isn't reusable here as it takes a whole ConfigRequirement rather than a bare file name).
        private void PingFirebaseConfigFile(string platform)
        {
            var fileName = FirebaseFileName(platform);
            try
            {
                var matches = Directory.GetFiles(Application.dataPath, fileName, SearchOption.AllDirectories);
                if (matches.Length == 0) return;

                var rel = "Assets" + matches[0].Substring(Application.dataPath.Length).Replace('\\', '/');
                var asset = AssetDatabase.LoadMainAssetAtPath(rel);
                if (asset == null) return;

                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CAS Hub] Could not locate {fileName}: {e.Message}");
            }
        }

        // Runs the resolved cell action, if any.
        private void RunCellAction(ConfigCellAction action, string platform)
        {
            switch (action)
            {
                case ConfigCellAction.OpenCasSettings:
                    CasNativeSettings.Open(platform);
                    break;
                case ConfigCellAction.PingFirebaseFile:
                    PingFirebaseConfigFile(platform);
                    break;
                case ConfigCellAction.LocateFirebaseFile:
                    LocateFirebaseConfigFile(platform);
                    break;
                case ConfigCellAction.GoToPlacements:
                    EnterStep(ConfigStepQueue.StepCas);
                    break;
                case ConfigCellAction.GoToTenjinSetup:
                    EnterStep(ConfigStepQueue.StepTenjin);
                    break;
                case ConfigCellAction.GoToFirebaseSetup:
                    EnterStep(ConfigStepQueue.StepFirebase);
                    break;
                case ConfigCellAction.GoToWelcome:
                    WelcomeScreen.RequestPlatform(platform);
                    _router.GoTo("welcome");
                    break;
            }
        }

        // Point entry: a queue of one step and a return here, instead of continuing along the
        // first-pass order.
        private void EnterStep(string stepId)
        {
            InstallerKeyStore.SetConfigQueue(new[] { stepId });
            InstallerKeyStore.SetConfigReturnTo("configure");
            _router.GoTo(ConfigStepQueue.ScreenFor(stepId));
        }

        private VisualElement BuildRow(SetupModel.Row row, bool alt, string platform)
        {
            var el = new VisualElement();
            el.AddToClassList("cas-setup-row");
            if (alt) el.AddToClassList("cas-setup-row--alt");

            var comp = new VisualElement();
            comp.AddToClassList("cas-setup-col-comp");
            if (!string.IsNullOrEmpty(row.Logo))
            {
                var logo = new VisualElement();
                logo.AddToClassList("cas-comp__logo");
                logo.AddToClassList("cas-logo");
                logo.AddToClassList("cas-logo--" + row.Logo);
                comp.Add(logo);
            }
            var name = new Label(row.Name);
            name.AddToClassList("cas-comp__name");
            comp.Add(name);

            var grid = new VisualElement();
            grid.AddToClassList("cas-setup-row__grid");
            grid.Add(comp);
            var cells = PickForPlatform(row.Android, row.IOS, platform);
            grid.Add(BuildPlatformColumn(row.Id, cells, platform));
            el.Add(grid);
            return el;
        }

        private VisualElement BuildPlatformColumn(string rowId, List<SetupModel.Cell> cells, string platform)
        {
            var col = new VisualElement();
            col.AddToClassList("cas-setup-col-plat");

            if (cells == null || cells.Count == 0)
            {
                var notUsed = new Label("Not used");
                notUsed.AddToClassList("cas-muted-cell");
                col.Add(notUsed);
                return col;
            }

            foreach (var cell in cells)
                col.Add(BuildCell(rowId, platform, cell));
            return col;
        }

        // Read-only: green "✓ <label>" when Configured, otherwise a yellow "⚠ <label>" warning —
        // Missing/NotConfigured/NotApplicable all need the same action (finish it in the plugin's
        // own settings), so the table doesn't distinguish them further. When ResolveCellAction finds
        // an action for this row, the whole cell line becomes clickable.
        private VisualElement BuildCell(string rowId, string platform, SetupModel.Cell cell)
        {
            var line = new VisualElement();
            line.AddToClassList("cas-setup-cell");

            var ok = cell.Result != null && cell.Result.Status == ReqStatus.Configured;
            var tone = ok ? "green" : "yellow";

            var dot = new VisualElement();
            dot.AddToClassList("cas-dot");
            dot.AddToClassList("cas-dot--sm");
            dot.AddToClassList("cas-dot--" + tone);
            line.Add(dot);

            var label = string.IsNullOrEmpty(cell.Label) ? "—" : cell.Label;
            var txt = new Label((ok ? "✓ " : "⚠ ") + label);
            txt.AddToClassList("cas-setup-cell__txt");
            txt.AddToClassList("cas-status--" + tone);
            line.Add(txt);

            var action = ResolveCellAction(rowId, cell.Req?.Kind, ok);
            if (action != ConfigCellAction.None)
            {
                // `rowId`/`platform` are this call's own parameters (not shared loop variables), so
                // capturing them directly in the closure is safe.
                line.AddToClassList("cas-setup-cell--click");
                line.RegisterCallback<ClickEvent>(_ => RunCellAction(action, platform));
            }

            return line;
        }
    }
}
