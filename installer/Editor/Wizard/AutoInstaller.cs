using System.Collections.Generic;
using System.Text;
using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Common;
using CAS.Publishing.Installer.Migrator;
using CAS.Publishing.Installer.Scanner;
using CAS.Publishing.Installer.Telemetry;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// "Make everything for me" — installs the default components ONE AT A TIME (step by step),
    /// driven by the Progress screen. Each <see cref="InstallOne"/> writes the manifest and
    /// triggers a UPM resolve + domain reload; after the reload the Progress driver re-evaluates
    /// what's resolved and kicks off the next component, until all are done → Done.
    ///
    /// State lives in SessionState so it survives those reloads:
    ///   • Active        — an auto-install run is in progress.
    ///   • IssuedIndex   — highest component index whose install has been issued (so the driver
    ///                     doesn't re-issue the same install while it's still resolving).
    /// </summary>
    internal static class AutoInstaller
    {
        private const string ActiveKey = "CAS.Publishing.Installer.Wizard.AutoInstallActive";
        private const string IssuedKey = "CAS.Publishing.Installer.Wizard.AutoInstallIssuedIndex";
        private const string DeadlineKey = "CAS.Publishing.Installer.Wizard.AutoInstallStepDeadline";
        private const string ResumeKey = "CAS.Publishing.Installer.Wizard.ResumeRecommendedAfterCatalog";
        // Persisted "phase" flags so ProgressScreen can correctly redraw a failed or completed run
        // after an UNRELATED domain reload (e.g. background asset import), instead of falling back to
        // the "nothing to show" redirect (completed) or silently sitting on a poll that will never
        // fire (failed — no watchdog is armed for a step that failed synchronously, before ever
        // reaching UPM). Both are
        // reset by Clear(), which now runs at the top of StartAll() too so a fresh run never inherits
        // stale phase state from a previous one.
        private const string CompletedKey = "CAS.Publishing.Installer.Wizard.AutoInstallCompleted";
        private const string FailedIdKey = "CAS.Publishing.Installer.Wizard.AutoInstallFailedId";
        private const string FailedDetailKey = "CAS.Publishing.Installer.Wizard.AutoInstallFailedDetail";

        private const string LogPrefix = "[CAS Hub]";

        public static bool IsActive => SessionState.GetBool(ActiveKey, false);

        /// <summary>
        /// True (consuming the flag) when a recommended-install click hit a not-yet-installed catalog
        /// and armed a resume. The wizard consumes this on its next open once the catalog is present,
        /// to continue the recommended install automatically — so the user needn't click again after
        /// Unity finishes installing the metadata catalog. Returns false (and consumes nothing extra)
        /// when no resume is armed.
        /// </summary>
        public static bool ConsumeResume()
        {
            if (!SessionState.GetBool(ResumeKey, false)) return false;
            SessionState.SetBool(ResumeKey, false);
            return true;
        }

        public static int IssuedIndex
        {
            get => SessionState.GetInt(IssuedKey, -1);
            set => SessionState.SetInt(IssuedKey, value);
        }

        /// <summary>
        /// Wall-clock deadline (<see cref="EditorApplication.timeSinceStartup"/>) by which the
        /// currently-issued step must resolve, or the Progress watchdog surfaces a stall. 0 = unarmed.
        /// Stored in SessionState so it survives the per-step domain reload. Float precision is ample
        /// for a seconds value over one editor session.
        /// </summary>
        public static double StepDeadline
        {
            get => SessionState.GetFloat(DeadlineKey, 0f);
            set => SessionState.SetFloat(DeadlineKey, (float)value);
        }

        public static void Clear()
        {
            SessionState.SetBool(ActiveKey, false);
            SessionState.SetInt(IssuedKey, -1);
            SessionState.SetFloat(DeadlineKey, 0f);
            SessionState.SetBool(CompletedKey, false);
            ClearFailure();
        }

        /// <summary>True once <see cref="MarkCompleted"/> has run for the current batch and hasn't
        /// been cleared yet (by Cancel/Continue, or a fresh <see cref="StartAll"/>). Checked FIRST in
        /// <c>ProgressScreen.OnEnter</c> — independent of <see cref="IsActive"/> — so a domain reload
        /// that lands back on the Progress screen after the whole queue finished still shows the
        /// completion panel instead of falling through to the "nothing to show" redirect.</summary>
        public static bool IsCompleted => SessionState.GetBool(CompletedKey, false);

        /// <summary>Marks the batch as finished. Deliberately does NOT also call <see cref="Clear"/> —
        /// that's deferred to the completion screen's Cancel/Continue so the completed state survives
        /// any reload that happens before the user acts on it.</summary>
        public static void MarkCompleted() => SessionState.SetBool(CompletedKey, true);

        /// <summary>Persists which step failed and why, so the failure can be redrawn after a domain
        /// reload (see <see cref="TryGetFailure"/>). A step that fails synchronously (apply failed, or
        /// nothing landed in the manifest) never arms <see cref="StepDeadline"/>, so the watchdog can't
        /// be relied on to detect this case after a reload — this persisted pair is the source of truth
        /// instead.</summary>
        public static void MarkFailed(string id, string detail)
        {
            SessionState.SetString(FailedIdKey, id ?? string.Empty);
            SessionState.SetString(FailedDetailKey, detail ?? string.Empty);
        }

        /// <summary>Clears a persisted failure (on Retry, or via <see cref="Clear"/>) without touching
        /// the rest of the run's state.</summary>
        public static void ClearFailure()
        {
            SessionState.EraseString(FailedIdKey);
            SessionState.EraseString(FailedDetailKey);
        }

        /// <summary>True (with the failed step id/detail) when a step is currently in the persisted
        /// failed state.</summary>
        public static bool TryGetFailure(out string id, out string detail)
        {
            id = SessionState.GetString(FailedIdKey, string.Empty);
            detail = SessionState.GetString(FailedDetailKey, string.Empty);
            return !string.IsNullOrEmpty(id);
        }

        /// <summary>
        /// True when <paramref name="id"/> is currently a dependency in <c>Packages/manifest.json</c>
        /// (case-insensitive). The driver uses this to confirm an <see cref="InstallOne"/> actually
        /// wrote the component into the manifest — a plan that adds nothing would otherwise leave the
        /// progress poll waiting forever for a package that can never resolve.
        /// </summary>
        public static bool ManifestHasDependency(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var data = ManifestProbe.Read();
            if (!data.Readable || data.Dependencies == null) return false;
            foreach (var kv in data.Dependencies)
                if (string.Equals(kv.Key, id, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>Selects a fixed id set at the recommended version target.</summary>
        private sealed class IdSetSelection : ISelectionSet
        {
            private readonly HashSet<string> _ids;
            public IdSetSelection(IEnumerable<string> ids) { _ids = new HashSet<string>(ids); }
            public bool IsSelected(string id) => _ids.Contains(id);
            public VersionTarget GetTarget(string id) => VersionTarget.Recommended;
        }

        /// <summary>
        /// Confirms, then begins the step-by-step run: marks active and routes to Progress, whose
        /// driver performs the installs one component at a time. When everything is already
        /// installed, routes straight to Done. No installs happen here — the Progress driver owns
        /// the sequence so it can survive the per-step domain reloads.
        /// </summary>
        public static void StartAll(WizardRouter router)
        {
            // Automatic integration should yield a buildable project — ensure the Android build
            // templates exist before EDM4U resolves the installed packages' dependencies.
            AndroidBuildFix.Ensure();

            // The async metadata install may have finished (and its reload run) since the catalog was
            // last read this session — drop the session cache so we don't act on a stale NotInstalled.
            CatalogLoader.InvalidateCache();
            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok)
            {
                if (load.Status == CatalogLoadStatus.NotInstalled)
                {
                    // First run — especially a git-installed client — can reach here before the metadata
                    // catalog has finished its asynchronous install + domain reload (the wizard kicks the
                    // install off on open, but it isn't resolvable until Unity reloads). (Re-)trigger the
                    // install now and ARM a resume: when the catalog becomes present the wizard continues
                    // this recommended install automatically (ConsumeResume in CreateGUI), so the user
                    // doesn't have to click again.
                    SessionState.SetBool(ResumeKey, true);
                    CAS.Publishing.Installer.Bootstrap.EnsureMetadata();
                    EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                        "The package catalog is installing. Unity will reload when it's ready and the " +
                        "installer will continue automatically — no need to click again.", "OK");
                }
                else // Unreadable
                {
                    EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                        "The package catalog is installed but couldn't be read:\n" +
                        (load.Error ?? "unknown error") +
                        "\n\nReinstall " + CAS.Publishing.Installer.Common.EditionInfo.MetadataPackageId + ".", "OK");
                }
                return;
            }

            var report = ProjectScanner.Scan(load.Catalog);
            RegistryVersions.EnsureChecked(load.Catalog);
            var retired = RetiredIds(load.Catalog, report);
            var plan = MigrationPlanner.Plan(
                load.Catalog, report, new IdSetSelection(SelectionFor(retired)), InstallMethod.Upm, out var warnings);

            // Manual (Assets) copies of retired packages — the Tenjin plugin in Assets/Tenjin and the
            // like. They go first, behind the git guard, before any manifest write.
            var manualCopies = ComponentStatusProvider.ManualRetiredCopies(load.Catalog);
            var deletes = new List<MigrationAction>();
            foreach (var copy in manualCopies)
                foreach (var path in copy.Paths)
                    deletes.Add(new BackupAndDeletePath(path));

            if (plan.Count == 0 && deletes.Count == 0)
            {
                Clear();
                // Nothing to install does not mean everything is configured: hand off to the
                // configuration phase, which decides whether any steps are open and lands on Done
                // when none are. IntroDone is still set by DoneScreen.OnEnter, or the stepper would
                // vanish from the very screen meant to show it.
                ConfigPhase.Enter(router);
                return;
            }

            // Retired packages go in one manifest write up front; the Progress driver below only
            // knows how to install components one by one.
            var removals = new List<MigrationAction>();
            var installs = new List<MigrationAction>();
            foreach (var a in plan)
                (a is RemovePackage rem && retired.Contains(rem.Id) ? removals : installs).Add(a);

            var shown = new List<MigrationAction>(deletes);
            shown.AddRange(plan);

            var references = WizardActions.ReferencesFor(load.Catalog, removals, manualCopies);
            if (!WizardActions.ConfirmPlan(
                    installs.Count > 0 ? "Install the default components?" : "Remove the retired packages?",
                    BuildSummary(shown, warnings, installs.Count > 0, references), shown, warnings, references,
                    installs.Count > 0 ? "Install all" : "Remove"))
            {
                HubTelemetry.Track("install_cancelled", "screen", "ready");
                return; // cancelled → stay on the first screen; intro not marked done
            }

            HubTelemetry.Track("install_started", new Dictionary<string, object>
            {
                ["actions"] = shown.Count,
                ["warnings"] = warnings?.Count ?? 0,
            });
            HubTelemetry.StartTimer("install");

            foreach (var copy in manualCopies)
                if (!WizardActions.DeleteGuarded(copy.Id, copy.Paths, "Packages/manifest.json was NOT changed."))
                    return; // the dialog said why; stay on Ready

            if (removals.Count > 0)
            {
                var removed = new MigrationRunner().Apply(removals);
                if (!removed.Success)
                {
                    EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                        "Couldn't remove the retired packages:\n• " + string.Join("\n• ", removed.Failures) +
                        "\n\nNo backup is kept — use 'git status' / 'git restore .' to inspect or revert.", "OK");
                    return;
                }
                Debug.Log($"{LogPrefix} Removed {removals.Count} retired package(s). Unity will resolve packages now.");
                var note = RetiredCodeProbe.Describe(references);
                if (note != null) Debug.LogWarning($"{LogPrefix} {note}");
            }

            if (installs.Count == 0)
            {
                Clear();
                ConfigPhase.Enter(router);
                return;
            }

            // IntroDone is deferred to DoneScreen.OnEnter (see comment above) — committing to install
            // here must NOT flip it yet, or the stepper disappears for the rest of the flow
            // (Progress → Setup/Configure → Done) and Cancel-from-Progress would strand Ready tab-less.
            Clear(); // reset any stale Completed/Failed/IssuedIndex from a previous run before starting fresh
            SessionState.SetBool(ActiveKey, true);
            IssuedIndex = -1;
            router.GoTo("progress");
        }

        /// <summary>
        /// Installs/updates a single component (no dialog — the batch was confirmed in StartAll).
        /// Writes the manifest + triggers resolve/reload.
        /// </summary>
        public static ApplyResult InstallOne(string id)
        {
            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok)
                return new ApplyResult(false, 0, new[] { "Catalog unavailable." });

            var report = ProjectScanner.Scan(load.Catalog);
            RegistryVersions.EnsureChecked(load.Catalog);
            var plan = MigrationPlanner.Plan(load.Catalog, report, new IdSetSelection(new[] { id }), InstallMethod.Upm, out _);

            // Diagnostic: a healthy install for a not-yet-present component must produce at least one
            // Add/AddGit action. An empty (or registry-only) plan here is the documented cause of the
            // auto-install "spinner forever" — the manifest never gains the dependency, so the poll
            // waits on a package that can never resolve. Log the plan so a stall is explainable.
            Debug.Log($"{LogPrefix} InstallOne('{id}'): plan has {plan.Count} action(s) — " +
                      DescribePlan(plan));

            var result = new MigrationRunner().Apply(plan);
            if (!result.Success)
                Debug.LogWarning($"{LogPrefix} InstallOne('{id}') apply failed: " +
                                 string.Join("; ", result.Failures));
            return result;
        }

        private static string DescribePlan(IReadOnlyList<MigrationAction> plan)
        {
            if (plan == null || plan.Count == 0) return "(empty)";
            var sb = new StringBuilder();
            for (var i = 0; i < plan.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                switch (plan[i])
                {
                    case AddPackage add:           sb.Append($"Add {add.Id}@{add.Version}"); break;
                    case AddGitPackage git:        sb.Append($"AddGit {git.Id} ({git.Spec})"); break;
                    case UpdatePackageVersion upd: sb.Append($"Update {upd.Id}->{upd.Version}"); break;
                    case RemovePackage rem:        sb.Append($"Remove {rem.Id}"); break;
                    case AddScopedRegistry reg:    sb.Append($"Registry {reg.Scope}"); break;
                    case AddScopeToRegistry sc:    sb.Append($"Scope {sc.Scope}"); break;
                    case BackupAndDeletePath del:  sb.Append($"Delete {del.RelativePath}"); break;
                    default:                       sb.Append(plan[i].GetType().Name); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Ids of the retired packages (catalog <c>uninstall</c> rules) still in the manifest.</summary>
        internal static HashSet<string> RetiredIds(PackageCatalog catalog, ScanReport report)
        {
            var ids = new HashSet<string>();
            foreach (var r in ComponentStatusProvider.RetiredFrom(catalog, report)) ids.Add(r.Id);
            return ids;
        }

        /// <summary>The recommended install's selection: the default components plus the retired packages to drop.</summary>
        internal static List<string> SelectionFor(IEnumerable<string> retired)
        {
            var ids = new List<string>(ComponentStatusProvider.DefaultIds);
            if (retired != null) ids.AddRange(retired);
            return ids;
        }

        private static string BuildSummary(IReadOnlyList<MigrationAction> plan, IReadOnlyList<PlannerWarning> warnings,
            bool installs, IReadOnlyList<RetiredCodeProbe.Hit> references)
        {
            var sb = new StringBuilder();
            if (installs)
            {
                sb.AppendLine("Install all default components (CAS SDK, Firebase Analytics)?");
                sb.AppendLine("They will be installed one at a time.");
            }
            else
            {
                sb.AppendLine("Remove the retired packages?");
                sb.AppendLine("CAS does their job now, so the project no longer needs them.");
            }
            sb.AppendLine();

            foreach (var a in plan)
            {
                switch (a)
                {
                    case AddPackage add:           sb.AppendLine($"  • Add {add.Id}@{add.Version}"); break;
                    case AddGitPackage git:        sb.AppendLine($"  • Add {git.Id} (git: {git.Spec})"); break;
                    case UpdatePackageVersion upd: sb.AppendLine($"  • Update {upd.Id} → {upd.Version}"); break;
                    case RemovePackage rem:        sb.AppendLine($"  • Remove {rem.Id}"); break;
                    case AddScopedRegistry reg:    sb.AppendLine($"  • Register scope {reg.Scope} ({reg.Url})"); break;
                    case AddScopeToRegistry sc:    sb.AppendLine($"  • Add scope {sc.Scope} to {sc.Url}"); break;
                    case BackupAndDeletePath del:  sb.AppendLine($"  • Delete Assets/{del.RelativePath}"); break;
                }
            }

            if (warnings != null && warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Warnings:");
                foreach (var w in warnings)
                    sb.AppendLine($"  ⚠ {w.Message}");
            }

            var note = RetiredCodeProbe.Describe(references);
            if (note != null)
            {
                sb.AppendLine();
                sb.AppendLine("⚠ " + note);
            }

            sb.AppendLine();
            sb.Append("Writes directly to Packages/manifest.json. " +
                      "Make sure you have a clean git state — use 'git restore .' to undo if needed.");
            return sb.ToString();
        }
    }
}
