// Compiled only while the CAS package is in the project (the CAS_SDK_PRESENT version define on
// this assembly). Without CAS there is no IManagerBuilder to extend — and no reason to fail the
// build of a project that merely has the Hub installed.
#if CAS_SDK_PRESENT
using CAS;
using UnityEngine;

namespace CAS.Publishing.Installer.Runtime
{
    /// <summary>
    /// The one line a game adds to its CAS initialization so attribution works:
    /// <code>
    /// MobileAds.BuildManager()
    ///     .WithPublishingSettings()
    ///     .Initialize();
    /// </code>
    /// CAS then initializes the Tenjin SDK itself and reports ad revenue and purchases
    /// (<c>MobileAds.ReportPurchase</c>) to it.
    /// </summary>
    public static class PublishingBuilderExtensions
    {
        /// <summary>
        /// Passes the Tenjin SDK key from <see cref="PublishingSettings"/> to CAS via the
        /// <c>tenjin_key</c> mediation extra. Without a key here the builder is returned unchanged
        /// and CAS uses the Tenjin key from the app's settings in the CAS dashboard.
        /// </summary>
        public static IManagerBuilder WithPublishingSettings(this IManagerBuilder builder)
        {
            if (builder == null) return null;

            var key = PublishingSettings.TenjinKey;
            if (string.IsNullOrEmpty(key))
            {
                // The Hub no longer asks for the key (its Tenjin step is hidden): the CAS dashboard
                // supplies it. A key still stored here from an earlier Hub wins.
                Debug.Log("[Publishing] No Tenjin SDK key in PublishingSettings — CAS takes it from " +
                          "the app's settings in the CAS dashboard.");
                return builder;
            }

            return builder.WithMediationExtras(MediationExtras.tenjinSDKKey, key);
        }
    }
}
#endif
