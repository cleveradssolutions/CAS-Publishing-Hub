using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Per-project store for SDK keys/ids entered in the wizard before the owning package is
    /// installed (so they can be applied to the package's settings afterwards). Keyed by
    /// componentId + platform, so future per-SDK keys (e.g. Gadsme) reuse the same mechanism.
    /// Backed by <see cref="HubProjectState"/> (UserSettings/ of this project). The EditorPrefs keys
    /// below are only where the values lived before, read once for the migration.
    /// </summary>
    internal static class InstallerKeyStore
    {
        private static string Key(string componentId, string platform) =>
            "key:" + componentId + "." + platform;

        private static string LegacyKey(string componentId, string platform) =>
            "CAS.Publishing.Installer.Wizard.Key:" + Application.dataPath + ":" + componentId + "." + platform;

        public static string Get(string componentId, string platform) =>
            HubProjectState.Get(Key(componentId, platform), LegacyKey(componentId, platform));

        public static void Set(string componentId, string platform, string value) =>
            HubProjectState.Set(Key(componentId, platform), value?.Trim());

        public static string GetOrDefault(string componentId, string platform, string fallback)
        {
            var v = Get(componentId, platform);
            return string.IsNullOrEmpty(v) ? (fallback ?? "") : v;
        }

        // The platform picked on Welcome. A separate key rather than a componentId+platform entry:
        // this is a choice about the whole pass, not a value belonging to one package.
        private const string SelectedPlatformKey = "selectedPlatform";

        private static string LegacySelectedPlatformKey =>
            "CAS.Publishing.Installer.Wizard.SelectedPlatform:" + Application.dataPath;

        public static string GetSelectedPlatform() =>
            HubProjectState.Get(SelectedPlatformKey, LegacySelectedPlatformKey);

        public static void SetSelectedPlatform(string platform) =>
            HubProjectState.Set(SelectedPlatformKey, platform?.Trim());

        // The configuration phase: the queue of unfinished steps and the return target. Separate
        // keys, like the platform slot — this is state about the pass, not a value belonging to a
        // package. Held on disk, so it survives the domain reload between install steps.
        private const string ConfigQueueKey = "configQueue";
        private const string ConfigReturnToKey = "configReturnTo";

        private static string LegacyConfigQueueKey =>
            "CAS.Publishing.Installer.Wizard.ConfigQueue:" + Application.dataPath;

        private static string LegacyConfigReturnToKey =>
            "CAS.Publishing.Installer.Wizard.ConfigReturnTo:" + Application.dataPath;

        public static string[] GetConfigQueue()
        {
            var raw = HubProjectState.Get(ConfigQueueKey, LegacyConfigQueueKey);
            var queue = string.IsNullOrEmpty(raw) ? new string[0] : raw.Split(',');
            // A queue built by an older Hub may still hold the Tenjin step; while it is hidden the
            // step must never be routed to.
            return TenjinKeySettings.StepEnabled ? queue : ConfigStepQueue.Without(queue, ConfigStepQueue.StepTenjin);
        }

        public static void SetConfigQueue(string[] steps) =>
            HubProjectState.Set(ConfigQueueKey,
                steps == null || steps.Length == 0 ? null : string.Join(",", steps));

        public static string GetConfigReturnTo() =>
            HubProjectState.Get(ConfigReturnToKey, LegacyConfigReturnToKey);

        public static void SetConfigReturnTo(string screenId) =>
            HubProjectState.Set(ConfigReturnToKey, screenId);
    }
}
