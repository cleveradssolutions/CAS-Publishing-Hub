using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Common;
using CAS.Publishing.Installer.Migrator;
using CAS.Publishing.Installer.Scanner;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Ui
{
    /// <summary>
    /// Per-package version target entry — stored as a Unity-serializable struct so it
    /// survives domain reloads as part of <see cref="InstallerWindow._targets"/>.
    /// Absence from the list means the default (<see cref="VersionTarget.Recommended"/>).
    /// </summary>
    [Serializable]
    internal struct TargetEntry
    {
        public string Id;

        /// <summary>
        /// True = user chose Minimum; false = Recommended (the default).
        /// We only store entries where IsMin=true to keep serialised state minimal.
        /// </summary>
        public bool IsMin;
    }

    /// <summary>
    /// Main installer editor window. Opens via menu or auto-pops up when the scan
    /// report hash differs from the last-shown hash stored in <see cref="EditorPrefs"/>.
    /// </summary>
    public sealed class InstallerWindow : EditorWindow
    {
        // ── Window title & minimum size ───────────────────────────────────────
        private const string WindowTitle = "CAS.AI Publishing Hub";
        private static readonly Vector2 MinSize = new Vector2(640, 400);

        // ── In-memory state ───────────────────────────────────────────────────
        private PackageCatalog         _catalog;
        private ScanReport             _report;
        private Vector2                _scrollPos;

        // _selected is [SerializeField] List<string> rather than HashSet so it
        // survives Unity domain reloads (HashSet is not Unity-serializable).
        // Membership checks are O(n) — fine at our scale (≤50 items).
        [SerializeField]
        private List<string> _selected = new List<string>();

        // Per-package version target. Parallel to _selected; absence means Recommended.
        // Only entries explicitly set to Min are stored — keeps serialised state minimal.
        [SerializeField]
        private List<TargetEntry> _targets = new List<TargetEntry>();

        // Apply result, serialized so it survives the domain reload that AssetDatabase.Refresh
        // triggers after a manifest write — otherwise the user gets no confirmation at all.
        [SerializeField] private string _resultMessage;
        [SerializeField] private int    _resultKind; // 0 = none, 1 = success, 2 = error

        // Manifest last-write ticks captured at scan time; if the file changes afterwards the
        // displayed report is stale (acting on it plans against out-of-date state).
        [SerializeField] private long _scannedManifestTicks;

        // ── Migrator ──────────────────────────────────────────────────────────
        private MigrationRunner _migrator;
        private bool            _busy;

        // ── Child view ────────────────────────────────────────────────────────
        private InstallerWindowReportView _reportView;

        // ── Cached styles ─────────────────────────────────────────────────────
        private GUIStyle _statusBarStyle;
        private bool     _stylesInitialised;

        // ═════════════════════════════════════════════════════════════════════
        //  Public API
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Opens or focuses the legacy IMGUI installer window. No longer in the menu — the
        /// UI-Toolkit Wizard (Assets/CleverAdsSolutions/Hub) is the product UI; kept as a fallback.
        /// </summary>
        public static void Open()
        {
            var window = GetWindow<InstallerWindow>(WindowTitle);
            window.minSize = MinSize;
            window.Show();
            if (window._report == null)
                window.RunScan();
        }

        /// <summary>
        /// Opens the window and stores the new hash only when
        /// <paramref name="report"/>'s hash differs from the stored one.
        /// Called by Bootstrap after a successful catalog load.
        /// </summary>
        public static void ShowIfReportChanged(ScanReport report)
        {
            if (report == null) return;

            var lastHash = ScanReportStore.GetLastShownHash();
            if (report.Hash == lastHash) return;

            ScanReportStore.SetLastShownHash(report.Hash);

            var window = GetWindow<InstallerWindow>(WindowTitle);
            window.minSize = MinSize;
            window.AcceptReport(report);
            window.Show();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  EditorWindow lifecycle
        // ═════════════════════════════════════════════════════════════════════

        private void OnEnable()
        {
            titleContent = new GUIContent(WindowTitle);
            _reportView  = new InstallerWindowReportView(_selected, _targets);
            _migrator    = new MigrationRunner();
        }

        private void OnGUI()
        {
            EnsureStyles();
            DrawToolbar();
            DrawBody();
            DrawStatusBar();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Toolbar
        // ═════════════════════════════════════════════════════════════════════

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            using (new EditorGUI.DisabledScope(_busy))
            {
                if (GUILayout.Button("Refresh catalog", EditorStyles.toolbarButton, GUILayout.Width(120)))
                    OnRefreshCatalog();

                if (GUILayout.Button("Run scan", EditorStyles.toolbarButton, GUILayout.Width(80)))
                    OnRunScan();

                var applyContent = new GUIContent(
                    "Apply selected",
                    "Migrate selected packages: add, remove, and register scoped registries as needed.");
                if (GUILayout.Button(applyContent, EditorStyles.toolbarButton, GUILayout.Width(110)))
                    OnApplySelected();

                GUILayout.FlexibleSpace();
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Body
        // ═════════════════════════════════════════════════════════════════════

        private void DrawResultBanner()
        {
            if (string.IsNullOrEmpty(_resultMessage)) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.HelpBox(_resultMessage, _resultKind == 2 ? MessageType.Error : MessageType.Info);
                if (GUILayout.Button("Dismiss", GUILayout.Width(80)))
                {
                    _resultMessage = null;
                    _resultKind = 0;
                }
            }
            EditorGUILayout.Space(4);
        }

        private static string ManifestPath()
        {
            var root = System.IO.Path.GetDirectoryName(Application.dataPath);
            return root == null ? null : System.IO.Path.Combine(root, "Packages", "manifest.json");
        }

        private static long ManifestTicks()
        {
            try
            {
                var p = ManifestPath();
                return p != null && System.IO.File.Exists(p)
                    ? System.IO.File.GetLastWriteTimeUtc(p).Ticks : 0;
            }
            catch { return 0; }
        }

        private void DrawStaleBanner()
        {
            if (_report == null) return;
            if (_scannedManifestTicks == 0) return;
            var current = ManifestTicks();
            if (current == 0 || current == _scannedManifestTicks) return;

            EditorGUILayout.HelpBox(
                "Packages/manifest.json changed since the last scan — this view may be out of date. " +
                "Click \"Run scan\" to refresh before applying.",
                MessageType.Warning);
            EditorGUILayout.Space(4);
        }

        private void DrawBody()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            EditorGUILayout.Space(4);
            DrawResultBanner();
            DrawStaleBanner();
            _reportView?.Draw(_report, _catalog);
            EditorGUILayout.Space(4);
            EditorGUILayout.EndScrollView();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Status bar
        // ═════════════════════════════════════════════════════════════════════

        private void DrawStatusBar()
        {
            var sep = new Color(0f, 0f, 0f, 0.25f);
            var rect = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(rect, sep);

            using (new EditorGUILayout.HorizontalScope())
            {
                var statusText = BuildStatusText();
                EditorGUILayout.LabelField(statusText, _statusBarStyle);
            }
        }

        private string BuildStatusText()
        {
            if (_report == null)
                return "No scan data.";

            var catalogVer = _report.CatalogVersion ?? "?";
            var scanTime   = _report.ScannedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var attention  = CountNeedingAttention();

            return $"Catalog v{catalogVer}  ·  Last scan: {scanTime}  ·  {attention} item(s) need attention";
        }

        private int CountNeedingAttention()
        {
            if (_report == null) return 0;

            var count = 0;

            if (_report.Packages != null)
                foreach (var p in _report.Packages)
                    if (p.State != PackageState.UpmCurrent && p.State != PackageState.NotInstalled
                                                            && !HiddenComponents.IsHidden(p.Id))
                        count++;

            if (_report.External != null)
                foreach (var e in _report.External)
                    if (e.State != ExternalState.UpmCurrent && e.State != ExternalState.NotInstalled
                                                           && e.State != ExternalState.InstalledLegacy
                                                           && !HiddenComponents.IsHidden(e.Id))
                        count++;

            if (_report.Uninstalls != null)
                foreach (var u in _report.Uninstalls)
                    if (u.State == UninstallState.InstalledNeedsRemoval)
                        count++;

            return count;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Toolbar button handlers
        // ═════════════════════════════════════════════════════════════════════

        private void OnRefreshCatalog()
        {
            const string logPrefix = "[CAS Hub]";

            CatalogUpdater.CheckRemoteLatestVersion(
                onSuccess: latest =>
                {
                    EnsureCatalog();
                    var current = _catalog?.CatalogVersion;

                    if (CatalogUpdater.IsNewer(latest, current))
                    {
                        Debug.Log($"{logPrefix} Newer catalog available: {latest} (installed: {current}). " +
                                  "Updating UPM dependency in background…");
                        CatalogUpdater.InstallVersion(latest);
                    }
                    else
                    {
                        Debug.Log($"{logPrefix} Catalog is up to date ({current}).");
                    }
                },
                onFailure: err =>
                {
                    Debug.LogWarning($"{logPrefix} Could not check registry for catalog updates: {err}");
                });
        }

        private void OnRunScan()
        {
            RunScan();
            if (_report != null)
                ScanReportStore.SetLastShownHash(_report.Hash);
        }

        private void OnApplySelected()
        {
            if (_busy) return;
            EnsureCatalog();

            if (_catalog == null)
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    "Cannot apply: catalog is not loaded. Run a scan first.", "OK");
                return;
            }

            if (_report == null)
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    "Cannot apply: no scan report. Run a scan first.", "OK");
                return;
            }

            var selectionAdapter = new HashSetSelectionAdapter(_selected, _targets);
            var plan = MigrationPlanner.Plan(_catalog, _report, selectionAdapter, InstallMethod.Upm, out var warnings);

            if (plan.Count == 0)
            {
                EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                    "Nothing to apply. Select one or more items that need action.", "OK");
                return;
            }

            // ── Partial-split confirm dialog ──────────────────────────────────
            foreach (var w in warnings)
            {
                if (!(w is PartialSplitWarning psw)) continue;

                var splitMsg = BuildPartialSplitMessage(psw);
                var continueAnyway = EditorUtility.DisplayDialog(
                    "CAS.AI Publishing Hub — Partial Split Migration",
                    splitMsg,
                    "Continue anyway",
                    "Cancel");

                if (!continueAnyway) return;
            }

            var summary = BuildPlanSummary(plan, warnings, selectionAdapter);
            var confirmed = EditorUtility.DisplayDialog(
                "CAS.AI Publishing Hub — Confirm Apply",
                summary,
                "Apply", "Cancel");

            if (!confirmed) return;

            _busy = true;
            Repaint();

            var result = _migrator.Apply(plan);

            _busy = false;

            if (result.Success)
            {
                _resultMessage = $"Applied {result.ExecutedCount} action(s) successfully.";
                _resultKind = 1;
            }
            else
            {
                _resultMessage = "Apply failed:\n• " + string.Join("\n• ", result.Failures) +
                    "\n\nNo backup is kept — use 'git status' / 'git restore .' to inspect and revert.";
                _resultKind = 2;
            }

            RunScan();
            Repaint();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Internal helpers
        // ═════════════════════════════════════════════════════════════════════

        private void RunScan()
        {
            EnsureCatalog();
            if (_catalog == null)
            {
                Debug.LogWarning("[CAS Hub] Cannot run scan: catalog not available.");
                return;
            }

            _report = ProjectScanner.Scan(_catalog);
            _scannedManifestTicks = ManifestTicks();
            Repaint();
        }

        private void EnsureCatalog()
        {
            if (_catalog != null) return;
            var load = CatalogLoader.Load();
            _catalog = load.Status == CatalogLoadStatus.Ok ? load.Catalog : null;
        }

        private void AcceptReport(ScanReport report)
        {
            _report = report;
            _scannedManifestTicks = ManifestTicks();
            EnsureCatalog();
            Repaint();
        }

        // ── Plan summary ──────────────────────────────────────────────────────

        private static string BuildPlanSummary(
            IReadOnlyList<MigrationAction> plan,
            IReadOnlyList<PlannerWarning> warnings,
            HashSetSelectionAdapter selectionAdapter)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Ready to apply {plan.Count} action(s):");
            sb.AppendLine();

            foreach (var a in plan)
            {
                switch (a)
                {
                    case AddPackage add:
                    {
                        var targetName = selectionAdapter.GetTarget(add.Id) == VersionTarget.Min ? "minimum" : "recommended";
                        sb.AppendLine($"  • Add package: {add.Id}@{add.Version} ({targetName})");
                        break;
                    }
                    case AddGitPackage git:
                        sb.AppendLine($"  • Add package (git): {git.Id} ({git.Spec})");
                        break;
                    case UpdatePackageVersion upd:
                    {
                        var targetName = selectionAdapter.GetTarget(upd.Id) == VersionTarget.Min ? "minimum" : "recommended";
                        sb.AppendLine($"  • Update package: {upd.Id} → {upd.Version} ({targetName})");
                        break;
                    }
                    case RemovePackage rem:
                        sb.AppendLine($"  • Remove package: {rem.Id}");
                        break;
                    case AddScopedRegistry reg:
                        sb.AppendLine($"  • Add scoped registry: {reg.Url} (scope: {reg.Scope})");
                        break;
                    case AddScopeToRegistry scope:
                        sb.AppendLine($"  • Add scope {scope.Scope} to registry {scope.Url}");
                        break;
                    case BackupAndDeletePath del:
                        sb.AppendLine($"  • Delete legacy path: Assets/{del.RelativePath}");
                        break;
                }
            }

            // Only show non-partial-split warnings (partial split is handled via separate dialog).
            var nonSplitWarnings = new List<PlannerWarning>();
            if (warnings != null)
                foreach (var w in warnings)
                    if (!(w is PartialSplitWarning))
                        nonSplitWarnings.Add(w);

            if (nonSplitWarnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Warnings ({nonSplitWarnings.Count}):");
                foreach (var w in nonSplitWarnings)
                    sb.AppendLine($"  ⚠ {w.Id}: {w.Message}");
            }

            sb.AppendLine();
            sb.Append(
                "These changes will be applied directly to manifest.json and Assets/.\n" +
                "Make sure you have a clean git state — use 'git restore .' to undo if anything goes wrong.");

            return sb.ToString();
        }

        private static string BuildPartialSplitMessage(PartialSplitWarning psw)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"⚠ Warning: split migration partial");
            sb.AppendLine();
            sb.AppendLine($"  {psw.LegacyId} is replaced by {psw.SelectedSiblings.Count + psw.UnselectedSiblings.Count} packages:");
            if (psw.SelectedSiblings.Count > 0)
                sb.AppendLine($"    Selected: {string.Join(", ", psw.SelectedSiblings)}");
            sb.AppendLine($"    NOT selected: {string.Join(", ", psw.UnselectedSiblings)}");
            sb.AppendLine();
            sb.Append($"Continuing will remove {psw.LegacyId} from manifest without installing all replacements.");
            return sb.ToString();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Style initialisation
        // ═════════════════════════════════════════════════════════════════════

        private void EnsureStyles()
        {
            if (_stylesInitialised) return;

            _statusBarStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                padding = new RectOffset(6, 6, 2, 2)
            };

            _stylesInitialised = true;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ISelectionSet adapter
        // ═════════════════════════════════════════════════════════════════════

        private sealed class HashSetSelectionAdapter : ISelectionSet
        {
            private readonly List<string>      _list;
            private readonly List<TargetEntry> _targets;

            public HashSetSelectionAdapter(List<string> list, List<TargetEntry> targets)
            {
                _list    = list;
                _targets = targets;
            }

            public bool IsSelected(string id) => _list != null && _list.Contains(id);

            public VersionTarget GetTarget(string id)
            {
                if (_targets == null) return VersionTarget.Recommended;
                foreach (var entry in _targets)
                    if (entry.Id == id)
                        return entry.IsMin ? VersionTarget.Min : VersionTarget.Recommended;
                return VersionTarget.Recommended;
            }
        }
    }
}
