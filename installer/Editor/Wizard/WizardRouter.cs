using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Holds the screen registry and the content host. Shows exactly one screen at a time
    /// by toggling display:flex/none, with a short opacity fade. Keeps a history stack for Back().
    /// </summary>
    internal sealed class WizardRouter
    {
        private readonly VisualElement _host;
        private readonly Dictionary<string, IWizardScreen> _screens = new Dictionary<string, IWizardScreen>();
        private readonly List<string> _order = new List<string>();
        private readonly Stack<string> _history = new Stack<string>();
        private readonly Action<string> _onScreenShown;
        private string _current;

        /// <param name="onScreenShown">Invoked with the screen id each time a screen is shown —
        /// used to persist the current screen across domain reloads.</param>
        public WizardRouter(VisualElement contentHost, Action<string> onScreenShown = null)
        {
            _host = contentHost;
            _onScreenShown = onScreenShown;
        }

        public string Current => _current;
        public IReadOnlyList<string> ScreenIds => _order;

        public void Register(IWizardScreen screen)
        {
            if (_screens.ContainsKey(screen.Id)) return;
            _screens[screen.Id] = screen;
            _order.Add(screen.Id);

            screen.Root.style.display = DisplayStyle.None;
            screen.Root.style.flexGrow = 1f;
            if (screen.Root.parent != _host)
                _host.Add(screen.Root);
        }

        /// <summary>Navigate to a screen, pushing the previous one onto the history stack.</summary>
        public void GoTo(string id)
        {
            if (!_screens.TryGetValue(id, out var next))
            {
                Debug.LogWarning($"[CAS Hub] No screen registered with id '{id}'.");
                return;
            }

            if (_current != null && _current != id)
                _history.Push(_current);

            Show(id, next);
        }

        /// <summary>Pop history; no-op when empty.</summary>
        public void Back()
        {
            if (_history.Count == 0) return;
            var id = _history.Pop();
            if (_screens.TryGetValue(id, out var screen))
                Show(id, screen);
        }

        /// <summary>Jump without recording history (used by the dev picker).</summary>
        public void Preview(string id)
        {
            if (_screens.TryGetValue(id, out var screen))
                Show(id, screen);
        }

        private void Show(string id, IWizardScreen screen)
        {
            if (_current != null && _current != id && _screens.TryGetValue(_current, out var prev))
                prev.Root.style.display = DisplayStyle.None;

            // Instant switch — guarantees the screen is visible. A fade can be layered on
            // later once the base render is verified in the editor.
            screen.Root.style.opacity = 1f;
            screen.Root.style.display = DisplayStyle.Flex;
            _current = id;
            _onScreenShown?.Invoke(id);

            screen.OnEnter(this);
        }
    }
}
