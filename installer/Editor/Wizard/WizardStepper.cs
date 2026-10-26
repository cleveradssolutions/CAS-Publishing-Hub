namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Maps a screen id to its 1-based position in the 3-step intro flow (Install → Configure →
    /// Done) for the stepper header. Null means "not a flow screen" — the caller (InstallerWizardWindow.
    /// UpdateTabBar) falls back to the tab bar for those ids. Pure/testable.
    /// </summary>
    internal static class WizardStepper
    {
        internal static int? StepFor(string screenId)
        {
            switch (screenId)
            {
                case "ready":
                case "progress":
                    return 1;
                case "configure":
                    return 2;
                case "done":
                    return 3;
                default:
                    return null;
            }
        }

        /// <summary>
        /// True when step <paramref name="step"/> is clickable from the current screen: only
        /// already-passed steps navigate back, and only while inside the 3-step stepper flow
        /// itself (non-flow ids, and "done", yield no clickable step — see the design doc for
        /// why Done is excluded even though step 3 == currentStep there).
        /// </summary>
        internal static bool IsStepClickable(int step, string currentScreenId)
        {
            var current = StepFor(currentScreenId);
            if (!current.HasValue) return false;
            if (currentScreenId == "done") return false;
            return step < current.Value;
        }

        /// <summary>Screen id a stepper click on <paramref name="step"/> navigates to. Step 3
        /// ("done") is returned for completeness — <see cref="IsStepClickable"/> never allows it.</summary>
        internal static string TargetScreenFor(int step)
        {
            switch (step)
            {
                case 1: return "ready";
                case 2: return "configure";
                case 3: return "done";
                default: return null;
            }
        }

        /// <summary>
        /// True when the stepper header (not the tab bar) belongs on screen. Pre-intro this is
        /// just "is a flow screen" — WizardMode is irrelevant, nothing has been decided yet. Once
        /// <paramref name="introDone"/>, the flow screens (ready/progress/configure/done) are
        /// normally unreachable via the stepper — WizardMode is the transient "I came back via the
        /// ↺ Setup Wizard button" flag that makes them show the stepper again for one more pass.
        /// </summary>
        internal static bool ShouldShowStepper(string screenId, bool introDone, bool wizardMode)
        {
            if (!StepFor(screenId).HasValue) return false;
            return !introDone || wizardMode;
        }

        /// <summary>
        /// True when a lingering <c>WizardMode</c> flag must be self-healed back to false on entry
        /// to <paramref name="screenId"/> — i.e. any non-flow screen (components/about, and any
        /// unrecognized id). Prevents a stuck flag (e.g. a watchdog exit from Progress straight to
        /// Components) from turning a later, unrelated tab click into a stepper view. Flow screens
        /// (ready/progress/configure/done) never self-heal here — WizardMode there is cleared only
        /// at its two deliberate exit points (Ready's Advanced handler, DoneScreen.OnEnter).
        /// </summary>
        internal static bool ShouldClearWizardMode(string screenId) => !StepFor(screenId).HasValue;
    }
}
