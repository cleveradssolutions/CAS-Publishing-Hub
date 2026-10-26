using System.Collections.Generic;
using CAS.Publishing.Installer.Migrator;
using CAS.Publishing.Installer.Scanner;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Confirm window for a manifest plan whose removals break the game's own scripts. The native
    /// dialog cut the "Code to update afterwards" block short ("Co…"), so the one part the user had
    /// to act on was never seen (QA 87). Same look and modal, synchronous contract as
    /// <see cref="MigrateConfirmWindow"/>; the file list scrolls.
    /// </summary>
    internal sealed class PlanConfirmWindow : EditorWindow
    {
        private string _title;
        private string _okText;
        private List<string> _actions;
        private List<string> _warnings;
        private IReadOnlyList<RetiredCodeProbe.Hit> _references;
        private bool _result;

        public static bool Confirm(string title, IReadOnlyList<MigrationAction> plan,
            IReadOnlyList<PlannerWarning> warnings, IReadOnlyList<RetiredCodeProbe.Hit> references, string okText)
        {
            var w = CreateInstance<PlanConfirmWindow>();
            w._title = title;
            w._okText = string.IsNullOrEmpty(okText) ? "Apply" : okText;
            w._actions = new List<string>();
            if (plan != null) foreach (var a in plan) w._actions.Add(Describe(a));
            w._warnings = new List<string>();
            if (warnings != null) foreach (var x in warnings) w._warnings.Add(x.Message);
            w._references = references ?? new List<RetiredCodeProbe.Hit>();
            w._result = false;
            w.titleContent = new GUIContent("CAS.AI Publishing Hub");
            w.minSize = new Vector2(480, 340);
            w.maxSize = new Vector2(680, 800);
            w.ShowModalUtility(); // blocks until Close()
            return w._result;
        }

        internal static string Describe(MigrationAction a)
        {
            switch (a)
            {
                case AddPackage add:           return $"Add {add.Id}@{add.Version}";
                case AddGitPackage git:        return $"Add {git.Id} (git: {git.Spec})";
                case UpdatePackageVersion upd: return $"Update {upd.Id} → {upd.Version}";
                case RemovePackage rem:        return $"Remove {rem.Id}";
                case AddScopedRegistry reg:    return $"Register scope {reg.Scope} ({reg.Url})";
                case AddScopeToRegistry sc:    return $"Add scope {sc.Scope} to {sc.Url}";
                case BackupAndDeletePath del:  return $"Delete Assets/{del.RelativePath}";
                default:                       return a?.GetType().Name ?? "";
            }
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            var theme = WizardAssets.LoadStyle("theme");
            if (theme != null) root.styleSheets.Add(theme);
            root.AddToClassList("cas-window");

            var body = new VisualElement();
            body.AddToClassList("cas-body");
            root.Add(body);

            var title = new Label(_title);
            title.AddToClassList("cas-h2");
            title.style.whiteSpace = WhiteSpace.Normal;
            body.Add(title);

            if (_actions.Count > 0)
                body.Add(MigrateConfirmWindow.Section("Changes to Packages/manifest.json", _actions, "cas-dot--green", scroll: false));

            if (_warnings.Count > 0)
                body.Add(MigrateConfirmWindow.Section("Warnings", _warnings, "cas-dot--yellow", scroll: false));

            foreach (var hit in _references)
            {
                body.Add(MigrateConfirmWindow.WarnBanner(
                    $"{hit.Files.Count} script(s) still reference {hit.PackageId} and will stop compiling once " +
                    "it is removed. Replace that code with CAS — it initializes Tenjin itself, purchases go " +
                    "through CASHub.ReportPurchase, and the SDK key goes to " + TenjinKeySettings.KeyHome + ".", marginTop: 12));
                body.Add(MigrateConfirmWindow.Section(null, hit.Files, "cas-dot--grey", scroll: true));
            }

            var note = new Label("This writes directly to Packages/manifest.json. Keep a clean git state — " +
                                 "'git restore .' undoes it.");
            note.AddToClassList("cas-muted");
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.marginTop = 12;
            body.Add(note);

            var footer = new VisualElement();
            footer.AddToClassList("cas-row");
            footer.style.marginTop = 16;
            footer.Add(MigrateConfirmWindow.Spacer());

            var cancel = new Button(() => { _result = false; Close(); }) { text = "Cancel" };
            cancel.AddToClassList("cas-btn");
            cancel.AddToClassList("cas-btn--ghost");
            cancel.style.marginRight = 8;
            footer.Add(cancel);

            var ok = new Button(() => { _result = true; Close(); }) { text = _okText };
            ok.AddToClassList("cas-btn");
            ok.AddToClassList("cas-btn--warn");
            footer.Add(ok);

            body.Add(footer);
        }
    }
}
