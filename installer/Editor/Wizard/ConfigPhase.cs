using System.Collections.Generic;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// The only way into the configuration phase and the only way out of it. Builds the step queue
    /// from the project's actual state, routes to the first step, and once the steps run out,
    /// settles the project's conflicts.
    ///
    /// There is more than one entry: after an install (ProgressScreen), straight from Ready when
    /// everything is installed already, and from AutoInstaller when there is nothing to install.
    /// While building the queue lived inside one of them, the other two went around it. The same
    /// mistake repeated with the conflict pass while it hung on the Placements Continue handler: a
    /// configured project skips that screen, so the code never ran. Hence both decisions live here.
    /// </summary>
    internal static class ConfigPhase
    {
        /// <summary>Rebuilds the queue from the project's state and stores it.</summary>
        public static string[] BuildQueue()
        {
            // A project that had the retired attribution wrapper already owns a Tenjin key: carry it
            // into PublishingSettings first, so the Tenjin step only queues when there is really
            // nothing to show. Empty slots only — a key entered here is never overwritten.
            TenjinKeyMigration.TryMigrate();

            // Two questions per installed row: is the component's own configuration done (its step),
            // and is the Hub's publishing part done (the Tenjin step). They queue independently.
            var needing = new List<string>();
            var tenjinKeyNeeded = false;
            if (SetupModel.TryBuild(out var rows, out _))
                foreach (var row in rows)
                {
                    if (!row.Installed) continue;
                    if (!row.SettingsConfigured) needing.Add(row.Id);
                    if (!row.PublishingConfigured) tenjinKeyNeeded = true;
                }

            var queue = ConfigStepQueue.Build(needing, tenjinKeyNeeded);
            InstallerKeyStore.SetConfigQueue(queue);
            InstallerKeyStore.SetConfigReturnTo(null);
            return queue;
        }

        /// <summary>Builds the queue and routes to its first step.</summary>
        public static void Enter(WizardRouter router)
        {
            if (router == null) return;
            Advance(router, BuildQueue(), null);
        }

        /// <summary>
        /// The transition after a finished step — the step screens use this too. When no steps are
        /// left and this isn't a point entry from Configure, the phase counts as done: the
        /// dependency set is final, which is the moment to settle the project's conflicts.
        ///
        /// A point return (<paramref name="returnTo"/> set) leaves the conflicts alone, or the
        /// heavy resolve would fire on every round trip from Configuration.
        /// </summary>
        public static void Advance(WizardRouter router, string[] queue, string returnTo)
        {
            if (router == null) return;

            var finished = string.IsNullOrEmpty(returnTo) && (queue == null || queue.Length == 0);
            if (finished)
            {
                var platform = PlatformChoice.Selected;
                if (string.IsNullOrEmpty(platform)) platform = PlatformDetect.ActivePlatform();
                CasProjectConflicts.ResolveAll(platform);
                // A project set up before this fix has its templates already, so the install pass
                // never patches them — the end of configuration does.
                AndroidBuildFix.PinErrorProne();
            }

            router.GoTo(ConfigStepQueue.RouteAfter(queue, returnTo));
        }
    }
}
