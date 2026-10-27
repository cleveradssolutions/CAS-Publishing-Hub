using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Common;
using CAS.Publishing.Installer.Migrator;
using CAS.Publishing.Installer.Scanner;
using CAS.Publishing.Installer.Telemetry;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Agent mode: the Hub without its window. Three entry points for <c>-executeMethod</c>, meant
    /// for an AI coding agent (or a script) that integrates the SDK without anyone clicking through
    /// the wizard — see <c>AGENTS.md</c> at the package root for the full procedure.
    ///
    /// <list type="bullet">
    ///   <item><see cref="Install"/> — one Scan → Plan → Apply pass over the default component set,
    ///     the same pipeline the wizard's "Install recommended" runs, minus the dialogs. Writes
    ///     <c>Packages/manifest.json</c>; Unity resolves the packages on its next launch, so the
    ///     caller runs it again until the report says <c>installed</c>.</item>
    ///   <item><see cref="Configure"/> — applies a JSON config (CAS ID, ad formats, audience, Tenjin
    ///     key, Firebase config file) through the very same writers the Placements / Tenjin /
    ///     Firebase steps use.</item>
    ///   <item><see cref="Status"/> — the readiness table of the Configuration screen, as JSON.</item>
    /// </list>
    ///
    /// Every run writes a JSON report (<c>-casHubReport &lt;path&gt;</c>, default
    /// <c>Library/CASHub/report.json</c>), echoes it to the log and exits with 0 on success, 1 on
    /// failure. Deliberately NOT wired to <c>Bootstrap</c>: the batch-mode guard there still holds —
    /// nothing happens in CI unless one of these methods is named on the command line. The
    /// migration safety rules hold too: deletes still go through <see cref="MigrationRunner"/> and
    /// its git guard, and a refusal fails the run instead of asking a question nobody can answer.
    /// </summary>
    public static class HeadlessHub
    {
        private const string LogPrefix = "[CAS Hub]";
        private const string ConfigArg = "-casHubConfig";
        private const string ReportArg = "-casHubReport";
        private const string DefaultReportPath = "Library/CASHub/report.json";

        /// <summary>Which platform a configuration pass targets ("Android" / "iOS").</summary>
        private static readonly string[] Platforms = { "Android", "iOS" };

        // ── Entry points ─────────────────────────────────────────────────────────────

        public static void Install() => Run("install", RunInstall);
        public static void Configure() => Run("configure", RunConfigure);
        public static void Status() => Run("status", RunStatus);

        // ── Runner ───────────────────────────────────────────────────────────────────

        private static void Run(string command, Action<JObject> body)
        {
            var report = new JObject
            {
                ["command"] = command,
                ["ok"] = false,
                ["state"] = "failed",
                ["messages"] = new JArray(),
            };

            HubTelemetry.Mode = "headless";
            var started = DateTime.UtcNow;
            try
            {
                body(report);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Fail(report, $"{e.GetType().Name}: {e.Message}");
            }

            WriteReport(report);
            TrackRun(command, report, started);
            EditorApplication.Exit(report.Value<bool>("ok") ? 0 : 1);
        }

        // One event per agent-mode run; sent before the Editor exits (anything that doesn't make it
        // in time stays queued and goes with the next run).
        private static void TrackRun(string command, JObject report, DateTime started)
        {
            var ok = report.Value<bool>("ok");
            var messages = report["messages"] as JArray;
            HubTelemetry.Track("headless_run", new Dictionary<string, object>
            {
                ["command"] = command,
                ["result"] = report.Value<string>("state") ?? "",
                ["durationMs"] = (long)(DateTime.UtcNow - started).TotalMilliseconds,
                ["error"] = !ok && messages != null && messages.Count > 0 ? messages.Last.ToString() : null,
            });
            HubTelemetry.FlushBlocking();
        }

        private static void Fail(JObject report, string message)
        {
            report["ok"] = false;
            report["state"] = "failed";
            Say(report, message);
        }

        private static void Say(JObject report, string message)
        {
            ((JArray)report["messages"]).Add(message);
            Debug.Log($"{LogPrefix} {message}");
        }

        private static void WriteReport(JObject report)
        {
            var path = ArgValue(ReportArg) ?? DefaultReportPath;
            try
            {
                var full = Path.GetFullPath(Path.Combine(ProjectRoot, path));
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, report.ToString());
                Debug.Log($"{LogPrefix} report written to {full}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogPrefix} could not write the report to '{path}': {e.Message}");
            }
            // The log is the one channel every caller has; the marker makes the JSON greppable.
            Debug.Log($"{LogPrefix} REPORT {report.ToString(Newtonsoft.Json.Formatting.None)}");
        }

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        // ── Install ──────────────────────────────────────────────────────────────────

        /// <summary>Selects a fixed id set at the recommended version target.</summary>
        private sealed class IdSetSelection : ISelectionSet
        {
            private readonly HashSet<string> _ids;
            public IdSetSelection(IEnumerable<string> ids) { _ids = new HashSet<string>(ids); }
            public bool IsSelected(string id) => _ids.Contains(id);
            public VersionTarget GetTarget(string id) => VersionTarget.Recommended;
        }

        private static void RunInstall(JObject report)
        {
            // Same first move as the wizard's recommended install: a buildable Android project
            // needs the gradle/manifest templates before EDM4U resolves anything.
            var templates = AndroidBuildFix.Ensure();
            report["androidTemplatesCreated"] = templates;
            if (templates > 0) Say(report, $"Created {templates} missing Android build template(s).");

            if (!TryLoadCatalog(report, out var catalog)) return;

            var scan = ProjectScanner.Scan(catalog);
            var manifest = ManifestProbe.Read();
            if (!manifest.Readable)
            {
                Fail(report, $"Packages/manifest.json is unreadable: {manifest.ReadError}");
                return;
            }

            // A legacy Firebase install must migrate as one family (see WizardActions): apply that
            // compound plan first and let the caller come back for the rest after the resolve.
            foreach (var id in ComponentStatusProvider.DefaultIds)
            {
                var group = LegacySplitRouting.FindGroupFor(scan, manifest.Dependencies, id);
                if (group == null) continue;

                Func<string, bool> embedded = pkg =>
                    Directory.Exists(Path.Combine(ProjectRoot, "Packages", pkg));
                var built = FirebaseMigrationPlan.Build(
                    catalog, group, manifest.Dependencies,
                    AssetInstallProbe.CollectLoadedIdentifiers(), embedded);
                foreach (var w in built.Warnings) Say(report, $"warning: {w}");

                if (built.Actions.Count == 0)
                {
                    Fail(report, $"Legacy {group.LegacyId} is in the manifest but the catalog cannot " +
                                 "replace it — update the metadata package and run again.");
                    return;
                }

                Say(report, $"Migrating legacy {group.LegacyId} to native Firebase + adapters.");
                ApplyPlan(report, catalog, built.Actions);
                return;
            }

            // A manual (Assets) copy of a default SDK moves to UPM the way "Connect to Hub" moves it:
            // its files go first, behind the git guard, and only then the manifest. Planning around
            // it instead put the package on top of the copy — Unity dropped the package over GUID
            // conflicts and the old copy kept running under an "installed" report.
            if (scan.External != null)
                foreach (var ext in scan.External)
                {
                    if (ext == null || ext.State != ExternalState.InstalledOutsideUpm) continue;
                    if (!IsDefault(ext.Id)) continue;
                    MigrateManualCopy(report, catalog, ext.Id);
                    return;
                }

            // A manual (Assets) copy of a retired package — the Tenjin plugin in Assets/Tenjin — goes
            // the way the wizard's Install all removes it: deleted behind the git guard, with the
            // game's scripts that call into it listed first.
            var manualRetired = ComponentStatusProvider.ManualRetiredCopies(catalog);
            if (manualRetired.Count > 0)
            {
                var deletes = new List<MigrationAction>();
                foreach (var copy in manualRetired)
                {
                    Say(report, $"{copy.Id} is a retired package copied into Assets/ — removing it.");
                    foreach (var path in copy.Paths) deletes.Add(new BackupAndDeletePath(path));
                }
                ApplyPlan(report, catalog, deletes, deletes, manualRetired);
                return;
            }

            var plan = PlanDefaults(catalog, scan, manifest, out var warnings);
            foreach (var w in warnings) Say(report, $"warning: {w.Id}: {w.Message}");

            if (plan.Count == 0)
            {
                report["ok"] = true;
                report["state"] = "installed";
                Say(report, "Nothing left to install — every default component is present.");
                HeadlessInstallTelemetry.Finished(InstallStatePath, IsRegistered, DateTime.UtcNow);
                AppendStatus(report, catalog);
                return;
            }

            ApplyPlan(report, catalog, plan);
        }

        /// <summary>
        /// The wizard's recommended-install plan, minus git adds that would rewrite a dependency the
        /// manifest already holds at that exact spec. The planner emits the git chain of a
        /// <c>installMethod: git</c> record on every pass (the wizard tolerates the redundant
        /// re-add); here it would keep the Install loop at "resolving" forever, so it is dropped.
        /// </summary>
        private static IReadOnlyList<MigrationAction> PlanDefaults(
            PackageCatalog catalog, ScanReport scan, ManifestData manifest,
            out IReadOnlyList<PlannerWarning> warnings)
        {
            // Retired packages (the old Tenjin wrapper, …) go too — the same selection as the wizard.
            var plan = MigrationPlanner.Plan(
                catalog, scan,
                new IdSetSelection(AutoInstaller.SelectionFor(AutoInstaller.RetiredIds(catalog, scan))),
                InstallMethod.Upm, out warnings);

            var deps = manifest.Readable ? manifest.Dependencies : null;
            if (deps == null) return plan;

            var filtered = new List<MigrationAction>(plan.Count);
            foreach (var action in plan)
            {
                if (action is AddGitPackage git
                    && deps.TryGetValue(git.Id, out var current)
                    && string.Equals(current, git.Spec, StringComparison.Ordinal))
                    continue;
                filtered.Add(action);
            }
            return filtered;
        }

        private static bool IsDefault(string id)
        {
            foreach (var d in ComponentStatusProvider.DefaultIds)
                if (d == id) return true;
            return false;
        }

        /// <summary>
        /// Deletes the manual copy of <paramref name="id"/> and then adds its UPM package. Never
        /// overrides the git guard: files git cannot restore stay put, the manifest stays untouched,
        /// and the report names the paths for the human (AGENTS.md: "refusing to delete").
        /// </summary>
        private static void MigrateManualCopy(JObject report, PackageCatalog catalog, string id)
        {
            ExternalRecord rec = null;
            if (catalog.External != null)
                foreach (var e in catalog.External)
                    if (e != null && e.Id == id) { rec = e; break; }
            if (rec == null)
            {
                Fail(report, $"{id} is installed manually but the catalog has no record to migrate it to.");
                return;
            }

            var prepared = WizardActions.PrepareExternalMigration(catalog, rec);
            if (prepared.Error != null)
            {
                Fail(report, $"{id} is installed manually but the catalog has no version to migrate it to.");
                return;
            }
            if (prepared.DeletePaths.Count == 0)
            {
                Fail(report, $"{id} is installed manually, but its known folders were not found under " +
                             "Assets/ (non-standard layout). Ask the human to remove the manual copy, then run again.");
                return;
            }

            Say(report, $"{id} is installed manually (Assets/) — removing that copy before adding the package.");
            var deletes = new List<MigrationAction>();
            foreach (var path in prepared.DeletePaths) deletes.Add(new BackupAndDeletePath(path));

            var deleted = new MigrationRunner().Apply(deletes);
            if (!deleted.Success)
            {
                foreach (var f in deleted.Failures) Say(report, $"failed: {f}");
                Fail(report, "The manual copy was not removed, and Packages/manifest.json was NOT changed. " +
                             "Show the human the paths above: they commit or restore those files, then run again.");
                return;
            }

            foreach (var path in prepared.SharedLeftovers)
                Say(report, $"warning: Assets/{path} in the shared Plugins folder may belong to {id} — " +
                            "not deleted; ask the human whether to remove it.");

            var all = new List<MigrationAction>(deletes);
            all.AddRange(prepared.AddPlan);
            ApplyPlan(report, catalog, prepared.AddPlan, all);
        }

        private static void ApplyPlan(JObject report, PackageCatalog catalog, IReadOnlyList<MigrationAction> plan)
            => ApplyPlan(report, catalog, plan, plan);

        // reported: the actions listed in the report — a superset of plan when some (deletes) already ran.
        private static void ApplyPlan(JObject report, PackageCatalog catalog, IReadOnlyList<MigrationAction> plan,
            IReadOnlyList<MigrationAction> reported,
            IReadOnlyList<ComponentStatusProvider.RetiredPackage> manualCopies = null)
        {
            var actions = new JArray();
            foreach (var a in reported) actions.Add(Describe(a));
            report["actions"] = actions;

            // Scripts that call into a package this plan removes: the agent gets the list to fix,
            // instead of meeting it as compile errors on the next run.
            var references = WizardActions.ReferencesFor(catalog, plan, manualCopies);
            var referenced = new JArray();
            foreach (var hit in references)
            {
                var files = new JArray();
                foreach (var f in hit.Files) files.Add(f);
                referenced.Add(new JObject { ["package"] = hit.PackageId, ["files"] = files });
                Say(report, $"warning: {hit.Files.Count} script(s) reference {hit.PackageId}, which this " +
                            "run removes — replace them with CAS (it initializes Tenjin itself; purchases " +
                            "go through CASHub.ReportPurchase): " + string.Join(", ", hit.Files));
            }
            report["codeReferences"] = referenced;

            var result = new MigrationRunner().Apply(plan);
            report["executed"] = result.ExecutedCount;

            var warnings = WarningCount(report);
            if (!result.Success)
            {
                HeadlessInstallTelemetry.ApplyFailed(InstallStatePath, plan, warnings, result.Failures, DateTime.UtcNow);
                foreach (var f in result.Failures) Say(report, $"failed: {f}");
                Fail(report, "Apply failed — the manifest may be partially updated; inspect with " +
                             "'git status' / 'git diff Packages/manifest.json'.");
                return;
            }

            HeadlessInstallTelemetry.Applied(InstallStatePath, plan, warnings, DateTime.UtcNow);
            report["ok"] = true;
            report["state"] = "resolving";
            Say(report, $"Applied {result.ExecutedCount} action(s) to Packages/manifest.json. Unity " +
                        "resolves the packages on its next launch — run Install again until the " +
                        "state is 'installed'.");
        }

        private static string InstallStatePath => HeadlessInstallTelemetry.DefaultPath(ProjectRoot);

        // The "warning: …" lines this run has shown the agent so far — what install_started reports
        // as warnings, like the wizard's confirmation dialog count.
        private static int WarningCount(JObject report)
        {
            var count = 0;
            foreach (var m in (JArray)report["messages"])
                if (((string)m).StartsWith("warning:", StringComparison.Ordinal)) count++;
            return count;
        }

        private static bool IsRegistered(string id)
        {
            foreach (var pkg in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                if (pkg.name == id) return true;
            return false;
        }

        private static string Describe(MigrationAction action)
        {
            switch (action)
            {
                case AddPackage add:           return $"add {add.Id}@{add.Version}";
                case AddGitPackage git:        return $"add {git.Id} (git: {git.Spec})";
                case UpdatePackageVersion upd: return $"update {upd.Id} -> {upd.Version}";
                case RemovePackage rem:        return $"remove {rem.Id}";
                case AddScopedRegistry reg:    return $"register scope {reg.Scope} ({reg.Url})";
                case AddScopeToRegistry sc:    return $"add scope {sc.Scope} to {sc.Url}";
                case BackupAndDeletePath del:  return $"delete Assets/{del.RelativePath}";
                default:                       return action.GetType().Name;
            }
        }

        private static bool TryLoadCatalog(JObject report, out PackageCatalog catalog)
        {
            catalog = null;
            CatalogLoader.InvalidateCache();
            var load = CatalogLoader.Load();
            switch (load.Status)
            {
                case CatalogLoadStatus.Ok:
                    catalog = load.Catalog;
                    report["catalogVersion"] = catalog.CatalogVersion;
                    // registryFirst records plan from the registry once it has a qualifying version.
                    RegistryVersions.EnsureChecked(catalog);
                    return true;

                case CatalogLoadStatus.NotInstalled:
                    // Batch mode never auto-installs the catalog (Bootstrap's guard); the caller adds
                    // the dependency to the manifest — the README shows the entry — and comes back.
                    Fail(report, $"The metadata catalog ({EditionInfo.MetadataPackageId}) is not in the " +
                                 "project yet. Add it next to the installer in Packages/manifest.json " +
                                 "and run again once Unity has resolved it.");
                    return false;

                default:
                    Fail(report, $"The metadata catalog is present but unreadable: {load.Error}");
                    return false;
            }
        }

        // ── Configure ────────────────────────────────────────────────────────────────

        private static void RunConfigure(JObject report)
        {
            var configPath = ArgValue(ConfigArg);
            if (string.IsNullOrEmpty(configPath))
            {
                Fail(report, $"Missing {ConfigArg} <path-to-json>.");
                return;
            }

            JObject config;
            try
            {
                config = JObject.Parse(File.ReadAllText(Path.Combine(ProjectRoot, configPath)));
            }
            catch (Exception e)
            {
                Fail(report, $"Cannot read config '{configPath}': {e.Message}");
                return;
            }

            if (!TryLoadCatalog(report, out var catalog)) return;

            if (!CasPresence.IsInstalled())
            {
                Fail(report, "CAS is not installed yet — run Install first (until it reports 'installed').");
                return;
            }

            var platform = config.Value<string>("platform") ?? PlatformDetect.ActivePlatform();
            if (Array.IndexOf(Platforms, platform) < 0)
            {
                Fail(report, $"platform must be \"Android\" or \"iOS\" (got '{platform}'). Pass -buildTarget " +
                             "to Unity as well so the Editor evaluates the same platform.");
                return;
            }
            report["platform"] = platform;
            PlatformChoice.Select(platform);

            // The CAS settings asset is where the ID and formats land; create it the way Welcome does.
            if (CasSettingsWriter.EnsureAsset(platform) == null)
            {
                Fail(report, "The CAS settings asset could not be created — is CAS resolved?");
                return;
            }

            // CAS ID — validated with the platform's rule (package name on Android, App Store id on iOS).
            var casId = config.Value<string>("casId");
            if (!string.IsNullOrEmpty(casId))
            {
                var (regex, hint) = CasIdValidation.For(platform);
                if (!CasIdValidation.IsValid(casId, regex))
                {
                    Fail(report, $"casId '{casId}' is not valid for {platform} (expected like '{hint}').");
                    return;
                }
                CasIdApplier.SetManagerId(platform, casId);
                Say(report, $"CAS ID for {platform}: {casId}");
            }
            else if (string.IsNullOrEmpty(CasIdApplier.ReadExisting(platform)))
            {
                Fail(report, "casId is required: the project has no CAS ID for this platform yet.");
                return;
            }

            // Ad formats — the Placements step. Absent → the same default the screen pre-selects.
            var flags = ParseAdFormats(config["adFormats"], report);
            if (flags < 0) return;
            if (flags == 0) flags = PlacementsModel.InitialFlags(CasSettingsWriter.ReadAdFlags(platform));
            CasSettingsWriter.SetAdFlags(platform, flags);
            report["adFlags"] = flags;

            var audience = ParseAudience(config.Value<string>("audience"), report);
            if (audience < 0) return;
            if (audience == int.MaxValue) audience = CasSettingsWriter.ReadAudience(platform);
            else CasSettingsWriter.SetAudience(platform, audience);

            // Mediation solutions — as Placements → Continue: Publishing on (unless the project's own
            // Optimal/Families solution is to be kept), Tenjin MMP always.
            var target = platform == "iOS" ? BuildTarget.iOS : BuildTarget.Android;
            var keepOwn = config.Value<bool?>("keepExistingSolution") ?? false;
            var hasOwn = CasMediation.IsSolutionInstalled(target, CasMediation.OptimalAds)
                      || CasMediation.IsSolutionInstalled(target, CasMediation.FamiliesAds);
            if (!(keepOwn && hasOwn))
            {
                CasMediation.SetSolution(target, CasMediation.PublishingAds, audience, true);
                CasMediation.SetSolution(target, CasMediation.OptimalAds, audience, false);
                CasMediation.SetSolution(target, CasMediation.FamiliesAds, audience, false);
                Say(report, "Mediation solution: Publishing.");
            }
            else Say(report, "Mediation solution: kept the project's own (keepExistingSolution).");
            CasMediation.SetSolution(target, CasMediation.TenjinMmp, audience, true);

            // A key the retired wrapper stored is carried over first (empty slots only), exactly as
            // the wizard does on entering configuration.
            foreach (var p in TenjinKeyMigration.TryMigrate())
                Say(report, $"Tenjin SDK key for {p} migrated from the retired wrapper's settings asset.");

            // Tenjin SDK key — the Tenjin step; validated with the catalog's regex.
            var tenjinKey = config.Value<string>("tenjinKey");
            if (!string.IsNullOrEmpty(tenjinKey))
            {
                // Set only writes; the format check is the step screen's, so it is repeated here —
                // without it "abc-123" was stored and reported configured (QA 115).
                if (!TenjinKeySettings.IsValid(tenjinKey, TenjinKeySettings.RegexFor(platform)) ||
                    !TenjinKeySettings.Set(platform, tenjinKey))
                {
                    Fail(report, $"tenjinKey is not a valid Tenjin SDK key ({TenjinKeySettings.HintFor(platform)}).");
                    return;
                }
                Say(report, $"Tenjin SDK key for {platform} stored in Assets/Resources/PublishingSettings.asset.");
            }

            // Firebase config file — the Firebase step: validated and copied into Assets/.
            var firebase = config.Value<string>("firebaseConfig");
            if (!string.IsNullOrEmpty(firebase))
            {
                var source = Path.IsPathRooted(firebase) ? firebase : Path.Combine(ProjectRoot, firebase);
                var error = FirebaseConfigFile.ValidateAndCopy(source, "Assets");
                if (error != null)
                {
                    Fail(report, $"firebaseConfig: {error}");
                    return;
                }
                Say(report, $"Firebase config copied to Assets/{Path.GetFileName(firebase)}.");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            report["ok"] = true;
            report["state"] = "configured";
            AppendStatus(report, catalog);
        }

        /// <summary>Flags from ["banner","interstitial","rewarded","appOpen"]; 0 when absent, -1 on error.</summary>
        internal static int ParseAdFormats(JToken token, JObject report)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;
            if (token.Type != JTokenType.Array)
            {
                Fail(report, "adFormats must be an array of \"banner\" | \"interstitial\" | \"rewarded\" | \"appOpen\".");
                return -1;
            }

            var flags = 0;
            foreach (var t in token)
            {
                switch ((t.ToString() ?? "").Trim().ToLowerInvariant())
                {
                    case "banner":       flags |= AdFlagsBits.Banner; break;
                    case "interstitial": flags |= AdFlagsBits.Interstitial; break;
                    case "rewarded":     flags |= AdFlagsBits.Rewarded; break;
                    case "appopen":      flags |= AdFlagsBits.AppOpen; break;
                    default:
                        Fail(report, $"adFormats: unknown format '{t}'.");
                        return -1;
                }
            }

            if (!PlacementsModel.CanContinue(flags))
            {
                Fail(report, "adFormats must name at least one format.");
                return -1;
            }
            return flags;
        }

        /// <summary>Audience code from "mixed" | "children" | "notChildren"; int.MaxValue when absent, -1 on error.</summary>
        internal static int ParseAudience(string value, JObject report)
        {
            if (string.IsNullOrEmpty(value)) return int.MaxValue;
            switch (value.Trim().ToLowerInvariant())
            {
                case "mixed":       return CasAudience.Mixed;
                case "children":    return CasAudience.Children;
                case "notchildren": return CasAudience.NotChildren;
                default:
                    Fail(report, "audience must be \"mixed\" | \"children\" | \"notChildren\".");
                    return -1;
            }
        }

        // ── Status ───────────────────────────────────────────────────────────────────

        private static void RunStatus(JObject report)
        {
            if (!TryLoadCatalog(report, out var catalog)) return;
            report["ok"] = true;
            report["state"] = AppendStatus(report, catalog) ? "ready" : "incomplete";
        }

        /// <summary>
        /// Adds the Configuration screen's view to the report: install state per default component,
        /// the per-platform requirement cells, what an Install run would still do, and the missing
        /// Android templates. Returns true when the requested platform is fully ready.
        /// </summary>
        private static bool AppendStatus(JObject report, PackageCatalog catalog)
        {
            var platform = report.Value<string>("platform");
            if (string.IsNullOrEmpty(platform)) platform = PlatformChoice.Selected;
            if (string.IsNullOrEmpty(platform)) platform = PlatformDetect.ActivePlatform();
            if (Array.IndexOf(Platforms, platform) < 0) platform = "Android";
            report["platform"] = platform;

            ComponentStatusProvider.InvalidateCache();

            var components = new JArray();
            var allInstalled = true;
            if (ComponentStatusProvider.TryGetStatuses(out var statuses, out var statusError))
            {
                foreach (var s in statuses)
                {
                    components.Add(new JObject
                    {
                        ["id"] = s.Id,
                        ["name"] = s.DisplayName,
                        ["installed"] = s.Installed,
                        ["version"] = s.Version,
                        ["status"] = s.StatusText,
                    });
                    allInstalled &= s.Installed;
                }
            }
            else
            {
                Say(report, $"warning: component statuses unavailable: {statusError}");
                allInstalled = false;
            }
            report["components"] = components;

            var scan = ProjectScanner.Scan(catalog);
            var pending = PlanDefaults(catalog, scan, ManifestProbe.Read(), out _);
            var pendingList = new JArray();
            foreach (var copy in ComponentStatusProvider.ManualRetiredCopies(catalog))
                foreach (var path in copy.Paths)
                    pendingList.Add(Describe(new BackupAndDeletePath(path)));
            foreach (var a in pending) pendingList.Add(Describe(a));
            report["pendingInstall"] = pendingList;

            var missingTemplates = new JArray();
            foreach (var t in AndroidBuildFix.MissingNow()) missingTemplates.Add(t);
            report["androidTemplatesMissing"] = missingTemplates;

            var configuration = new JArray();
            var allConfigured = true;
            if (SetupModel.TryBuild(out var rows, out var rowsError))
            {
                foreach (var row in rows)
                {
                    var cells = new JArray();
                    foreach (var cell in platform == "iOS" ? row.IOS : row.Android)
                    {
                        cells.Add(new JObject
                        {
                            ["label"] = cell.Label,
                            ["kind"] = cell.Req?.Kind,
                            ["status"] = cell.Result?.Status.ToString(),
                            ["value"] = cell.Result?.Value,
                            ["error"] = cell.Result?.Error,
                        });
                    }

                    var configured = row.Installed
                        && SetupModel.AllConfigured(platform == "iOS" ? row.IOS : row.Android)
                        && SetupModel.AdFormatsSatisfiedFor(row, platform);
                    allConfigured &= configured;

                    configuration.Add(new JObject
                    {
                        ["id"] = row.Id,
                        ["name"] = row.Name,
                        ["installed"] = row.Installed,
                        ["configured"] = configured,
                        ["cells"] = cells,
                    });
                }
            }
            else
            {
                Say(report, $"warning: configuration status unavailable: {rowsError}");
                allConfigured = false;
            }
            report["configuration"] = configuration;

            var ready = allInstalled && allConfigured && pendingList.Count == 0 && missingTemplates.Count == 0;
            report["ready"] = ready;
            return ready;
        }
    }
}
