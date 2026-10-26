using System;
using System.Collections.Generic;
using CAS.Publishing.Installer.Scanner;
using CAS.Publishing.Installer.Telemetry;
using CAS.Publishing.Installer.Ui;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// New UI-Toolkit installer window (preview). Runs in parallel to the existing IMGUI
    /// <c>InstallerWindow</c> and shares no state with it. Iteration 1 = layout + navigation +
    /// stub data only; no Scanner/Migrator/CatalogUpdater logic is wired here.
    /// </summary>
    public sealed class InstallerWizardWindow : EditorWindow
    {
        // Comfortable default; the window is resizable. Min keeps the layout from collapsing;
        // height floor matches the design so non-scrolling screens never clip.
        private static readonly Vector2 DefaultSize = new Vector2(480, 560);
        private static readonly Vector2 MinSize     = new Vector2(440, 560);
        private static readonly Vector2 MaxSize     = new Vector2(4000, 4000);

        // Order is the wizard order; element [0] is the entry screen. Welcome sits before the
        // 3-step intro flow (ready → progress → configure → done); components/about are
        // post-intro tabs.
        // "placements", "tenjin-setup" and "firebase-setup" are configuration-phase steps. They must
        // be listed here or ResolveStartScreenPure treats a saved id as stale and, after a domain
        // reload, drops the user out of a half-finished configuration onto Components.
        private static readonly string[] ScreenOrder =
            { "welcome", "ready", "progress", "placements", "tenjin-setup", "firebase-setup", "configure", "done", "components", "about" };

        // Persists the current screen across domain reloads (e.g. the reload UPM triggers after
        // an install) so the wizard returns to where the user was instead of jumping back to Ready.
        // SessionState survives reloads and resets on editor restart.
        private const string CurrentScreenKey = "CAS.Publishing.Installer.Wizard.CurrentScreen";

        // Screens that appear as top tabs; everything else is a full-screen flow state.
        // Ready/Progress/Done are first-run-only flow screens — once set up, the user never
        // returns to the install picker, so they are NOT tabs.
        private static readonly string[] TabScreens = { "components", "configure", "about" };

        // Per-project, in UserSettings/ (HubProjectState): each client project is first-run on its
        // own, and a project re-created in the same folder is first-run again. The EditorPrefs key is
        // where the flag lived before — read once for the migration.
        private const string IntroDoneKey = "introDone";
        private static string LegacyIntroDoneKey => "CAS.Publishing.Installer.Wizard.IntroDone:" + Application.dataPath;

        /// <summary>True once the user has passed the first-run intro (Ready → Progress → Configure
        /// → Done), per project, so later opens land on the tabs instead of Ready.</summary>
        internal static bool IntroDone
        {
            get => HubProjectState.Get(IntroDoneKey, LegacyIntroDoneKey) == "1";
            set => HubProjectState.Set(IntroDoneKey, value ? "1" : null);
        }

        // SessionState (not EditorPrefs): this is a transient "return to the simplified wizard"
        // flag, not per-project persistent state. It survives the domain reload an install
        // triggers (so a WizardMode re-entry can install something mid-flow) but resets on editor
        // restart — unlike IntroDone, which is never touched by this feature.
        private const string WizardModeKey = "CAS.Publishing.Installer.Wizard.WizardMode";

        /// <summary>True while the user is re-running the stepper flow after returning from the
        /// advanced tabs via the "↺ Setup Wizard" button. Lets <see cref="UpdateTabBar"/> show the
        /// stepper again for ready/progress/configure/done even though <see cref="IntroDone"/> is
        /// already set. Cleared at the flow's two natural exits (DoneScreen, Ready's Advanced
        /// button) and self-healed on any non-flow screen — see <see cref="WizardStepper.ShouldClearWizardMode"/>.</summary>
        internal static bool WizardMode
        {
            get => SessionState.GetBool(WizardModeKey, false);
            set => SessionState.SetBool(WizardModeKey, value);
        }

        // Telemetry: whether this window instance already reported hub_opened (SessionState, so the
        // window re-created by a domain reload doesn't count as a new open), and what opened it.
        private const string WindowOpenKey = "CAS.Publishing.Installer.Telemetry.WindowOpen";
        private const string OpenSourceKey = "CAS.Publishing.Installer.Telemetry.OpenSource";
        private const string LastScreenKey = "CAS.Publishing.Installer.Telemetry.LastScreen";

        private WizardRouter _router;
        private VisualElement _tabBar;
        private Button _tabComponents;
        private Button _tabConfigure;
        private Button _tabAbout;
        private Button _btnSetupWizard;
        private VisualElement _aboutBadge;

        private VisualElement _stepper;
        private Label _step1;
        private Label _step2;
        private Label _step3;

        [MenuItem("Assets/CleverAdsSolutions/Hub")]
        public static void Open()
        {
            var window = GetWindow<InstallerWizardWindow>(true, "CAS.AI Publishing Hub", true);
            window.minSize = MinSize;
            window.maxSize = MaxSize;

            // Give a comfy initial size without shrinking a window the user already enlarged.
            var r = window.position;
            if (r.width < DefaultSize.x || r.height < DefaultSize.y)
                window.position = new Rect(r.x, r.y,
                    Mathf.Max(r.width, DefaultSize.x), Mathf.Max(r.height, DefaultSize.y));

            window.Show();
        }

        /// <summary>
        /// Reopens the wizard at the first-run Ready screen, clearing the per-project intro flag.
        /// Ready/Progress are not tabs (you don't normally return to the install picker), so this
        /// is the way back to redo setup from scratch.
        /// </summary>
        [MenuItem("Assets/CleverAdsSolutions/Hub (Restart Intro)")]
        public static void OpenFirstRun()
        {
            IntroDone = false;
            SessionState.SetString(CurrentScreenKey, "welcome"); // don't restore a previous tab
            Open();
            if (HasOpenInstances<InstallerWizardWindow>())
                GetWindow<InstallerWizardWindow>()._router?.GoTo("welcome");
        }

        /// <summary>Top-level entry — testers looked for the Hub in the main menu bar, not under
        /// Assets/CleverAdsSolutions (which reads as the ad-SDK settings menu). Same window.</summary>
        [MenuItem("CAS Hub/Open Hub")]
        public static void OpenFromMainMenu() => Open();

        /// <summary>Main-menu shortcut straight to the Components tab. Deliberately works before the
        /// intro too (explicit user intent beats the first-run Ready default — ResolveStartScreenPure
        /// restores any valid saved screen id regardless of <see cref="IntroDone"/>).</summary>
        [MenuItem("CAS Hub/Components")]
        public static void OpenAtComponents()
        {
            SessionState.SetString(CurrentScreenKey, "components");
            Open();
            // A fresh Open() already landed on Components via ResolveStartScreen; only navigate an
            // ALREADY-open window that's on a different screen (same pattern as OpenAtConfigure).
            if (HasOpenInstances<InstallerWizardWindow>())
            {
                var router = GetWindow<InstallerWizardWindow>()._router;
                if (router != null && router.Current != "components")
                    router.GoTo("components");
            }
        }

        /// <summary>
        /// Opens the wizard on the Configuration screen (the build-target watcher uses this when CAS
        /// is installed but the newly active platform's CAS id isn't configured yet). Does NOT clear
        /// <see cref="IntroDone"/> — it is a targeted "configure this platform" prompt, not a first-run
        /// reset. Unlike the old Welcome-preselect flow, no platform id needs to be threaded through —
        /// the Configuration screen re-scans and shows per-platform status itself on every OnEnter.
        /// </summary>
        public static void OpenAtConfigure()
        {
            // Already open ON Configure: a build-target switch must not re-enter and force a redundant
            // OnEnter/rescan on a screen the user is already looking at (WizardRouter.Show always
            // re-invokes OnEnter, even for the current screen). Leave them be — the Configure screen
            // rescans on its own Refresh/OnEnter cadence anyway. (HasOpenInstances first, so
            // GetWindow never creates a window just to check.)
            if (HasOpenInstances<InstallerWizardWindow>() &&
                GetWindow<InstallerWizardWindow>()._router?.Current == "configure")
                return;

            SessionState.SetString(CurrentScreenKey, "configure");
            TagOpenSource("build_target");
            Open();
            // A fresh Open() already previewed Configure (ResolveStartScreen → "configure", since we
            // just set CurrentScreenKey) and ran OnEnter once — only navigate an ALREADY-open window
            // that's on a DIFFERENT screen, so a brand-new window never gets a redundant double
            // OnEnter/rescan.
            if (HasOpenInstances<InstallerWizardWindow>())
            {
                var router = GetWindow<InstallerWizardWindow>()._router;
                if (router != null && router.Current != "configure")
                    router.GoTo("configure");
            }
        }

        /// <summary>Closes the window if open — used by the All Done screen's Close button.</summary>
        public static void CloseActive()
        {
            if (HasOpenInstances<InstallerWizardWindow>())
                GetWindow<InstallerWizardWindow>().Close();
        }

        /// <summary>
        /// Re-enters the currently shown screen of an open wizard window — used by
        /// <see cref="MetadataRefreshWatcher"/> so a background state change (the metadata
        /// package registering without a domain reload) reflects in the UI immediately, without
        /// waiting for the user to click Refresh/Retry. No-op when no window is open.
        /// <see cref="WizardRouter.GoTo"/> re-invokes <c>OnEnter</c> even when the target equals
        /// the current screen (it just skips the history push) — screens' OnEnter/Rebuild are
        /// idempotent, so this is safe to call speculatively.
        /// </summary>
        internal static void RefreshActive()
        {
            if (!HasOpenInstances<InstallerWizardWindow>()) return;
            var router = GetWindow<InstallerWizardWindow>()._router;
            var current = router?.Current;
            if (current != null) router.GoTo(current);
        }

        /// <summary>Re-enters <paramref name="screenId"/> only when an open window is showing it.</summary>
        internal static void RefreshIfShowing(string screenId)
        {
            // FindObjectsOfTypeAll, not GetWindow: GetWindow focuses the window, and this runs on
            // every build-target switch — a Hub docked as a background tab must stay there.
            foreach (var window in Resources.FindObjectsOfTypeAll<InstallerWizardWindow>())
            {
                var router = window._router;
                if (router?.Current == screenId) router.GoTo(screenId);
            }
        }

        /// <summary>True when a wizard window is currently up. Wraps the generic
        /// <c>HasOpenInstances</c> so callers outside this type can ask without naming it.</summary>
        internal static bool HasOpenInstance() => HasOpenInstances<InstallerWizardWindow>();

        /// <summary>
        /// Runs the show path after the metadata catalog registers mid-session — the case
        /// <see cref="MetadataArrivalRoute"/> describes, where <c>Bootstrap.RunOnce</c> already
        /// bailed at <c>NotInstalled</c> and no domain reload will re-run it. Scans against the
        /// now-available catalog and hands the report to the same
        /// <see cref="ShowIfReportChanged"/> gate the reload path uses, so the intro flag still
        /// decides whether the window actually pops.
        ///
        /// Deferred to <c>delayCall</c>: this arrives inside a Package Manager event, and opening
        /// an EditorWindow (plus a full project scan) from there runs during package registration.
        /// </summary>
        internal static void ShowAfterMetadataArrival()
        {
            EditorApplication.delayCall += () =>
            {
                var load = CAS.Publishing.Installer.Catalog.CatalogLoader.Load();
                if (load.Status != CAS.Publishing.Installer.Catalog.CatalogLoadStatus.Ok) return;
                ShowIfReportChanged(ProjectScanner.Scan(load.Catalog));
            };
        }

        /// <summary>
        /// Opens the wizard only when the scan report differs from the last one shown
        /// (shared hash gate via <see cref="ScanReportStore"/>, so it doesn't re-pop on every
        /// reload). Bootstrap calls this through its UI hook.
        /// </summary>
        public static void ShowIfReportChanged(ScanReport report)
        {
            if (report == null) return;

            // First run: always open (idempotent; record hash AFTER so an install/self-delete reload
            // that kills the just-opened window can't latch the hash and suppress later opens).
            if (!IntroDone)
            {
                TagOpenSource("auto");
                Open();
                ScanReportStore.SetLastShownHash(report.Hash);
                return;
            }

            // After first-run: auto-open ONLY when the installer drove the change (a manifest mutation
            // set the one-shot signal). Unrelated/manual UPM changes never re-pop the window — updates
            // surface via the About badge instead. ResolveStartScreen restores the saved screen
            // (Components for a normal post-intro reopen).
            if (!CAS.Publishing.Installer.Common.InstallReloadSignal.ConsumePending()) return;

            TagOpenSource("auto");
            Open();
            ScanReportStore.SetLastShownHash(report.Hash);
        }

        private void CreateGUI()
        {
            // Opening the wizard self-heals: make sure the metadata catalog is present (install it
            // if missing) and current (check the registry for a newer one). Async — never blocks the
            // window, and works regardless of whether the auto-open path ran.
            CAS.Publishing.Installer.Bootstrap.EnsureMetadata();

            // registryFirst records (CAS): ask the registry whether it has caught up with the git
            // source, so a row offers Switch to UPM only when it can. Async; redraws the list screens.
            var catalogLoad = CAS.Publishing.Installer.Catalog.CatalogLoader.Load();
            if (catalogLoad.Status == CAS.Publishing.Installer.Catalog.CatalogLoadStatus.Ok)
                CAS.Publishing.Installer.Catalog.RegistryVersions.RefreshAsync(catalogLoad.Catalog, () =>
                {
                    RefreshIfShowing("components");
                    RefreshIfShowing("ready");
                });

            // Catches the case where CAS arrived between Editor sessions: an id captured on a
            // previous pass is still in the key store and should land silently, without making
            // the user walk the wizard again. Fast-exits when nothing was ever captured.
            CasIdApplier.ApplyPending();

            var root = rootVisualElement;

            root.styleSheets.Add(WizardAssets.LoadStyle("theme"));
            ApplyFont(root);

            var shell = WizardAssets.LoadTree("WizardShell");
            shell.CloneTree(root);

            var content = root.Q<VisualElement>("cas-content");
            _router = new WizardRouter(content, OnScreenShown);

            RegisterScreens(_router);
            BuildTabBar(root);
            RefreshUpdateBadge();

            // The Configuration tab only makes sense once something is installed (you configure
            // installed packages). Recomputed each time the window is built — installs trigger a reload.
            if (_tabConfigure != null)
                _tabConfigure.style.display = AnyComponentInstalled() ? DisplayStyle.Flex : DisplayStyle.None;

            var start = ResolveStartScreen();
            TrackOpened(start);
            _router.Preview(start);

            // If a recommended-install click armed a resume while the metadata catalog was still
            // installing, and the catalog is now present, continue that install automatically. The
            // catalog-Ok check short-circuits ConsumeResume, so the flag is preserved (not burned)
            // when the catalog still isn't ready — a later open retries. Deferred to delayCall so the
            // confirm modal doesn't run mid-CreateGUI.
            if (CAS.Publishing.Installer.Catalog.CatalogLoader.Load().Status == CAS.Publishing.Installer.Catalog.CatalogLoadStatus.Ok
                && AutoInstaller.ConsumeResume())
            {
                var router = _router;
                EditorApplication.delayCall += () => { if (router != null) AutoInstaller.StartAll(router); };
            }
        }

        // Only a window that is about to be created consumes the tag (TrackOpened); one set while
        // the window is already open would linger and mislabel the next real open.
        private static void TagOpenSource(string source)
        {
            if (!HasOpenInstances<InstallerWizardWindow>())
                SessionState.SetString(OpenSourceKey, source);
        }

        private static void TrackOpened(string startScreen)
        {
            if (SessionState.GetBool(WindowOpenKey, false)) return; // re-created by a domain reload
            SessionState.SetBool(WindowOpenKey, true);
            SessionState.EraseString(LastScreenKey);

            var source = SessionState.GetString(OpenSourceKey, "menu");
            SessionState.EraseString(OpenSourceKey);
            HubTelemetry.Track("hub_opened", new Dictionary<string, object>
            {
                ["source"] = source,
                ["screen"] = startScreen,
                ["introDone"] = IntroDone,
            });
        }

        // Not called on a domain reload (the window is serialized and re-created), only on a real close.
        private void OnDestroy()
        {
            SessionState.SetBool(WindowOpenKey, false);
            HubTelemetry.Track("hub_closed", new Dictionary<string, object>
            {
                ["screen"] = _router?.Current ?? "",
                ["introDone"] = IntroDone,
            });
        }

        private static bool AnyComponentInstalled()
        {
            if (ComponentStatusProvider.TryGetStatuses(out var statuses, out _))
                foreach (var s in statuses)
                    if (s.Installed) return true;
            return false;
        }

        private static string ResolveStartScreen()
        {
            // Mid-session restore (e.g. after the install-triggered domain reload).
            var saved = SessionState.GetString(CurrentScreenKey, "");
            // A session that sat on the Tenjin step under an older Hub reopens on Configuration.
            if (saved == ConfigStepQueue.ScreenFor(ConfigStepQueue.StepTenjin) && !TenjinKeySettings.StepEnabled)
                saved = "configure";
            return ResolveStartScreenPure(saved, IntroDone);
        }

        /// <summary>
        /// Pure decision logic for <see cref="ResolveStartScreen"/> — no SessionState/EditorPrefs
        /// reads, so it's directly testable. <paramref name="saved"/> is valid (present in
        /// <see cref="ScreenOrder"/>) → restore it, EXCEPT once past the intro a saved "welcome" or
        /// "ready" is redirected to "components" (past the intro you return to neither the CAS ID
        /// entry nor the install picker). Invalid/empty/stale ids (e.g. "integration" from a removed
        /// screen) fall through to the fresh-open rule: a first run starts on Welcome — the platform
        /// and CAS ID must be captured before anything installs — and afterwards it lands on the
        /// Components tab.
        /// </summary>
        internal static string ResolveStartScreenPure(string saved, bool introDone)
        {
            if (!string.IsNullOrEmpty(saved) && Array.IndexOf(ScreenOrder, saved) >= 0)
            {
                if (introDone && (saved == "ready" || saved == "welcome")) return "components";
                return saved;
            }

            return introDone ? "components" : "welcome";
        }

        private static void ApplyFont(VisualElement root)
        {
            // Bind Inter in C# (not via USS url()) so a missing font can never break the
            // stylesheet — it simply falls back to the editor default. Font is inherited.
            var font = AssetDatabase.LoadAssetAtPath<Font>(WizardAssets.Fonts + "/Inter-Regular.ttf");
            if (font != null)
                root.style.unityFontDefinition = new StyleFontDefinition(font);
        }

        private void RegisterScreens(WizardRouter router)
        {
            // Welcome is back (spec 2026-09-01): it sits BEFORE the 3-step intro flow
            // (Ready → Progress → Configure → Done) and carries no stepper step number.
            // Placements is the "cas" configuration step (spec 2026-09-03): ad formats,
            // Publishing solution and Tenjin MMP moved back OUT of CAS.AI's own settings
            // window and into the wizard, reversing the 2026-09-01 scope.
            router.Register(new WelcomeScreen());
            router.Register(new ReadyScreen());
            router.Register(new ComponentsScreen());
            router.Register(new ProgressScreen());
            router.Register(new PlacementsScreen());
            router.Register(new TenjinSetupScreen());
            router.Register(new FirebaseSetupScreen());
            router.Register(new DoneScreen());
            router.Register(new ConfigureScreen());
            router.Register(new AboutScreen());
        }

        private void OnScreenShown(string id)
        {
            SessionState.SetString(CurrentScreenKey, id);
            UpdateTabBar(id);

            // Screens re-enter on refreshes and reload restores; only a change of screen is an event.
            if (SessionState.GetString(LastScreenKey, "") != id)
            {
                SessionState.SetString(LastScreenKey, id);
                HubTelemetry.Track("screen_shown", "screen", id);
            }
        }

        private void BuildTabBar(VisualElement root)
        {
            _tabBar        = root.Q<VisualElement>("cas-tabbar");
            _tabComponents = root.Q<Button>("tab-components");
            _tabConfigure  = root.Q<Button>("tab-configure");
            _tabAbout      = root.Q<Button>("tab-about");
            _btnSetupWizard = root.Q<Button>("btn-setup-wizard");

            _stepper = root.Q<VisualElement>("cas-stepper");
            _step1   = root.Q<Label>("step-install");
            _step2   = root.Q<Label>("step-configure");
            _step3   = root.Q<Label>("step-done");

            // Registered once here (not re-registered per SetStep call); the handler recomputes
            // clickability from the router's CURRENT screen id at click time, so it never acts on
            // stale state captured when the stepper was last redrawn.
            if (_step1 != null) _step1.RegisterCallback<ClickEvent>(_ => OnStepClicked(1));
            if (_step2 != null) _step2.RegisterCallback<ClickEvent>(_ => OnStepClicked(2));
            if (_step3 != null) _step3.RegisterCallback<ClickEvent>(_ => OnStepClicked(3));

            if (_tabComponents != null) _tabComponents.clicked += () => _router.GoTo("components");
            if (_tabConfigure != null)  _tabConfigure.clicked  += () => _router.GoTo("configure");
            if (_tabAbout != null)      _tabAbout.clicked      += () => _router.GoTo("about");

            // Return path out of the advanced tabs: arms WizardMode so the stepper flow shows again
            // (see UpdateTabBar) and jumps to Ready, which re-detects installed state on its own.
            if (_btnSetupWizard != null)
                _btnSetupWizard.clicked += () =>
                {
                    WizardMode = true;
                    _router.GoTo("ready");
                };

            if (_tabAbout != null)
            {
                // A Unity Button is a TextElement: once it gets a child element, its own `text` no
                // longer drives the element's width and the caption clips ("About" → "Abou"). Move the
                // caption into a child Label (it inherits color/font from .cas-tab, incl. the active
                // state) so the button sizes to the label, then overlay the absolutely-positioned badge.
                var label = new Label(_tabAbout.text) { pickingMode = PickingMode.Ignore };
                label.AddToClassList("cas-tab__label");
                _tabAbout.text = string.Empty;
                _tabAbout.Add(label);

                _aboutBadge = new VisualElement();
                _aboutBadge.AddToClassList("cas-tab__badge");
                _tabAbout.Add(_aboutBadge);
            }
        }

        // Shows the stepper header while still inside the pre-intro 3-step flow (Ready/Progress/
        // Configure/Done, before IntroDone flips) and the tab bar otherwise. "configure" is
        // shared between step 2 of the flow and the post-intro Configuration tab — IntroDone (not
        // the id alone) decides which header applies, so re-entering the Configure tab after the intro
        // shows tabs like Components/About do, not a step-2-of-3 stepper.
        private void UpdateTabBar(string id)
        {
            // Self-heal BEFORE computing showStepper, so a stuck WizardMode flag (e.g. a watchdog
            // exit from Progress straight to Components) can't make THIS render show a stepper for
            // an unrelated tab click — order matters here.
            if (WizardStepper.ShouldClearWizardMode(id) && WizardMode)
                WizardMode = false;

            var step = WizardStepper.StepFor(id);
            var showStepper = WizardStepper.ShouldShowStepper(id, IntroDone, WizardMode);
            var isTab = !showStepper && Array.IndexOf(TabScreens, id) >= 0;

            if (_tabBar != null)
                _tabBar.style.display = isTab ? DisplayStyle.Flex : DisplayStyle.None;
            if (_stepper != null)
                _stepper.style.display = showStepper ? DisplayStyle.Flex : DisplayStyle.None;

            if (showStepper) UpdateStepper(id, step.Value);

            SetTabActive(_tabComponents, id == "components");
            SetTabActive(_tabConfigure, id == "configure");
            SetTabActive(_tabAbout, id == "about");
        }

        private static void SetTabActive(Button tab, bool active)
        {
            if (tab == null) return;
            if (active) tab.AddToClassList("cas-tab--active");
            else        tab.RemoveFromClassList("cas-tab--active");
        }

        private void UpdateStepper(string currentScreenId, int currentStep)
        {
            SetStep(_step1, "Install", 1, currentStep, currentScreenId);
            SetStep(_step2, "Configure", 2, currentStep, currentScreenId);
            SetStep(_step3, "Done", 3, currentStep, currentScreenId);
        }

        // Pending: "N Label" in muted grey. Active (currentStep == index): "N Label" in accent blue.
        // Passed (currentStep > index): "✓ Label" in green. Clickable passed steps (see
        // WizardStepper.IsStepClickable) additionally get "cas-introstep--link" so the cursor/hover
        // affordance shows without changing the base state classes above.
        private static void SetStep(Label label, string name, int index, int currentStep, string currentScreenId)
        {
            if (label == null) return;

            label.RemoveFromClassList("cas-introstep--active");
            label.RemoveFromClassList("cas-introstep--done");
            label.RemoveFromClassList("cas-introstep--link");

            if (currentStep > index)
            {
                label.text = "✓ " + name;
                label.AddToClassList("cas-introstep--done");
            }
            else
            {
                label.text = index + " " + name;
                if (currentStep == index) label.AddToClassList("cas-introstep--active");
            }

            if (WizardStepper.IsStepClickable(index, currentScreenId))
                label.AddToClassList("cas-introstep--link");
        }

        // Recomputes clickability from the router's CURRENT screen id at click time (not from
        // whatever state was true when the stepper was last redrawn) — the handler itself is
        // registered exactly once, in BuildTabBar.
        private void OnStepClicked(int step)
        {
            var currentId = _router?.Current;
            if (currentId == null) return;
            if (!WizardStepper.IsStepClickable(step, currentId)) return;
            _router.GoTo(WizardStepper.TargetScreenFor(step));
        }

        // Shows a dot on the About tab when a newer installer version is published. Probes the
        // registry at most once per editor session (SessionState), caches the result so reopening
        // the window within the session reflects it without re-checking.
        private void RefreshUpdateBadge()
        {
            // Git-installed installer: no Verdaccio version to compare against, and About shows a
            // manual git-update instruction — so never show the "update available" badge here.
            if (CAS.Publishing.Installer.Common.InstallerSource.IsGit()) { SetBadge(false); return; }

            SetBadge(SessionState.GetBool(UpdateBadgeState.AvailableKey, false));

            if (SessionState.GetBool(UpdateBadgeState.ProbedKey, false)) return;
            SessionState.SetBool(UpdateBadgeState.ProbedKey, true);

            CAS.Publishing.Installer.Catalog.CatalogUpdater.CheckLatestVersion(UpdateBadgeState.PackageId,
                onSuccess: latest =>
                {
                    var available = CAS.Publishing.Installer.Catalog.CatalogUpdater.IsNewer(latest, WizardAssets.InstallerVersion);
                    SessionState.SetBool(UpdateBadgeState.AvailableKey, available);
                    SetBadge(available);
                },
                onFailure: _ => { /* leave the badge as-is on a failed probe */ });
        }

        private void SetBadge(bool on)
        {
            if (_aboutBadge == null) return;
            if (on) _aboutBadge.AddToClassList("cas-tab__badge--on");
            else    _aboutBadge.RemoveFromClassList("cas-tab__badge--on");
        }
    }
}
