using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Common;
using UnityEditor.PackageManager;
using UnityEngine.UIElements;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// About tab — shows the installed installer version, checks the registry for a newer one
    /// (highest published version, robust against the prerelease dist-tag pin), and self-updates
    /// via UPM. Updating triggers a resolve + domain reload into the new installer version.
    /// </summary>
    internal sealed class AboutScreen : IWizardScreen
    {
        public string Id => "about";
        public VisualElement Root { get; }

        private bool _bound;
        private readonly Label _current;
        private readonly Label _latest;
        private readonly Label _status;
        private readonly Label _notes;
        private readonly Button _update;
        private readonly Button _check;
        private string _latestVersion;

        public AboutScreen()
        {
            Root = new VisualElement();
            WizardAssets.CloneInto("About", Root);
            _current = Root.Q<Label>("about-current");
            _latest  = Root.Q<Label>("about-latest");
            _status  = Root.Q<Label>("about-status");
            _notes   = Root.Q<Label>("about-notes");
            _update  = Root.Q<Button>("about-update");
            _check   = Root.Q<Button>("about-check");

            if (_current != null) _current.text = "v" + WizardAssets.InstallerVersion;
            if (_update != null)  _update.style.display = DisplayStyle.None;
            if (_notes != null)   _notes.text = LoadReleaseNotes();
        }

        // Reads the installed package's CHANGELOG.md from disk (Unity does NOT import .md as a
        // TextAsset, so AssetDatabase can't load it — read the file via the package's resolved
        // path). Skips the preamble before the first version heading.
        private static string LoadReleaseNotes()
        {
            try
            {
                var info = PackageInfo.FindForAssembly(typeof(AboutScreen).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
                {
                    var path = System.IO.Path.Combine(info.resolvedPath, "CHANGELOG.md");
                    if (System.IO.File.Exists(path))
                    {
                        var text = System.IO.File.ReadAllText(path);
                        var idx = text.IndexOf("## ", System.StringComparison.Ordinal);
                        return (idx >= 0 ? text.Substring(idx) : text).Trim();
                    }
                }
            }
            catch
            {
                // fall through to the default message
            }
            return "No release notes available.";
        }

        public void OnEnter(WizardRouter router)
        {
            if (_bound) return;
            _bound = true;

            if (_check != null)  _check.clicked  += CheckForUpdates;
            if (_update != null) _update.clicked += DoUpdate;

            CheckForUpdates(); // auto-check the first time the tab is opened
        }

        private void CheckForUpdates()
        {
            if (InstallerSource.IsGit())
            {
                if (_latest != null)  _latest.text = "git";
                if (_update != null)  _update.style.display = DisplayStyle.None;
                SetStatus("Installed via git. To update, change the installer git URL to a newer " +
                          "tag in Packages/manifest.json (Package Manager → installer → version).", null);
                return;
            }

            _latestVersion = null;
            if (_latest != null) _latest.text = "checking…";
            if (_update != null) _update.style.display = DisplayStyle.None;
            SetStatus("Checking for updates…", null);

            CatalogUpdater.CheckLatestVersion(UpdateBadgeState.PackageId,
                onSuccess: latest =>
                {
                    if (_latest != null) _latest.text = latest;
                    var current = WizardAssets.InstallerVersion;

                    if (CatalogUpdater.IsNewer(latest, current))
                    {
                        _latestVersion = latest;
                        SetStatus($"Update available: v{current} → {latest}", "warn");
                        if (_update != null) _update.style.display = DisplayStyle.Flex;
                    }
                    else
                    {
                        SetStatus("You're on the latest version.", "ok");
                    }
                },
                onFailure: err =>
                {
                    if (_latest != null) _latest.text = "—";
                    SetStatus("Couldn't check for updates: " + err, null);
                });
        }

        private void DoUpdate()
        {
            if (string.IsNullOrEmpty(_latestVersion)) return;
            SetStatus($"Updating to {_latestVersion}… Unity will resolve and reload.", null);
            if (_update != null) _update.SetEnabled(false);
            CatalogUpdater.TrackInstall(Client.Add($"{UpdateBadgeState.PackageId}@{_latestVersion}"), "Installer");
            UpdateBadgeState.Reset(); // clear the badge for the post-update reload
        }

        private void SetStatus(string text, string tone)
        {
            if (_status == null) return;
            _status.text = text;
            _status.RemoveFromClassList("cas-setup-summary--ok");
            _status.RemoveFromClassList("cas-setup-summary--warn");
            if (tone == "ok")        _status.AddToClassList("cas-setup-summary--ok");
            else if (tone == "warn") _status.AddToClassList("cas-setup-summary--warn");
        }
    }
}
