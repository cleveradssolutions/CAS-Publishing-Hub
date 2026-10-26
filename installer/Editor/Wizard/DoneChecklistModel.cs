namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Pure copy/tone rules for the Done screen's 3-row checklist (CAS SDK / Firebase).
    /// Kept separate from <see cref="Screens.DoneScreen"/> so the string logic is unit-testable
    /// without touching Unity APIs — the screen itself only does live detection (CasSettingsReader,
    /// SetupModel) and hands the results in here.
    /// </summary>
    internal static class DoneChecklistModel
    {
        /// <summary>One checklist row: display text (already includes the ✓/⚠ glyph) and whether it
        /// should render in the warning (yellow) tone instead of the normal (green) one.</summary>
        internal readonly struct Line
        {
            public readonly string Text;
            public readonly bool Warn;

            public Line(string text, bool warn)
            {
                Text = text;
                Warn = warn;
            }
        }

        /// <summary>
        /// CAS SDK row. <paramref name="managerId"/> is the value read for the active platform via
        /// <see cref="CasSettingsReader.ReadExisting"/> — null/empty (not yet set, or CAS not installed)
        /// drops the parenthetical id from the line. Always green: Done is only reached after the
        /// intro install flow, so CAS SDK itself is present.
        /// </summary>
        internal static Line CasLine(string managerId, bool tenjinMmpReady = true)
        {
            // Formats picked before the Hub arrived skip Placements, and with it the Tenjin MMP
            // switch — "mediation ready" was then a lie with no attribution SDK in the build.
            if (!tenjinMmpReady)
                return new Line("⚠ CAS SDK — Tenjin MMP not enabled (open Placements)", true);
            return string.IsNullOrEmpty(managerId)
                ? new Line("✓ CAS SDK — mediation ready", false)
                : new Line($"✓ CAS SDK — mediation ready ({managerId})", false);
        }

        /// <summary>Tenjin row: green when the active platform's Tenjin SDK key is stored in the
        /// publishing settings, yellow otherwise. The SDK itself ships inside CAS, so the key is
        /// the only thing left to report.</summary>
        internal static Line TenjinLine(bool keySet) =>
            keySet
                ? new Line("✓ Tenjin — attribution key set", false)
                : new Line("⚠ Tenjin — SDK key missing", true);

        /// <summary>Firebase row: green when the active platform's config file/field checks all
        /// come back Configured (or NotApplicable), yellow otherwise.</summary>
        internal static Line FirebaseLine(bool configured) =>
            configured
                ? new Line("✓ Firebase — analytics connected", false)
                : new Line("⚠ Firebase — configuration file missing", true);
    }
}
