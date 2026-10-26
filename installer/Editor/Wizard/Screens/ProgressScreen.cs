using System.Collections.Generic;
using CAS.Publishing.Installer.Telemetry;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.UIElements;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Installation Progress. In auto-install mode (set by <see cref="AutoInstaller"/>) it drives
    /// a step-by-step install: poll the real UPM package list, and as each default component
    /// resolves, kick off the next one — surviving the per-step domain reloads — then route to
    /// Done. If this screen is entered with no failure, no completion, and no active auto-install
    /// (e.g. Back from Configure after Continue already cleared the run) there is nothing to show,
    /// so it redirects to Ready instead of rendering fabricated placeholder data.
    /// </summary>
    internal sealed class ProgressScreen : IWizardScreen
    {
        public string Id => "progress";
        public VisualElement Root { get; }

        private WizardRouter _router;
        private bool _bound;

        private readonly VisualElement _stepsHost;
        private readonly VisualElement _fill;
        private readonly Label _pct;

        // Header + footer, swapped between the three inline states (installing / failed / complete).
        private readonly Label _title;
        private readonly Label _subtitle;
        private readonly Button _btnCancel;
        private readonly Button _btnContinue;

        // Failure panel.
        private readonly VisualElement _failPanel;
        private readonly Label _failTitle;
        private readonly Label _failMessage;
        private readonly Button _btnRetryStep;
        private readonly Button _btnCopyLog;
        private ProgressFailureModel _failure;
        private string _failedId;

        // Completion panel.
        private readonly VisualElement _donePanel;

        // Auto-install tracking.
        private const double StepPauseSeconds = 0.7; // breathing room so the UI paints between steps
        // Watchdog: a step that hasn't resolved this long after its install was issued is treated as
        // stalled (failed git clone / registry download, or a PackageManager lock) and surfaced to the
        // user instead of spinning forever. Generous so a slow-but-working clone resolves first — the
        // watchdog only fires while the step is STILL unresolved.
        private const double StepTimeoutSeconds = 150;
        // A Client.List request that never completes (PM busy / lock contention) would freeze the whole
        // driver, since Drive only runs once a list completes. Abandon and retry a request older than this.
        private const double ListRequestTimeoutSeconds = 25;
        private const string LogPrefix = "[CAS Hub]";
        private ListRequest _listReq;
        private double _listReqStartedAt; // when the in-flight _listReq was issued (stuck-request guard)
        private IVisualElementScheduledItem _poll;
        private List<string> _targetIds;
        private Dictionary<string, string> _targetNames;
        private double _issueAtTime; // when the next install may be issued (0 = not scheduled)

        public ProgressScreen()
        {
            Root = new VisualElement();
            WizardAssets.CloneInto("Progress", Root);
            _stepsHost = Root.Q<VisualElement>("progress-steps");
            _fill      = Root.Q<VisualElement>("progress-fill");
            _pct       = Root.Q<Label>("progress-pct");

            _title     = Root.Q<Label>("progress-title");
            _subtitle  = Root.Q<Label>("progress-subtitle");
            _btnCancel = Root.Q<Button>("btn-cancel");
            _btnContinue = Root.Q<Button>("btn-continue");

            _failPanel   = Root.Q<VisualElement>("fail-panel");
            _failTitle   = Root.Q<Label>("fail-title");
            _failMessage = Root.Q<Label>("fail-message");
            _btnRetryStep = Root.Q<Button>("btn-retry-step");
            _btnCopyLog   = Root.Q<Button>("btn-copy-log");

            _donePanel = Root.Q<VisualElement>("done-panel");
        }

        public void OnEnter(WizardRouter router)
        {
            _router = router;
            if (!_bound)
            {
                _bound = true;
                if (_btnCancel != null) _btnCancel.clicked += OnCancel;
                if (_btnRetryStep != null) _btnRetryStep.clicked += OnRetryStep;
                if (_btnCopyLog != null) _btnCopyLog.clicked += OnCopyLog;
                if (_btnContinue != null) _btnContinue.clicked += OnContinue;
            }

            // Persisted phase flags are checked BEFORE IsActive, and survive a domain reload that
            // happens after a step failed or the whole run finished but before the user acted on it
            // (Retry/Cancel/Continue) — so a reload never silently drops back to the "nothing to
            // show" redirect or a hung poll. See AutoInstaller.MarkFailed/MarkCompleted for why
            // these can't be derived from the watchdog deadline or IssuedIndex alone.
            if (AutoInstaller.TryGetFailure(out var failedId, out var failedDetail))
                EnterFailedMode(failedId, failedDetail);
            else if (AutoInstaller.IsCompleted)
                EnterCompletedMode();
            else if (AutoInstaller.IsActive)
                EnterAutoMode();
            else
                // Nothing to show — reached e.g. via Back from Configure after Continue cleared the
                // run (see OnContinue). Redirect instead of rendering fabricated preview data.
                _router.GoTo("ready");
        }

        private void OnCancel()
        {
            HubTelemetry.Track("install_cancelled", "screen", Id);
            StopPoll();
            AutoInstaller.Clear();
            _router.GoTo("ready");
        }

        private void OnContinue()
        {
            AutoInstaller.Clear();

            ConfigPhase.Enter(_router);
        }

        // ── Inline panel state (installing / failed / complete) ──────────────

        /// <summary>Resets the screen to its default "installing" chrome — called on every fresh
        /// entry so a failure/completion shown on a previous run doesn't linger.</summary>
        private void ResetPanels()
        {
            if (_failPanel != null) _failPanel.style.display = DisplayStyle.None;
            if (_donePanel != null) _donePanel.style.display = DisplayStyle.None;
            if (_title != null) _title.text = "Installation Progress";
            if (_subtitle != null) _subtitle.style.display = DisplayStyle.None;
            if (_btnCancel != null) _btnCancel.style.display = DisplayStyle.Flex;
            if (_btnContinue != null) _btnContinue.style.display = DisplayStyle.None;
        }

        private void ShowFailure(string id, string detail)
        {
            _failedId = id;
            _failure = ProgressFailureModel.From(NameOf(id), detail);
            if (_failTitle != null) _failTitle.text = _failure.Title;
            if (_failMessage != null) _failMessage.text = _failure.Message;
            if (_failPanel != null) _failPanel.style.display = DisplayStyle.Flex;
        }

        private void OnRetryStep()
        {
            // Guard FIRST: if we can't actually identify the failed step, don't hide the panel — that
            // would strand the user on a blank screen with no visible state and no poll running.
            // Leaving the panel up keeps Copy log / Cancel available.
            if (string.IsNullOrEmpty(_failedId) || _targetIds == null) return;

            if (_failPanel != null) _failPanel.style.display = DisplayStyle.None;
            AutoInstaller.ClearFailure();
            HubTelemetry.Track("component_retry", "package", _failedId);

            var idx = _targetIds.IndexOf(_failedId);
            if (idx >= 0)
                // Roll IssuedIndex back to just before the failed step so Drive() treats it as
                // not-yet-issued and re-runs InstallOne for THIS step only. Earlier steps stay
                // resolved (Drive reads live UPM state, not IssuedIndex, to know what's done) and
                // are never re-installed — the SessionState queue otherwise carries on unchanged.
                AutoInstaller.IssuedIndex = idx - 1;

            _issueAtTime = 0;
            _failedId = null;
            _failure = null;
            StartPoll();
        }

        private void OnCopyLog()
        {
            if (_failure != null) EditorGUIUtility.systemCopyBuffer = _failure.Log;
        }

        private void ShowComplete()
        {
            // Packages are in — this is the first moment the CAS ID entered on Welcome can reach
            // CASSettings: until now the CAS package simply did not exist in a clean project.
            CasIdApplier.ApplyPending();

            if (_title != null) _title.text = "Installation complete";
            if (_subtitle != null)
            {
                _subtitle.text = "All components were installed successfully.";
                _subtitle.style.display = DisplayStyle.Flex;
            }
            if (_donePanel != null) _donePanel.style.display = DisplayStyle.Flex;
            if (_btnCancel != null) _btnCancel.style.display = DisplayStyle.None;
            if (_btnContinue != null) _btnContinue.style.display = DisplayStyle.Flex;
        }

        // ── Auto-install mode (real, step by step) ───────────────────────────

        /// <summary>Populates <see cref="_targetIds"/>/<see cref="_targetNames"/> from the current
        /// component statuses, once per screen instance. Shared by the live driver and the
        /// reload-recovery paths (<see cref="EnterCompletedMode"/>/<see cref="EnterFailedMode"/>),
        /// which need the same id→name map to render without waiting on a poll tick.</summary>
        private void PopulateTargetsIfNeeded()
        {
            if (_targetIds != null) return;
            _targetIds = new List<string>();
            _targetNames = new Dictionary<string, string>();
            if (ComponentStatusProvider.TryGetStatuses(out var statuses, out _))
            {
                foreach (var s in statuses)
                {
                    // A non-UPM copy is already on disk — it will never appear in the UPM
                    // package list, so polling for it would stall the sequence forever.
                    // Skip it (it's also skipped by the planner, so it's never installed here).
                    if (s.OutsideUpm) continue;
                    _targetIds.Add(s.Id);
                    _targetNames[s.Id] = s.DisplayName;
                }
            }
        }

        private void EnterAutoMode()
        {
            ResetPanels();
            PopulateTargetsIfNeeded();

            if (_targetIds.Count == 0)
            {
                _stepsHost?.Clear();
                var note = new Label("Couldn't read components — is the catalog installed?");
                note.AddToClassList("cas-empty-note");
                _stepsHost?.Add(note);
                return;
            }

            Render(new HashSet<string>(), currentIndex: 0);
            StartPoll();
        }

        /// <summary>Reload-recovery entry: the whole queue finished (<see cref="AutoInstaller.IsCompleted"/>)
        /// on a PREVIOUS instance of this screen, and a domain reload (unrelated to this run — it
        /// already finished) has now recreated it. Rebuilds the step list from live component status
        /// (all resolved, since the run completed) and redraws the completion panel — no polling
        /// needed, nothing left to install.</summary>
        private void EnterCompletedMode()
        {
            StopPoll();
            ResetPanels();
            PopulateTargetsIfNeeded();
            if (_targetIds.Count > 0)
                Render(new HashSet<string>(_targetIds), currentIndex: -1);
            else
                _stepsHost?.Clear();
            ShowComplete();
        }

        /// <summary>Reload-recovery entry: a step failed synchronously (before <see cref="AutoInstaller.StepDeadline"/>
        /// was ever armed, so the watchdog has nothing to fire on) and a domain reload has recreated
        /// this screen before the user clicked Retry/Cancel. Redraws the failure panel from the
        /// persisted id/detail instead of silently sitting on a poll that will never resume by itself.</summary>
        private void EnterFailedMode(string id, string detail)
        {
            StopPoll();
            ResetPanels();
            PopulateTargetsIfNeeded();
            if (_targetIds.Count > 0)
            {
                // Steps before the failed one are assumed resolved (that's how the driver reached
                // this one); the fail panel below is the authoritative status regardless of this
                // best-effort step-list paint — the next Retry re-polls live state anyway.
                var idx = _targetIds.IndexOf(id);
                var resolvedGuess = new HashSet<string>();
                if (idx > 0)
                    for (var i = 0; i < idx; i++) resolvedGuess.Add(_targetIds[i]);
                Render(resolvedGuess, currentIndex: idx);
            }
            else
            {
                _stepsHost?.Clear();
            }
            ShowFailure(id, detail);
        }

        private void StartPoll()
        {
            if (_poll == null)
                _poll = Root.schedule.Execute(PollOnce).Every(400);
            else
                _poll.Resume();
        }

        private void StopPoll()
        {
            _poll?.Pause();
            _listReq = null;
        }

        private void PollOnce()
        {
            if (!AutoInstaller.IsActive) { StopPoll(); return; }
            if (Root.resolvedStyle.display == DisplayStyle.None) return;

            if (_listReq == null)
            {
                // offlineMode=true; includeIndirectDependencies=true — "resolved" must mean the same
                // thing here as in StateClassifier (registered with UPM, direct OR transitive), or a
                // transitively-satisfied target dead-ends: EDM4U arrives via com.google.firebase.app
                // on the Firebase step, so the direct-only list never shows it resolved while the
                // planner (correctly) refuses to add it — "didn't add a manifest entry" every time.
                _listReq = Client.List(true, true);
                _listReqStartedAt = EditorApplication.timeSinceStartup;
                return;
            }
            if (!_listReq.IsCompleted)
            {
                // Don't let a single stuck list request freeze the driver forever — abandon it and the
                // next tick re-issues a fresh one.
                if (EditorApplication.timeSinceStartup - _listReqStartedAt > ListRequestTimeoutSeconds)
                {
                    Debug.LogWarning($"{LogPrefix} Package list request stalled (>{ListRequestTimeoutSeconds}s); retrying.");
                    _listReq = null;
                }
                return;
            }

            var resolved = new HashSet<string>();
            if (_listReq.Status == StatusCode.Success && _listReq.Result != null)
                foreach (var p in _listReq.Result)
                    resolved.Add(p.name);
            else if (_listReq.Status == StatusCode.Failure)
                Debug.LogWarning($"{LogPrefix} Package list failed: {_listReq.Error?.message}");
            _listReq = null;

            Drive(resolved);
        }

        /// <summary>Renders the current state and advances the install sequence.</summary>
        private void Drive(HashSet<string> resolved)
        {
            // First component not yet resolved is the "current" step.
            var next = AutoInstallProgress.FirstUnresolved(_targetIds, resolved);
            TrackResolved(resolved);

            Render(resolved, next);

            if (next < 0)
            {
                // Everything resolved → show the completion state inline (no navigation to a separate
                // Done screen). Deliberately does NOT call AutoInstaller.Clear() here — that's deferred
                // to Cancel/Continue (the only two exits from this state) so an unrelated domain reload
                // landing back on this screen before the user acts still finds AutoInstaller.IsCompleted
                // true and redraws the completion panel via EnterCompletedMode, instead of falling
                // through to the "nothing to show" redirect.
                StopPoll();
                if (!AutoInstaller.IsCompleted)
                    HubTelemetry.Track("install_completed", "durationMs", HubTelemetry.StopTimer("install"));
                AutoInstaller.MarkCompleted();
                ShowComplete();
                return;
            }

            // Kick off the current step's install if we haven't already issued it — but pause
            // first so the UI paints the current state (e.g. "X installed, Y installing…")
            // before InstallOne freezes the editor for its resolve/recompile.
            if (AutoInstaller.IssuedIndex < next)
            {
                if (_issueAtTime <= 0)
                {
                    _issueAtTime = EditorApplication.timeSinceStartup + StepPauseSeconds;
                    return; // wait — the next poll ticks will keep the UI repainted during the pause
                }
                if (EditorApplication.timeSinceStartup < _issueAtTime)
                    return;

                _issueAtTime = 0;
                var id = _targetIds[next];
                var result = AutoInstaller.InstallOne(id);
                AutoInstaller.IssuedIndex = next;

                if (!result.Success)
                {
                    FailStep(id,
                        "Install failed:\n• " + string.Join("\n• ", result.Failures) +
                        "\n\nNo backup is kept — use 'git restore .' to inspect or revert.");
                    return;
                }

                // The manifest write is synchronous, so the dependency must be present NOW. If it
                // isn't, the plan added nothing for this component (e.g. it's detected as already
                // present, or the catalog has no source for the chosen install method) — the poll
                // would otherwise wait forever for a package that can never resolve. Surface it.
                if (!AutoInstaller.ManifestHasDependency(id))
                {
                    FailStep(id,
                        "The installer didn't add a manifest entry for this component, so it can't " +
                        "resolve.\n\nLikely causes:\n" +
                        "• it's detected as already present (leftover assets or types in the project), or\n" +
                        "• its catalog source for the selected install method (UPM / Git) is missing.\n\n" +
                        "See the Console for the planned actions, then install it from the Components tab.");
                    return;
                }

                HubTelemetry.StartTimer("component:" + id);

                // Arm the per-step watchdog: from here we're waiting on UPM to resolve the dependency.
                AutoInstaller.StepDeadline = EditorApplication.timeSinceStartup + StepTimeoutSeconds;
                Debug.Log($"{LogPrefix} Issued install for '{id}' — waiting up to {StepTimeoutSeconds}s to resolve.");
                // On success: manifest written + resolve queued. The reload re-enters this screen,
                // and the next poll sees this component resolved and moves on.
            }
            else
            {
                // Current step already issued — waiting for UPM to resolve it. Enforce the watchdog so
                // a failed/stuck resolve becomes an actionable prompt instead of an endless spinner.
                if (AutoInstallProgress.IsStepOverdue(EditorApplication.timeSinceStartup, AutoInstaller.StepDeadline))
                    WatchdogTimeout(_targetIds[next]);
            }
        }

        // A hard failure (apply failed, or nothing landed in the manifest): stop and surface the
        // inline failure panel. Deliberately does NOT call AutoInstaller.Clear() — IssuedIndex stays
        // pointed at the failed step so "Retry step" can re-issue just this one without losing the
        // steps already resolved earlier in the SessionState-persisted queue. Also persists the
        // failure (MarkFailed) so a domain reload before the user clicks Retry/Cancel re-enters via
        // EnterFailedMode instead of silently polling a watchdog that was never armed for this step.
        private void FailStep(string id, string detail)
        {
            HubTelemetry.Track("component_failed", new Dictionary<string, object>
            {
                ["package"] = id,
                ["screen"] = Id,
                ["durationMs"] = HubTelemetry.StopTimer("component:" + id),
                ["error"] = detail,
            });
            StopPoll();
            AutoInstaller.MarkFailed(id, detail);
            ShowFailure(id, detail);
        }

        // The step was issued and written to the manifest but hasn't resolved within the deadline.
        // It may still be downloading/cloning (slow network, Git LFS) or it may have failed — let the
        // user keep waiting or stop, rather than guessing and aborting a working-but-slow install.
        private void WatchdogTimeout(string id)
        {
            StopPoll();
            var keepWaiting = EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                $"{NameOf(id)} is taking longer than expected to install " +
                $"({Mathf.RoundToInt((float)StepTimeoutSeconds)}s).\n\n" +
                "It may still be downloading or cloning, or it may have failed. Check the Console for " +
                "Package Manager errors (git URL, version, network, or Git LFS).\n\n" +
                "Keep waiting, or stop and finish on the Components tab?",
                "Keep waiting", "Stop");
            HubTelemetry.Track("component_timeout", new Dictionary<string, object>
            {
                ["package"] = id,
                ["screen"] = Id,
                ["result"] = keepWaiting ? "keep_waiting" : "stop",
            });

            if (keepWaiting)
            {
                AutoInstaller.StepDeadline = EditorApplication.timeSinceStartup + StepTimeoutSeconds;
                StartPoll();
            }
            else
            {
                AutoInstaller.Clear();
                _router.GoTo("components");
            }
        }

        // A component counts as installed the first poll that sees it resolved after this run
        // issued it. The timer starts only when an install is really issued, so a component that
        // was already in the project (resolved, never issued, sitting before IssuedIndex in the
        // target list) has none and doesn't report.
        private void TrackResolved(HashSet<string> resolved)
        {
            var issued = AutoInstaller.IssuedIndex;
            for (var i = 0; i <= issued && i < _targetIds.Count; i++)
            {
                var id = _targetIds[i];
                if (!resolved.Contains(id) || !HubTelemetry.HasTimer("component:" + id)) continue;
                if (!HubTelemetry.Once("installed:" + id)) continue;
                HubTelemetry.Track("component_installed", new Dictionary<string, object>
                {
                    ["package"] = id,
                    ["durationMs"] = HubTelemetry.StopTimer("component:" + id),
                });
            }
        }

        private void Render(HashSet<string> resolved, int currentIndex)
        {
            if (_stepsHost == null) return;
            _stepsHost.Clear();

            var done = 0;
            for (var i = 0; i < _targetIds.Count; i++)
            {
                var id = _targetIds[i];
                string state;
                if (resolved.Contains(id)) { state = "done"; done++; }
                else if (i == currentIndex)  state = "active";
                else                          state = "wait";
                _stepsHost.Add(BuildStep(NameOf(id), state));
            }

            var pctVal = _targetIds.Count > 0 ? Mathf.RoundToInt(done * 100f / _targetIds.Count) : 0;
            if (_fill != null) _fill.style.width = Length.Percent(pctVal);
            if (_pct != null) _pct.text = pctVal + "%";
        }

        private string NameOf(string id) => _targetNames.TryGetValue(id, out var n) ? n : id;

        // ── Shared step row ──────────────────────────────────────────────────

        private static VisualElement BuildStep(string label, string state, string activeRight = null)
        {
            var row = new VisualElement();
            row.AddToClassList("cas-step");

            var icons = new VisualElement();
            icons.AddToClassList("cas-step__icongroup");
            if (state == "done")
            {
                var c = new Label("✓");
                c.AddToClassList("cas-checkmark");
                icons.Add(c);
            }
            else if (state == "active")
            {
                var sp = new VisualElement();
                sp.AddToClassList("cas-spinner");
                icons.Add(sp);
            }
            else
            {
                var d = new VisualElement();
                d.AddToClassList("cas-wait-dot");
                icons.Add(d);
            }

            var nameLabel = new Label(label);
            nameLabel.AddToClassList("cas-step__label");
            if (state == "wait") nameLabel.AddToClassList("cas-step__label--wait");
            row.Add(icons);
            row.Add(nameLabel);

            if (state == "active")
            {
                var r = new Label(string.IsNullOrEmpty(activeRight) ? "Installing…" : activeRight);
                r.AddToClassList("cas-step__right");
                row.Add(r);
            }
            else if (state == "wait")
            {
                var r = new Label("Waiting");
                r.AddToClassList("cas-step__right");
                r.AddToClassList("cas-step__right--wait");
                row.Add(r);
            }
            else if (state == "done")
            {
                var r = new Label("Installed");
                r.AddToClassList("cas-step__right");
                row.Add(r);
            }

            return row;
        }
    }
}
