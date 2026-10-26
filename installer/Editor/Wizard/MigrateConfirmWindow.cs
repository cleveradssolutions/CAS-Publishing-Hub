using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Custom modal confirm for "Migrate to UPM" — replaces the cramped native text dialog with a
    /// readable, sectioned UI Toolkit window styled like the wizard (theme.uss). Shows the install set,
    /// what gets deleted, an optional downgrade warning, and a SCROLLABLE list of shared-folder leftovers
    /// to remove by hand. Modal + synchronous: <see cref="Confirm"/> blocks via ShowModalUtility and
    /// returns the user's choice, so callers keep their simple <c>if (confirmed) …</c> flow.
    /// </summary>
    internal sealed class MigrateConfirmWindow : EditorWindow
    {
        private string _displayName;
        private string _downgrade;          // null when not a downgrade
        private List<string> _installs;     // "id@version"
        private List<string> _deletes;      // Assets-relative roots
        private List<string> _shared;       // Assets-relative files (shared folder, manual cleanup)
        private bool _result;

        /// <summary>Shows the modal and returns true when the user confirms. Never throws.</summary>
        public static bool Confirm(string displayName, string downgrade,
            List<string> installs, List<string> deletes, List<string> shared)
        {
            var w = CreateInstance<MigrateConfirmWindow>();
            w._displayName = string.IsNullOrEmpty(displayName) ? "this component" : displayName;
            w._downgrade   = string.IsNullOrEmpty(downgrade) ? null : downgrade;
            w._installs    = installs ?? new List<string>();
            w._deletes     = deletes  ?? new List<string>();
            w._shared      = shared   ?? new List<string>();
            w._result      = false;
            w.titleContent = new GUIContent("CAS.AI Publishing Hub");
            w.minSize = new Vector2(460, 320);
            w.maxSize = new Vector2(620, 760);
            w.ShowModalUtility(); // blocks until Close()
            return w._result;
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

            var title = new Label($"Migrate {_displayName} to UPM");
            title.AddToClassList("cas-h2");
            body.Add(title);

            var sub = new Label("Move the manual (Assets) install to the registry-managed UPM package.");
            sub.AddToClassList("cas-sub");
            body.Add(sub);

            // Downgrade warning (prominent, top).
            if (_downgrade != null)
                body.Add(WarnBanner(_downgrade, marginTop: 12));

            if (_installs.Count > 0)
                body.Add(Section("Install via UPM", _installs, "cas-dot--green", scroll: false));

            if (_deletes.Count > 0)
            {
                var deletesDisplay = new List<string>(_deletes.Count);
                foreach (var d in _deletes) deletesDisplay.Add("Assets/" + d);
                body.Add(Section("Delete the manual copy", deletesDisplay, "cas-dot--yellow", scroll: false));
            }

            // Shared-folder leftovers — never auto-deleted; potentially long → scrollable.
            if (_shared.Count > 0)
            {
                body.Add(WarnBanner("Shared folder (Assets/Plugins) — NOT deleted automatically. " +
                                    "Remove these by hand after migrating to avoid conflicts:", marginTop: 12));
                var sharedDisplay = new List<string>(_shared.Count);
                foreach (var f in _shared) sharedDisplay.Add("Assets/" + f);
                body.Add(Section(null, sharedDisplay, "cas-dot--grey", scroll: true));
            }

            var note = new Label("Files are removed via git — keep a clean/committed state to be recoverable " +
                                 "(use 'git restore .' to undo).");
            note.AddToClassList("cas-muted");
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.marginTop = 12;
            body.Add(note);

            // Footer buttons.
            var footer = new VisualElement();
            footer.AddToClassList("cas-row");
            footer.style.marginTop = 16;
            footer.Add(Spacer());

            var cancel = new Button(() => { _result = false; Close(); }) { text = "Cancel" };
            cancel.AddToClassList("cas-btn");
            cancel.AddToClassList("cas-btn--ghost");
            cancel.style.marginRight = 8;
            footer.Add(cancel);

            var ok = new Button(() => { _result = true; Close(); }) { text = "Migrate" };
            ok.AddToClassList("cas-btn");
            // Warn-coloured primary when it's a downgrade, so the risky action reads as risky.
            ok.AddToClassList(_downgrade != null ? "cas-btn--warn" : "cas-btn--primary");
            footer.Add(ok);

            body.Add(footer);
        }

        // ── builders ──────────────────────────────────────────────────────────

        internal static VisualElement Spacer()
        {
            var s = new VisualElement();
            s.AddToClassList("cas-spacer");
            return s;
        }

        internal static VisualElement WarnBanner(string text, float marginTop)
        {
            var banner = new VisualElement();
            banner.AddToClassList("cas-warning");
            banner.style.marginTop = marginTop;

            var icon = new Label("⚠"); // ⚠
            icon.AddToClassList("cas-warning__icon");
            banner.Add(icon);

            var txt = new Label(text);
            txt.AddToClassList("cas-warning__text");
            banner.Add(txt);
            return banner;
        }

        internal static VisualElement Section(string titleText, List<string> items, string dotClass, bool scroll)
        {
            var c = new VisualElement();
            c.style.marginTop = 12;

            if (!string.IsNullOrEmpty(titleText))
            {
                var t = new Label(titleText);
                t.AddToClassList("cas-section-title");
                c.Add(t);
            }

            VisualElement container = c;
            if (scroll)
            {
                var sv = new ScrollView();
                sv.style.maxHeight = 150;
                sv.style.marginTop = 4;
                c.Add(sv);
                container = sv.contentContainer;
            }

            foreach (var it in items)
            {
                var row = new VisualElement();
                row.AddToClassList("cas-row");
                row.style.marginBottom = 2;

                var dot = new VisualElement();
                dot.AddToClassList("cas-dot");
                dot.AddToClassList("cas-dot--sm");
                dot.AddToClassList(dotClass);
                dot.style.marginRight = 8;
                row.Add(dot);

                var lbl = new Label(it);
                lbl.AddToClassList("cas-label");
                lbl.style.whiteSpace = WhiteSpace.Normal;
                lbl.style.flexGrow = 1;
                row.Add(lbl);

                container.Add(row);
            }
            return c;
        }
    }
}
