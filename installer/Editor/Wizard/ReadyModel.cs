using System.Collections.Generic;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// One row on the "Ready to install" screen — a default component's display copy plus the
    /// right-column status text (target version, or an "already installed" confirmation).
    /// </summary>
    internal sealed class ReadyRow
    {
        public string Id;
        public string Name;
        public string Sub;
        public string RightText;      // "4.x.x" (target) or "✓ Already installed (v)"
        public bool   AlreadyInstalled;
        public bool   BlocksFlow;     // below the pin and this is the blocking component (CAS)
        /// <summary>
        /// Present as a manual (.unitypackage) copy, not under UPM. Counts as installed for the flow,
        /// but the row offers "Connect to Hub" — the Components tab did, this screen did not, and a
        /// first-run user never got there.
        /// </summary>
        public bool   OutsideUpm;
        /// <summary>A retired package (catalog uninstall rule) the install pass will remove.</summary>
        public bool   Retired;
    }

    /// <summary>
    /// Pure view-model for the "Ready to install" screen (step 1 of the install flow):
    /// derives per-row display state from the live <see cref="ComponentStatus"/> scan, and
    /// decides whether the primary button reads "Install" or "Continue".
    /// </summary>
    internal sealed class ReadyModel
    {
        public readonly List<ReadyRow> Rows = new List<ReadyRow>();
        public bool AllInstalled = true;
        public string PrimaryButtonText => AllInstalled ? "Continue" : "Install";

        /// <summary>The flow is barred until an update. Today that means CAS below the catalog pin.</summary>
        public bool Blocked;

        /// <summary>The copy under the list when <see cref="Blocked"/>. Null when nothing is barred.</summary>
        public string BlockMessage;

        /// <summary>
        /// Applies the version gate to a single component. Extracted into a method rather than
        /// buried in Build so the rule is verifiable without assembling a live ComponentStatus list.
        /// </summary>
        public void ApplyGate(string componentId, string installedVersion, string pinnedVersion)
        {
            if (!CasVersionGate.BlocksFlow(componentId, installedVersion, pinnedVersion)) return;

            // Print the parsed number, not the raw dependency value: for a git URL the raw value is
            // the whole address, which would make the message unreadable.
            var shown = CasVersionGate.Normalize(installedVersion) ?? installedVersion;

            Blocked = true;
            BlockMessage =
                $"CAS {shown} does not support Tenjin attribution. " +
                $"Update to {pinnedVersion} to continue — the flow cannot proceed without it.";
        }

        public static ReadyModel Build(List<ComponentStatus> statuses)
        {
            var m = new ReadyModel();
            foreach (var s in statuses)
            {
                var installed = s.Installed;
                var pin = installed ? ComponentStatusProvider.ResolveRecommendedVersion(s.Id) : null;
                var blocks = installed && CasVersionGate.BlocksFlow(s.Id, s.Version, pin);
                if (blocks) m.ApplyGate(s.Id, s.Version, pin);

                m.Rows.Add(new ReadyRow
                {
                    Id = s.Id,
                    BlocksFlow = blocks,
                    Name = s.DisplayName,
                    Sub = s.Sub,
                    AlreadyInstalled = installed,
                    OutsideUpm = installed && s.OutsideUpm,
                    RightText = RightTextFor(s, installed, blocks, pin),
                });
                if (!installed) m.AllInstalled = false;
            }
            return m;
        }

        /// <summary>
        /// The status column. A manual copy is named as such, with the version the SDK reports or
        /// an honest "version unknown" — "✓ Already installed (?)" read as a finished install.
        /// </summary>
        internal static string RightTextFor(ComponentStatus s, bool installed, bool blocks, string pin)
        {
            if (blocks) return $"To update ({pin})";
            if (!installed)
                return s.Version ?? ComponentStatusProvider.ResolveRecommendedVersion(s.Id) ?? "latest";
            if (s.OutsideUpm) return $"Installed manually ({s.Version ?? "version unknown"})";
            return $"✓ Already installed ({s.Version ?? "?"})";
        }
    }
}
