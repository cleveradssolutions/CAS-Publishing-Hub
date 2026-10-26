using UnityEngine;

namespace CAS.Publishing.Installer.Runtime
{
    /// <summary>
    /// Project-level publishing settings: written by the Hub, read by the game at runtime.
    ///
    /// Today it holds the Tenjin SDK keys. From CAS 4.8.1 the native Tenjin SDK ships inside CAS
    /// (the TenjinMMP solution) and CAS initializes it — but CAS has no settings field for the key;
    /// it only accepts it at initialization, through <c>MediationExtras</c>. Without the retired
    /// attribution wrapper the key had no home in the project, so this asset is that home.
    ///
    /// The asset lives in a <c>Resources</c> folder so <see cref="Load"/> can find it in a player
    /// build the same way CAS finds its own settings. One asset serves both platforms — one field per
    /// platform, because Tenjin issues a separate key per app.
    /// </summary>
    public sealed class PublishingSettings : ScriptableObject
    {
        /// <summary>Resource name (and therefore the asset file name) the Hub creates and the game loads.</summary>
        public const string ResourceName = "PublishingSettings";

        [Tooltip("Tenjin SDK key of the Android app (Tenjin dashboard → the app → SDK key).")]
        public string tenjinKeyAndroid;

        [Tooltip("Tenjin SDK key of the iOS app (Tenjin dashboard → the app → SDK key).")]
        public string tenjinKeyIos;

        /// <summary>The asset from Resources, or null when the Hub has not created it yet.</summary>
        public static PublishingSettings Load() => Resources.Load<PublishingSettings>(ResourceName);

        /// <summary>
        /// The Tenjin key for the platform this build targets, or an empty string when the asset is
        /// missing or the key was never entered. Never null, so it can go straight into a builder call.
        /// </summary>
        public static string TenjinKey
        {
            get
            {
                var settings = Load();
                return settings == null ? string.Empty : settings.TenjinKeyFor(TargetPlatform);
            }
        }

        /// <summary>
        /// The key for a platform named the way the Hub names them ("Android" / "iOS"). Anything
        /// else maps to Android. Never null.
        /// </summary>
        public string TenjinKeyFor(string platform)
        {
            var key = platform == "iOS" ? tenjinKeyIos : tenjinKeyAndroid;
            return string.IsNullOrEmpty(key) ? string.Empty : key.Trim();
        }

        // Compile-time, not Application.platform: in the Editor the active build target defines
        // UNITY_IOS / UNITY_ANDROID, so Play Mode reads the same key the player build will.
        private static string TargetPlatform =>
#if UNITY_IOS
            "iOS";
#else
            "Android";
#endif
    }
}
