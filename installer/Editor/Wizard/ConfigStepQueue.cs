using System.Collections.Generic;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// The order of the configuration phase. The queue is rebuilt every time the wizard opens,
    /// from the components whose catalog requirements aren't met; a screen drops its own step on
    /// Continue and asks <see cref="RouteAfter"/> where to go next.
    ///
    /// The returnTo slot is the point entry: Configuration's Configure button queues a single step
    /// and asks to come back, instead of continuing along the first-pass order.
    ///
    /// Only pure array operations live here; storage is in <see cref="InstallerKeyStore"/> so this
    /// stays testable without EditorPrefs.
    /// </summary>
    internal static class ConfigStepQueue
    {
        public const string StepCas = "cas";
        public const string StepTenjin = "tenjin";
        public const string StepFirebase = "firebase";

        private const string CasComponent = "com.cleversolutions.ads.unity";
        private const string FirebaseComponent = "com.google.firebase.analytics";

        /// <summary>
        /// First-pass order: CAS, then the Tenjin key, then Firebase. The Tenjin step is not a
        /// component of its own — the key belongs to CAS, which initializes Tenjin — so it is asked
        /// for separately: a CAS row whose formats are set but whose key is missing needs only the
        /// Tenjin step, not Placements again.
        /// </summary>
        public static string[] Build(IReadOnlyList<string> componentIdsNeedingConfig, bool tenjinKeyNeeded = false)
        {
            var steps = new List<string>(3);

            var wantsCas = false;
            var wantsFirebase = false;
            if (componentIdsNeedingConfig != null)
                foreach (var id in componentIdsNeedingConfig)
                {
                    if (id == CasComponent) wantsCas = true;
                    else if (id == FirebaseComponent) wantsFirebase = true;
                }

            if (wantsCas) steps.Add(StepCas);
            if (tenjinKeyNeeded) steps.Add(StepTenjin);
            if (wantsFirebase) steps.Add(StepFirebase);
            return steps.ToArray();
        }

        public static string[] Without(string[] queue, string stepId)
        {
            if (queue == null) return new string[0];
            var left = new List<string>(queue.Length);
            foreach (var s in queue)
                if (s != stepId) left.Add(s);
            return left.ToArray();
        }

        public static string ScreenFor(string stepId)
        {
            if (stepId == StepCas) return "placements";
            if (stepId == StepTenjin) return "tenjin-setup";
            if (stepId == StepFirebase) return "firebase-setup";
            return "configure";
        }

        /// <summary>
        /// Where to go once a step is done: the return target first (when set), otherwise the first
        /// remaining step, otherwise the Configuration screen.
        /// </summary>
        public static string RouteAfter(string[] queue, string returnTo)
        {
            if (!string.IsNullOrEmpty(returnTo)) return returnTo;
            if (queue == null || queue.Length == 0) return "configure";
            return ScreenFor(queue[0]);
        }
    }
}
