using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using CAS.Publishing.Installer.Migrator;
using CAS.Publishing.Installer.Telemetry;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// The wizard's install events for agent mode: <c>install_started</c>, <c>component_installed</c>,
    /// <c>component_failed</c>, <c>install_completed</c>. They carry <c>mode: headless</c>, which is
    /// how the dashboard tells an agent install from a clicked one.
    ///
    /// An agent install spans several Editor processes (Install writes the manifest, Unity resolves
    /// on its next launch, Install runs again until "installed"), so nothing in SessionState
    /// survives between the passes. What this install added and when it began is kept in a small
    /// file under Library/ instead, and the outcome per package is reported on the pass that finds
    /// nothing left to install.
    /// </summary>
    internal static class HeadlessInstallTelemetry
    {
        /// <summary>An install left unfinished longer than this is abandoned: the next pass starts a new one.</summary>
        internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

        internal static Action<string, IDictionary<string, object>> Track = HubTelemetry.Track;

        internal static string DefaultPath(string projectRoot) =>
            Path.Combine(projectRoot, "Library", "CASHub", "headless-install.json");

        /// <summary>A plan was written to the manifest; Unity resolves it on the next launch.</summary>
        internal static void Applied(string path, IReadOnlyList<MigrationAction> plan, int warnings, DateTime now)
        {
            try
            {
                var state = StartedOrLoad(path, plan, warnings, now);
                foreach (var id in PackageIds(plan))
                    if (!state.Packages.Contains(id)) state.Packages.Add(id);
                Save(path, state);
            }
            catch (Exception) { /* never let telemetry break the run */ }
        }

        /// <summary>The plan could not be applied; the manifest may be partially written.</summary>
        internal static void ApplyFailed(string path, IReadOnlyList<MigrationAction> plan, int warnings,
            IReadOnlyList<string> failures, DateTime now)
        {
            try
            {
                var state = StartedOrLoad(path, plan, warnings, now);
                var error = failures != null && failures.Count > 0 ? string.Join("; ", failures) : "apply failed";
                var ids = PackageIds(plan);
                // A run that only deletes files names no package; the failure still has to show up.
                if (ids.Count == 0) ids.Add("");
                foreach (var id in ids)
                    Track("component_failed", new Dictionary<string, object>
                    {
                        ["package"] = id,
                        ["durationMs"] = Since(state, now),
                        ["error"] = error,
                    });
                Save(path, state);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// The pass found nothing left to install. Reports each package this install added — resolved
        /// by Unity or not — and closes the install. Nothing is reported when no agent install was open.
        /// </summary>
        internal static void Finished(string path, Func<string, bool> isPresent, DateTime now)
        {
            try
            {
                var state = Load(path, now);
                if (state == null) return;

                var failed = 0;
                foreach (var id in state.Packages)
                {
                    if (isPresent(id))
                    {
                        Track("component_installed", new Dictionary<string, object>
                        {
                            ["package"] = id,
                            ["durationMs"] = Since(state, now),
                        });
                        continue;
                    }
                    failed++;
                    Track("component_failed", new Dictionary<string, object>
                    {
                        ["package"] = id,
                        ["durationMs"] = Since(state, now),
                        ["error"] = "in Packages/manifest.json but not resolved by Unity",
                    });
                }

                Track("install_completed", new Dictionary<string, object>
                {
                    ["durationMs"] = Since(state, now),
                    ["components"] = state.Packages.Count,
                    ["failed"] = failed,
                });
                File.Delete(path);
            }
            catch (Exception) { }
        }

        // ── State ────────────────────────────────────────────────────────────────

        internal sealed class State
        {
            public DateTime Started;
            public readonly List<string> Packages = new List<string>();
        }

        private static State StartedOrLoad(string path, IReadOnlyList<MigrationAction> plan, int warnings, DateTime now)
        {
            var state = Load(path, now);
            if (state != null) return state;

            Track("install_started", new Dictionary<string, object>
            {
                ["actions"] = plan.Count,
                ["warnings"] = warnings,
            });
            return new State { Started = now };
        }

        /// <summary>The open install, or null when there is none (no file, unreadable, or stale).</summary>
        internal static State Load(string path, DateTime now)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var o = JObject.Parse(File.ReadAllText(path));
                var state = new State { Started = new DateTime(o.Value<long>("startedTicks"), DateTimeKind.Utc) };
                if (now - state.Started > StaleAfter || state.Started > now) return null;
                if (o["packages"] is JArray packages)
                    foreach (var p in packages) state.Packages.Add((string)p);
                return state;
            }
            catch (Exception) { return null; }
        }

        private static void Save(string path, State state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, new JObject
            {
                ["startedTicks"] = state.Started.Ticks,
                ["packages"] = new JArray(state.Packages),
            }.ToString());
        }

        private static long Since(State state, DateTime now) => (long)(now - state.Started).TotalMilliseconds;

        /// <summary>The packages a plan adds or moves to another version; removals and registry edits aren't installs.</summary>
        internal static List<string> PackageIds(IReadOnlyList<MigrationAction> plan)
        {
            var ids = new List<string>();
            foreach (var action in plan)
            {
                string id;
                switch (action)
                {
                    case AddPackage add:           id = add.Id; break;
                    case AddGitPackage git:        id = git.Id; break;
                    case UpdatePackageVersion upd: id = upd.Id; break;
                    default: continue;
                }
                if (!ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }
    }
}
