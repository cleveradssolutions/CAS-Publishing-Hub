namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>Pure bit helpers for the CAS <c>allowedAdFlags</c> bitmask. Values mirror CAS's
    /// <c>AdFlags</c> enum: Banner=1, Interstitial=2, Rewarded=4, AppOpen=8.</summary>
    internal static class AdFlagsBits
    {
        public const int Banner = 1;
        public const int Interstitial = 2;
        public const int Rewarded = 4;
        public const int AppOpen = 8;

        public static bool HasFlag(int mask, int flag) => (mask & flag) == flag && flag != 0;

        public static int WithFlag(int mask, int flag, bool on) => on ? (mask | flag) : (mask & ~flag);
    }
}
