using UnityEditor;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Maps the project's active build target to the platform string the wizard uses
    /// ("Android" | "iOS"). Any non-iOS target (Android and all desktop/other targets)
    /// maps to "Android" — the single-platform Welcome pass defaults there and stays switchable.
    /// </summary>
    internal static class PlatformDetect
    {
        /// <summary>Pure mapping: iOS → "iOS", everything else → "Android". Testable.</summary>
        internal static string FromBuildTarget(BuildTarget target) =>
            target == BuildTarget.iOS ? "iOS" : "Android";

        /// <summary>The platform for the project's current active build target.</summary>
        public static string ActivePlatform() =>
            FromBuildTarget(EditorUserBuildSettings.activeBuildTarget);

        /// <summary>
        /// Three-state pure mapping for views that must NOT default a non-Android/iOS target (desktop,
        /// WebGL, …) into "Android": Android → "Android", iOS → "iOS", everything else → "Other".
        /// Unlike <see cref="FromBuildTarget"/>/<see cref="ActivePlatform"/> (which stay untouched —
        /// DoneScreen's checklist and BuildTargetWatcher's switch-policy defaults still need the
        /// always-Android-or-iOS answer), this is for the Configuration screen's single-platform view,
        /// which shows a "switch to Android/iOS" prompt instead of guessing a platform on Other. Testable.
        /// </summary>
        internal static string FromBuildTargetThreeState(BuildTarget target)
        {
            if (target == BuildTarget.iOS) return "iOS";
            if (target == BuildTarget.Android) return "Android";
            return "Other";
        }

        /// <summary>The three-state platform ("Android" | "iOS" | "Other") for the project's current
        /// active build target.</summary>
        public static string ActivePlatformOrOther() =>
            FromBuildTargetThreeState(EditorUserBuildSettings.activeBuildTarget);
    }
}
