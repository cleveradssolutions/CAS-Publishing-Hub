using System.Collections.Generic;
using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Common;
using CAS.Publishing.Installer.Migrator;
using CAS.Publishing.Installer.Scanner;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Ui
{
    /// <summary>
    /// Draws the action-oriented body of the installer window.
    ///
    /// Section order (fixed, skipped when empty except "Installed"):
    ///   1. To install   — NotInstalled (own + External)
    ///   2. To update    — UpmOutdated / UpmBelowMin
    ///   3. To migrate   — split-group blocks ("Replaces X (N): …") followed by 1:1
    ///                     LegacyUpm / LegacyAssets / Conflict + External ScopeMissing.
    ///                     Split groups only fire when at least one member has actual
    ///                     legacy state — fresh installs see split members in "To install".
    ///   4. To uninstall — UninstallScanResult with InstalledNeedsRemoval
    ///   5. Installed    — UpmCurrent / External UpmCurrent (collapsed by default)
    ///
    /// Two functions the installer serves:
    ///   (a) Install needed packages              → "To install"
    ///   (b) Migrate packages already in project  → "To update" / "To migrate" / "To uninstall"
    ///
    /// Precedence (which section a row belongs to) is independent of render order:
    /// an id that's part of an active split group is excluded from other sections via the
    /// splitGroupIds filter so it only renders inside the migrate section's split block.
    /// </summary>
    internal sealed class InstallerWindowReportView
    {
        // ── Colour palette (hex strings for rich text) ──────────────────────
        private const string ColourRed    = "#E05252";   // Conflict / UpmBelowMin
        private const string ColourYellow = "#D4A843";   // UpmOutdated / LegacyUpm / LegacyAssets / ScopeMissing
        private const string ColourGreen  = "#5BB85A";   // UpmCurrent
        private const string ColourGrey   = "#8A8A8A";   // NotInstalled / version text / disabled

        // ── Cached styles (created lazily to avoid domain-reload issues) ──────
        private GUIStyle _richLabel;
        private GUIStyle _boldLabel;
        private GUIStyle _subHeaderLabel;

        // ── Foldout state (keyed by section name) ────────────────────────────
        private bool _installFoldout  = true;
        private bool _updateFoldout   = true;
        private bool _migrateFoldout  = true;
        private bool _uninstallFoldout = true;
        private bool _currentFoldout  = false;   // Installed: collapsed by default

        // ── Selection ─────────────────────────────────────────────────────────
        private readonly List<string>      _selected;
        private readonly List<TargetEntry> _targets;

        public InstallerWindowReportView(List<string> selected, List<TargetEntry> targets)
        {
            _selected = selected;
            _targets  = targets;
        }

        private void Select(string id)
        {
            if (!_selected.Contains(id))
                _selected.Add(id);
        }

        private void Deselect(string id)
        {
            _selected.Remove(id);
        }

        private VersionTarget GetTarget(string id)
        {
            if (_targets == null) return VersionTarget.Recommended;
            foreach (var entry in _targets)
                if (entry.Id == id)
                    return entry.IsMin ? VersionTarget.Min : VersionTarget.Recommended;
            return VersionTarget.Recommended;
        }

        private void SetTarget(string id, VersionTarget target)
        {
            if (_targets == null) return;
            // Remove any existing entry for this id.
            for (var i = _targets.Count - 1; i >= 0; i--)
                if (_targets[i].Id == id)
                    _targets.RemoveAt(i);
            // Only store Min entries — absence means Recommended.
            if (target == VersionTarget.Min)
                _targets.Add(new TargetEntry { Id = id, IsMin = true });
        }

        // ── Public draw entry-point ───────────────────────────────────────────

        /// <summary>
        /// Draws the full body. Call from <see cref="InstallerWindow.OnGUI"/> inside
        /// a scroll view.
        /// </summary>
        public void Draw(ScanReport report, PackageCatalog catalog)
        {
            EnsureStyles();

            if (report == null)
            {
                EditorGUILayout.HelpBox("No scan report available. Click \"Run scan\" to generate one.",
                    MessageType.Info);
                return;
            }

            if (!string.IsNullOrEmpty(report.ManifestError))
            {
                EditorGUILayout.HelpBox(
                    $"Packages/manifest.json could not be read:\n{report.ManifestError}\n\n" +
                    "Fix the manifest, then click \"Run scan\". (States below are not shown because the " +
                    "manifest is unreadable — this is NOT an empty project.)",
                    MessageType.Error);
                return;
            }

            // ── Compute split-group membership set ────────────────────────────
            // An id belongs to the set when it is a member of any split group that has
            // at least one member in an ACTUALLY-DETECTED legacy state (LegacyUpm,
            // LegacyAssets, Conflict). NotInstalled does NOT count — fresh installs
            // shouldn't see "available migration" if the legacy package isn't really
            // in the project. All members of an active group (including UpmCurrent
            // for context, NotInstalled for siblings) render inside the split block
            // within "To migrate".
            var splitGroupIds = BuildActiveSplitGroupIds(report);

            DrawInstallSection(report, catalog, splitGroupIds);
            DrawUpdateSection(report, catalog, splitGroupIds);
            DrawMigrateSection(report, catalog, splitGroupIds);
            DrawUninstallSection(report);
            DrawCurrentSection(report, splitGroupIds);

            if (!HasAnyActionable(report))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(
                    "All CAS packages are up to date — nothing to install or migrate.",
                    MessageType.Info);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Section: To install
        // ═════════════════════════════════════════════════════════════════════

        private void DrawInstallSection(
            ScanReport report,
            PackageCatalog catalog,
            HashSet<string> splitGroupIds)
        {
            // CAS packages: NotInstalled, not in a split group.
            var pkgs = new List<(PackageScanResult result, PackageRecord record)>();
            if (report.Packages != null)
            {
                var catalogPkgById = BuildCatalogPackageMap(catalog);
                foreach (var r in report.Packages)
                {
                    if (r.State != PackageState.NotInstalled) continue;
                    if (splitGroupIds.Contains(r.Id)) continue;
                    if (HiddenComponents.IsHidden(r.Id)) continue;
                    catalogPkgById.TryGetValue(r.Id, out var rec);
                    pkgs.Add((r, rec));
                }
            }

            // External packages: NotInstalled (external ScopeMissing goes in Migrate).
            var exts = new List<ExternalScanResult>();
            if (report.External != null)
                foreach (var e in report.External)
                    if (e.State == ExternalState.NotInstalled && !HiddenComponents.IsHidden(e.Id))
                        exts.Add(e);

            if (pkgs.Count == 0 && exts.Count == 0) return;

            EditorGUILayout.Space(4);
            _installFoldout = EditorGUILayout.Foldout(
                _installFoldout, $"To install ({pkgs.Count + exts.Count})", true, EditorStyles.foldoutHeader);

            if (!_installFoldout) return;

            var extRecordById = BuildExternalRecordMap(catalog);
            foreach (var (result, record) in pkgs)
                DrawPackageRow(result, record, indent: 1);
            foreach (var ext in exts)
            {
                extRecordById.TryGetValue(ext.Id, out var rec);
                DrawExternalRow(ext, rec, indent: 1);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Section: To update
        // ═════════════════════════════════════════════════════════════════════

        private void DrawUpdateSection(ScanReport report, PackageCatalog catalog, HashSet<string> splitGroupIds)
        {
            var rows = new List<(PackageScanResult result, PackageRecord record)>();
            if (report.Packages != null)
            {
                var catalogPkgById = BuildCatalogPackageMap(catalog);
                foreach (var r in report.Packages)
                {
                    if (r.State != PackageState.UpmOutdated && r.State != PackageState.UpmBelowMin) continue;
                    if (splitGroupIds.Contains(r.Id)) continue;
                    if (HiddenComponents.IsHidden(r.Id)) continue;
                    catalogPkgById.TryGetValue(r.Id, out var rec);
                    rows.Add((r, rec));
                }
            }

            if (rows.Count == 0) return;

            EditorGUILayout.Space(4);
            _updateFoldout = EditorGUILayout.Foldout(
                _updateFoldout, $"To update ({rows.Count})", true, EditorStyles.foldoutHeader);

            if (!_updateFoldout) return;

            foreach (var (result, record) in rows)
                DrawPackageRow(result, record, indent: 1);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Section: To migrate
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Renders the unified migration section. Includes:
        ///   - Active split-group blocks (one sub-header per group, "Replaces X (N):"
        ///     followed by the group's member rows).
        ///   - 1:1 legacy migrations (LegacyUpm / LegacyAssets / Conflict) that are
        ///     NOT part of any active split group.
        ///   - External ScopeMissing entries (registry scope needs to be added).
        ///
        /// Empty section skips render entirely (per "only what's actionable" principle).
        /// </summary>
        private void DrawMigrateSection(
            ScanReport report,
            PackageCatalog catalog,
            HashSet<string> splitGroupIds)
        {
            // Active split groups: at least one member has actual legacy state.
            var activeSplitGroups = new List<MigrationGroup>();
            if (report.SplitGroups != null)
            {
                foreach (var group in report.SplitGroups)
                {
                    foreach (var pkgId in group.PackageIds)
                    {
                        if (splitGroupIds.Contains(pkgId))
                        {
                            activeSplitGroups.Add(group);
                            break;
                        }
                    }
                }
            }

            // 1:1 legacy migrations (excluded if already inside an active split group).
            var pkgs = new List<(PackageScanResult result, PackageRecord record)>();
            Dictionary<string, PackageRecord> catalogPkgById = null;
            if (report.Packages != null)
            {
                catalogPkgById = BuildCatalogPackageMap(catalog);
                foreach (var r in report.Packages)
                {
                    if (r.State != PackageState.LegacyUpm &&
                        r.State != PackageState.LegacyAssets &&
                        r.State != PackageState.Conflict)
                        continue;
                    if (splitGroupIds.Contains(r.Id)) continue;
                    if (HiddenComponents.IsHidden(r.Id)) continue;
                    catalogPkgById.TryGetValue(r.Id, out var rec);
                    pkgs.Add((r, rec));
                }
            }

            // External scope-missing — also a migration (registry config fix).
            var exts = new List<ExternalScanResult>();
            if (report.External != null)
                foreach (var e in report.External)
                    if (e.State == ExternalState.ScopeMissing && !HiddenComponents.IsHidden(e.Id))
                        exts.Add(e);

            // Header count: every split-group member row counts (incl. UpmCurrent siblings
            // shown for context), plus the 1:1 rows and externals.
            var splitRowCount = 0;
            foreach (var g in activeSplitGroups) splitRowCount += g.PackageIds.Count;
            var total = splitRowCount + pkgs.Count + exts.Count;
            if (total == 0) return;

            EditorGUILayout.Space(4);
            _migrateFoldout = EditorGUILayout.Foldout(
                _migrateFoldout, $"To migrate ({total})", true, EditorStyles.foldoutHeader);
            if (!_migrateFoldout) return;

            // ── Split-group blocks (with sub-header) ──────────────────────────
            if (activeSplitGroups.Count > 0)
            {
                var pkgById = BuildPackageResultMap(report);
                if (catalogPkgById == null) catalogPkgById = BuildCatalogPackageMap(catalog);

                foreach (var group in activeSplitGroups)
                {
                    EditorGUILayout.Space(4);
                    var savedIndent = EditorGUI.indentLevel;
                    EditorGUI.indentLevel = 0;
                    try
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(12);
                            EditorGUILayout.LabelField(
                                $"Replaces {group.LegacyId} ({group.PackageIds.Count} packages):",
                                _subHeaderLabel);
                        }
                    }
                    finally { EditorGUI.indentLevel = savedIndent; }

                    // Members that are actually selectable (UpmCurrent siblings are shown
                    // disabled and excluded). Toggling any one ticks/unticks them all.
                    var actionable = new List<string>();
                    foreach (var pkgId in group.PackageIds)
                        if (pkgById.TryGetValue(pkgId, out var r) && r.State != PackageState.UpmCurrent)
                            actionable.Add(pkgId);

                    foreach (var pkgId in group.PackageIds)
                    {
                        if (!pkgById.TryGetValue(pkgId, out var result)) continue;
                        catalogPkgById.TryGetValue(pkgId, out var record);

                        if (result.State == PackageState.UpmCurrent)
                            DrawSplitCurrentRow(result, indent: 2);
                        else
                            DrawPackageRow(result, record, indent: 2, linkedSiblings: actionable);
                    }
                }
            }

            // ── 1:1 legacy migrations + scope-missing externals ───────────────
            var extRecordById = BuildExternalRecordMap(catalog);
            foreach (var (result, record) in pkgs)
                DrawPackageRow(result, record, indent: 1);
            foreach (var ext in exts)
            {
                extRecordById.TryGetValue(ext.Id, out var rec);
                DrawExternalRow(ext, rec, indent: 1);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Section: To uninstall
        // ═════════════════════════════════════════════════════════════════════

        private void DrawUninstallSection(ScanReport report)
        {
            if (report.Uninstalls == null || report.Uninstalls.Count == 0)
                return;

            EditorGUILayout.Space(4);
            _uninstallFoldout = EditorGUILayout.Foldout(
                _uninstallFoldout,
                $"To uninstall ({report.Uninstalls.Count})",
                true,
                EditorStyles.foldoutHeader);

            if (!_uninstallFoldout) return;

            foreach (var uninstall in report.Uninstalls)
                DrawUninstallRow(uninstall, indent: 1);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Section: Up to date
        // ═════════════════════════════════════════════════════════════════════

        private void DrawCurrentSection(ScanReport report, HashSet<string> splitGroupIds)
        {
            var pkgs = new List<PackageScanResult>();
            if (report.Packages != null)
                foreach (var r in report.Packages)
                    if (r.State == PackageState.UpmCurrent && !splitGroupIds.Contains(r.Id) &&
                        !HiddenComponents.IsHidden(r.Id))
                        pkgs.Add(r);

            var exts = new List<ExternalScanResult>();
            if (report.External != null)
                foreach (var e in report.External)
                    if (e.State == ExternalState.UpmCurrent && !HiddenComponents.IsHidden(e.Id))
                        exts.Add(e);

            var total = pkgs.Count + exts.Count;

            EditorGUILayout.Space(4);
            _currentFoldout = EditorGUILayout.Foldout(
                _currentFoldout, $"Installed ({total})", true, EditorStyles.foldoutHeader);

            if (!_currentFoldout) return;

            foreach (var r in pkgs)
                DrawCurrentPackageRow(r, indent: 1);
            foreach (var e in exts)
                DrawCurrentExternalRow(e, indent: 1);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Row renderers
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Draws an actionable CAS package row with:
        ///   [checkbox+name]  [state]  [installed ver]  →  [Min/Rec toolbar]  [extra]
        ///
        /// EditorGUI.indentLevel is reset to 0 and replayed as GUILayout.Space to
        /// avoid IndentedRect collapsing the Toggle hit area to ~1 px (commit 5d30f1d).
        /// </summary>
        private void DrawPackageRow(PackageScanResult pkg, PackageRecord record, int indent,
            IReadOnlyList<string> linkedSiblings = null)
        {
            var savedIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            try
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(indent * 15);

                    // Checkbox + bold name (clickable via ToggleLeft).
                    var wasSelected = _selected.Contains(pkg.Id);
                    var nowSelected = EditorGUILayout.ToggleLeft(
                        new GUIContent(pkg.DisplayName), wasSelected, _boldLabel,
                        GUILayout.MinWidth(200));
                    if (nowSelected != wasSelected)
                    {
                        if (linkedSiblings != null && linkedSiblings.Count > 0)
                            SplitSelection.SetGroup(_selected, linkedSiblings, nowSelected);
                        else if (nowSelected) Select(pkg.Id);
                        else Deselect(pkg.Id);
                    }

                    // State badge.
                    EditorGUILayout.LabelField(StateLabel(pkg.State), _richLabel, GUILayout.MinWidth(100));

                    // Installed version (60px) — hidden for NotInstalled (state badge already says so).
                    if (!string.IsNullOrEmpty(pkg.DetectedVersion))
                    {
                        EditorGUILayout.LabelField(
                            $"<color={ColourGrey}>{pkg.DetectedVersion}</color>",
                            _richLabel, GUILayout.Width(60));
                    }
                    else
                    {
                        GUILayout.Space(60);
                    }

                    // Arrow separator.
                    EditorGUILayout.LabelField("→", GUILayout.Width(16));

                    // Target switch — Min / Rec segmented control (160px).
                    DrawTargetSwitch(pkg.Id, record, pkg.State);

                    // Extra: legacy path count.
                    if (pkg.DetectedLegacyPaths != null && pkg.DetectedLegacyPaths.Count > 0)
                    {
                        var legacyLabel = $"<color={ColourYellow}>{pkg.DetectedLegacyPaths.Count} legacy path(s)</color>";
                        EditorGUILayout.LabelField(legacyLabel, _richLabel, GUILayout.MinWidth(100));
                    }
                    else
                    {
                        GUILayout.FlexibleSpace();
                    }
                }
            }
            finally
            {
                EditorGUI.indentLevel = savedIndent;
            }
        }

        /// <summary>
        /// Draws an external package row with checkbox, state badge, installed version,
        /// and either a target switch (NotInstalled) or nothing (ScopeMissing hides switch
        /// per spec — only scope registration, no version action).
        /// </summary>
        private void DrawExternalRow(ExternalScanResult ext, ExternalRecord record, int indent)
        {
            var savedIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            try
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(indent * 15);

                    var wasSelected = _selected.Contains(ext.Id);
                    var nowSelected = EditorGUILayout.ToggleLeft(
                        new GUIContent(ext.DisplayName), wasSelected, _boldLabel,
                        GUILayout.MinWidth(200));
                    if (nowSelected != wasSelected)
                    {
                        if (nowSelected) Select(ext.Id);
                        else             Deselect(ext.Id);
                    }

                    EditorGUILayout.LabelField(ExternalStateLabel(ext.State), _richLabel, GUILayout.MinWidth(100));

                    if (!string.IsNullOrEmpty(ext.DetectedVersion))
                    {
                        EditorGUILayout.LabelField(
                            $"<color={ColourGrey}>{ext.DetectedVersion}</color>",
                            _richLabel, GUILayout.Width(60));
                    }
                    else
                    {
                        GUILayout.Space(60);
                    }

                    if (ext.State == ExternalState.NotInstalled)
                    {
                        EditorGUILayout.LabelField("→", GUILayout.Width(16));
                        DrawExternalTargetSwitch(ext.Id, record);
                    }
                    else
                    {
                        // ScopeMissing: no version action — hide switch.
                        GUILayout.FlexibleSpace();
                    }
                }
            }
            finally
            {
                EditorGUI.indentLevel = savedIndent;
            }
        }

        /// <summary>
        /// Draws an uninstall row — checkbox + legacy id + "remove (no replacement)" label.
        /// No target switch (removal is unambiguous).
        /// </summary>
        private void DrawUninstallRow(UninstallScanResult uninstall, int indent)
        {
            var savedIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            try
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(indent * 15);

                    var key         = uninstall.LegacyNpmId;
                    var wasSelected = _selected.Contains(key);
                    var nowSelected = EditorGUILayout.ToggleLeft(
                        new GUIContent(uninstall.LegacyNpmId), wasSelected, _boldLabel,
                        GUILayout.MinWidth(200));
                    if (nowSelected != wasSelected)
                    {
                        if (nowSelected) Select(key);
                        else             Deselect(key);
                    }

                    EditorGUILayout.LabelField(UninstallStateLabel(uninstall.State), _richLabel, GUILayout.MinWidth(100));

                    if (!string.IsNullOrEmpty(uninstall.DetectedVersion))
                    {
                        var verLabel = $"<color={ColourGrey}>{uninstall.DetectedVersion}</color>";
                        EditorGUILayout.LabelField(verLabel, _richLabel, GUILayout.Width(60));
                    }
                    else
                    {
                        GUILayout.Space(60);
                    }

                    EditorGUILayout.LabelField(
                        $"<color={ColourGrey}>remove (no replacement)</color>",
                        _richLabel, GUILayout.MinWidth(160));

                    GUILayout.FlexibleSpace();
                }
            }
            finally
            {
                EditorGUI.indentLevel = savedIndent;
            }
        }

        /// <summary>
        /// Draws a read-only "Up to date" CAS package row — checkbox disabled, no target switch.
        /// </summary>
        private void DrawCurrentPackageRow(PackageScanResult pkg, int indent)
        {
            var savedIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            try
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(indent * 15);

                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ToggleLeft(
                            new GUIContent(pkg.DisplayName), false, _boldLabel,
                            GUILayout.MinWidth(200));
                    }

                    EditorGUILayout.LabelField(StateLabel(pkg.State), _richLabel, GUILayout.MinWidth(100));

                    if (!string.IsNullOrEmpty(pkg.DetectedVersion))
                    {
                        var verLabel = $"<color={ColourGrey}>{pkg.DetectedVersion}</color>";
                        EditorGUILayout.LabelField(verLabel, _richLabel, GUILayout.Width(60));
                    }
                    else
                    {
                        GUILayout.Space(60);
                    }

                    EditorGUILayout.LabelField(
                        $"<color={ColourGrey}>no action needed</color>",
                        _richLabel);

                    GUILayout.FlexibleSpace();
                }
            }
            finally
            {
                EditorGUI.indentLevel = savedIndent;
            }
        }

        /// <summary>
        /// Draws a read-only "Up to date" external row — checkbox disabled, no target switch.
        /// </summary>
        private void DrawCurrentExternalRow(ExternalScanResult ext, int indent)
        {
            var savedIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            try
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(indent * 15);

                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ToggleLeft(
                            new GUIContent(ext.DisplayName), false, _boldLabel,
                            GUILayout.MinWidth(200));
                    }

                    EditorGUILayout.LabelField(ExternalStateLabel(ext.State), _richLabel, GUILayout.MinWidth(100));

                    if (!string.IsNullOrEmpty(ext.DetectedVersion))
                    {
                        var verLabel = $"<color={ColourGrey}>{ext.DetectedVersion}</color>";
                        EditorGUILayout.LabelField(verLabel, _richLabel, GUILayout.Width(60));
                    }
                    else
                    {
                        GUILayout.Space(60);
                    }

                    EditorGUILayout.LabelField(
                        $"<color={ColourGrey}>no action needed</color>",
                        _richLabel);

                    GUILayout.FlexibleSpace();
                }
            }
            finally
            {
                EditorGUI.indentLevel = savedIndent;
            }
        }

        /// <summary>
        /// Draws a read-only split-group member that is already UpmCurrent — disabled
        /// checkbox, "installed" badge, no target switch. Shown inline within the split
        /// group sub-header to give context for "what's already there vs. still missing".
        /// </summary>
        private void DrawSplitCurrentRow(PackageScanResult pkg, int indent)
        {
            var savedIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            try
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(indent * 15);

                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ToggleLeft(
                            new GUIContent(pkg.DisplayName), false, _boldLabel,
                            GUILayout.MinWidth(200));
                    }

                    EditorGUILayout.LabelField(
                        $"<color={ColourGreen}>✓ installed</color>", _richLabel, GUILayout.MinWidth(100));

                    GUILayout.FlexibleSpace();
                }
            }
            finally
            {
                EditorGUI.indentLevel = savedIndent;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Target switch (segmented control)
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Draws a two-option Toolbar segmented control for CAS packages:
        ///   [ Min X.Y.Z | Rec A.B.C ]
        /// Width 160px. When only one version is available both options show the same
        /// version (min == rec fallback). When no version is available, shows a grey dash.
        /// </summary>
        private void DrawTargetSwitch(string id, PackageRecord record, PackageState state)
        {
            if (record == null)
            {
                EditorGUILayout.LabelField($"<color={ColourGrey}>—</color>", _richLabel, GUILayout.Width(160));
                return;
            }

            var minVer = record.MinVersion ?? record.RecommendedVersion ?? "?";
            var recVer = record.RecommendedVersion ?? record.MinVersion ?? "?";

            var options = new[] { $"Min {minVer}", $"Rec {recVer}" };
            var current = GetTarget(id) == VersionTarget.Min ? 0 : 1;
            var chosen  = GUILayout.Toolbar(current, options, GUILayout.Width(160));
            if (chosen != current)
                SetTarget(id, chosen == 0 ? VersionTarget.Min : VersionTarget.Recommended);
        }

        /// <summary>
        /// Draws the target switch for an external package (NotInstalled only).
        /// </summary>
        private void DrawExternalTargetSwitch(string id, ExternalRecord record)
        {
            if (record == null)
            {
                EditorGUILayout.LabelField($"<color={ColourGrey}>—</color>", _richLabel, GUILayout.Width(160));
                return;
            }

            var minVer = record.MinVersion ?? record.RecommendedVersion ?? "?";
            var recVer = record.RecommendedVersion ?? record.MinVersion ?? "?";

            var options = new[] { $"Min {minVer}", $"Rec {recVer}" };
            var current = GetTarget(id) == VersionTarget.Min ? 0 : 1;
            var chosen  = GUILayout.Toolbar(current, options, GUILayout.Width(160));
            if (chosen != current)
                SetTarget(id, chosen == 0 ? VersionTarget.Min : VersionTarget.Recommended);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Split group computation
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns the set of package ids that belong to at least one ACTIVE split group.
        /// "Active" = at least one member has an actually-detected legacy state
        /// (<see cref="PackageState.LegacyUpm"/>, <see cref="PackageState.LegacyAssets"/>,
        /// or <see cref="PackageState.Conflict"/>). NotInstalled does NOT trigger the
        /// split view — a fresh project without the legacy package should see the
        /// would-be replacement members in "To install" as plain installs, not as a
        /// hypothetical migration. (Owner principle: don't show available migrations
        /// when there is nothing to migrate from.)
        ///
        /// When a group IS active, ALL members are added to the set — including any
        /// UpmCurrent members (already migrated; shown for context with a disabled
        /// row) and any NotInstalled siblings (missing replacements; the user needs
        /// to tick them too to avoid the partial-split warning at Apply time).
        /// </summary>
        private static HashSet<string> BuildActiveSplitGroupIds(ScanReport report)
        {
            var result = new HashSet<string>();

            if (report.SplitGroups == null || report.SplitGroups.Count == 0)
                return result;

            var pkgById = BuildPackageResultMap(report);

            foreach (var group in report.SplitGroups)
            {
                // Check whether at least one member has actually-detected legacy state.
                var hasLegacy = false;
                foreach (var pkgId in group.PackageIds)
                {
                    if (!pkgById.TryGetValue(pkgId, out var r)) continue;
                    if (r.State == PackageState.LegacyUpm   ||
                        r.State == PackageState.LegacyAssets ||
                        r.State == PackageState.Conflict)
                    {
                        hasLegacy = true;
                        break;
                    }
                }

                if (!hasLegacy) continue;

                // Mark all members — UpmCurrent for context, NotInstalled for
                // sibling-completion hint, Legacy* for the migration itself.
                foreach (var pkgId in group.PackageIds)
                    result.Add(pkgId);
            }

            return result;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Actionable predicate
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// True when the report contains at least one row the user can act on — anything that
        /// is not already UpmCurrent: a package to install/update/migrate, an external that's
        /// not current, or an uninstall. Drives the friendly "all up to date" empty state.
        /// </summary>
        internal static bool HasAnyActionable(ScanReport report)
        {
            if (report == null) return false;

            if (report.Packages != null)
                foreach (var p in report.Packages)
                    if (p != null && p.State != PackageState.UpmCurrent && !HiddenComponents.IsHidden(p.Id))
                        return true;

            if (report.External != null)
                foreach (var e in report.External)
                    if (e != null && e.State != ExternalState.UpmCurrent
                                  && e.State != ExternalState.InstalledLegacy
                                  && !HiddenComponents.IsHidden(e.Id))
                        return true;

            if (report.Uninstalls != null)
                foreach (var u in report.Uninstalls)
                    // The scanner only emits Uninstalls with InstalledNeedsRemoval; the state
                    // check is future-proofing, the null check is the load-bearing one.
                    if (u != null && u.State == UninstallState.InstalledNeedsRemoval)
                        return true;

            return false;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Lookup helpers
        // ═════════════════════════════════════════════════════════════════════

        private static Dictionary<string, PackageScanResult> BuildPackageResultMap(ScanReport report)
        {
            var map = new Dictionary<string, PackageScanResult>();
            if (report.Packages != null)
                foreach (var r in report.Packages)
                    if (r != null) map[r.Id] = r;
            return map;
        }

        private static Dictionary<string, PackageRecord> BuildCatalogPackageMap(PackageCatalog catalog)
        {
            var map = new Dictionary<string, PackageRecord>();
            if (catalog?.Packages != null)
                foreach (var r in catalog.Packages)
                    if (r != null && r.Id != null) map[r.Id] = r;
            return map;
        }

        private static Dictionary<string, ExternalRecord> BuildExternalRecordMap(PackageCatalog catalog)
        {
            var map = new Dictionary<string, ExternalRecord>();
            if (catalog?.External != null)
                foreach (var r in catalog.External)
                    if (r != null && r.Id != null) map[r.Id] = r;
            return map;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  State label helpers
        // ═════════════════════════════════════════════════════════════════════

        private string StateLabel(PackageState state)
        {
            switch (state)
            {
                case PackageState.UpmCurrent:
                    return $"<color={ColourGreen}>[Installed ✓]</color>";
                case PackageState.UpmOutdated:
                    return $"<color={ColourYellow}>[Outdated]</color>";
                case PackageState.UpmBelowMin:
                    return $"<color={ColourRed}>[Too old ⚠]</color>";
                case PackageState.LegacyUpm:
                    return $"<color={ColourYellow}>[Old package ID]</color>";
                case PackageState.LegacyAssets:
                    return $"<color={ColourYellow}>[Old files in Assets/]</color>";
                case PackageState.Conflict:
                    return $"<color={ColourRed}>[Mixed install ⚠]</color>";
                case PackageState.NotInstalled:
                default:
                    return $"<color={ColourGrey}>[Not installed]</color>";
            }
        }

        private string ExternalStateLabel(ExternalState state)
        {
            switch (state)
            {
                case ExternalState.UpmCurrent:
                    return $"<color={ColourGreen}>[Installed ✓]</color>";
                case ExternalState.ScopeMissing:
                    return $"<color={ColourYellow}>[Needs registry setup]</color>";
                case ExternalState.InstalledLegacy:
                    return $"<color={ColourGreen}>[Installed (legacy)]</color>";
                case ExternalState.NotInstalled:
                default:
                    return $"<color={ColourGrey}>[Not installed]</color>";
            }
        }

        private string UninstallStateLabel(UninstallState state)
        {
            switch (state)
            {
                case UninstallState.InstalledNeedsRemoval:
                    return $"<color={ColourYellow}>[To be removed]</color>";
                case UninstallState.NotInstalled:
                default:
                    return $"<color={ColourGrey}>[Not installed]</color>";
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Style initialisation
        // ═════════════════════════════════════════════════════════════════════

        private void EnsureStyles()
        {
            if (_richLabel != null) return;

            _richLabel = new GUIStyle(EditorStyles.label)
            {
                richText = true
            };

            _boldLabel = new GUIStyle(EditorStyles.label)
            {
                fontStyle = FontStyle.Bold,
                richText   = false
            };

            _subHeaderLabel = new GUIStyle(EditorStyles.label)
            {
                fontStyle = FontStyle.Italic,
                richText  = false
            };
        }
    }
}
