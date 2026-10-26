using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CAS.Publishing.Installer.Catalog;
using CAS.Publishing.Installer.Common;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Networking;

namespace CAS.Publishing.Installer.Telemetry
{
    /// <summary>
    /// Usage events from the wizard and the headless agent mode: which steps people reach, where
    /// they stop, what fails — attributed to the app (CAS ID) and the Unity account signed in to the
    /// Editor, so support can find a specific client's failed install. <see cref="Track"/> only appends to a queue file under Library/;
    /// a background tick sends it in batches. The catalog's <c>telemetry</c> block decides where to and
    /// whether at all (absent or <c>enabled: false</c> → nothing leaves the machine). Clients get the
    /// Hub under contract, so there is no per-user opt-out.
    ///
    /// Never sent: Tenjin keys, Firebase config contents, file paths (stripped from error texts).
    /// </summary>
    [InitializeOnLoad]
    internal static class HubTelemetry
    {
        private const string SessionKey = "CAS.Publishing.Installer.Telemetry.SessionId";
        private const string LastSendKey = "CAS.Publishing.Installer.Telemetry.LastSendAttempt";
        private static string InstallIdKey => "CAS.Publishing.Installer.Telemetry.InstallId:" + Application.dataPath;

        /// <summary>Local-testing override: point the wizard at a collector without touching the catalog.
        /// The env var wins over the EditorPrefs value the Hub Debug menu sets.</summary>
        internal const string EndpointEnv = "CAS_HUB_TELEMETRY_URL";
        private const string EndpointPrefKey = "CAS.Publishing.Installer.Telemetry.EndpointOverride";

        private const int BatchSize = 200;
        private const double SendIntervalSeconds = 15;
        private const int TimeoutSeconds = 10;

        private static readonly TelemetryQueue Queue =
            new TelemetryQueue(Path.Combine(ProjectRoot, "Library", "CASHub", "telemetry-queue.jsonl"));

        private static UnityWebRequest _inFlight;
        private static int _inFlightCount;

        /// <summary>"wizard" or "headless"; the agent-mode runner switches it before its first event.</summary>
        internal static string Mode = "wizard";

        /// <summary>
        /// The CAS manager id for a platform ("Android"/"iOS"), set by the wizard assembly (which
        /// owns the CAS settings reader). Null or empty result → the bundle id is reported instead.
        /// </summary>
        internal static Func<string, string> CasIdProvider;

        private static bool _clearedWhileOff;

        static HubTelemetry()
        {
            // Plain batch runs (CI builds, other tools' -executeMethod) send nothing on their own;
            // the headless hub flushes explicitly before it exits.
            if (Application.isBatchMode) return;
            EditorApplication.update += Tick;
        }

        // ── Endpoint ─────────────────────────────────────────────────────────────

        internal static string EndpointOverride
        {
            get
            {
                var env = Environment.GetEnvironmentVariable(EndpointEnv);
                return !string.IsNullOrEmpty(env) ? env : EditorPrefs.GetString(EndpointPrefKey, "");
            }
            set
            {
                if (string.IsNullOrEmpty(value)) EditorPrefs.DeleteKey(EndpointPrefKey);
                else EditorPrefs.SetString(EndpointPrefKey, value);
                _clearedWhileOff = false;
            }
        }

        internal enum Gate { On, Off, Unknown }

        /// <summary>
        /// Whether events may be recorded at all. Unknown while the catalog is not installed yet (the
        /// first run's first screens): those are queued and go out once a catalog turns telemetry on.
        /// Off — the catalog has no <c>telemetry</c> block or <c>enabled: false</c>: nothing is
        /// recorded, and what was queued before that was known is deleted, so it can't be sent later
        /// by a catalog that switches telemetry on.
        /// </summary>
        internal static Gate CurrentGate()
        {
            if (!string.IsNullOrEmpty(EndpointOverride)) return Gate.On;
            var load = CatalogLoader.Load();
            if (load.Status != CatalogLoadStatus.Ok) return Gate.Unknown;
            var t = load.Catalog?.Telemetry;
            return t != null && t.Enabled && !string.IsNullOrEmpty(t.Endpoint) ? Gate.On : Gate.Off;
        }

        /// <summary>Where events go right now, or null when nothing should be sent.</summary>
        internal static TelemetrySettings ActiveSettings()
        {
            var overrideUrl = EndpointOverride;
            if (!string.IsNullOrEmpty(overrideUrl))
                return new TelemetrySettings { Endpoint = overrideUrl, Enabled = true };

            var load = CatalogLoader.Load();
            var t = load.Status == CatalogLoadStatus.Ok ? load.Catalog?.Telemetry : null;
            return t != null && t.Enabled && !string.IsNullOrEmpty(t.Endpoint) ? t : null;
        }

        // ── Session ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Current wizard session. SessionState keeps it across the domain reloads an install causes,
        /// so one install run stays one session; an Editor restart starts a new one.
        /// </summary>
        internal static string SessionId
        {
            get
            {
                var id = SessionState.GetString(SessionKey, "");
                if (string.IsNullOrEmpty(id)) id = NewSession();
                return id;
            }
        }

        internal static string NewSession()
        {
            var id = Guid.NewGuid().ToString();
            SessionState.SetString(SessionKey, id);
            return id;
        }

        // ── Recording ────────────────────────────────────────────────────────────

        internal static void Track(string name, IDictionary<string, object> props = null)
        {
            try
            {
                if (CurrentGate() == Gate.Off)
                {
                    if (!_clearedWhileOff) { Queue.Clear(); _clearedWhileOff = true; }
                    return;
                }
                Queue.Append(TelemetryPayload.EventLine(name, props, SessionId, Mode, DateTime.UtcNow, ProjectRoot));
            }
            catch (Exception) { /* never let telemetry break the caller */ }
        }

        internal static void Track(string name, string key, object value) =>
            Track(name, new Dictionary<string, object> { [key] = value });

        // Durations across domain reloads: the start time is parked in SessionState.
        internal static void StartTimer(string key) =>
            SessionState.SetString("CAS.Publishing.Installer.Telemetry.Timer." + key, DateTime.UtcNow.Ticks.ToString());

        internal static bool HasTimer(string key) =>
            !string.IsNullOrEmpty(SessionState.GetString("CAS.Publishing.Installer.Telemetry.Timer." + key, ""));

        /// <summary>Milliseconds since <see cref="StartTimer"/>, or 0 when no timer was started.</summary>
        internal static long StopTimer(string key)
        {
            var k = "CAS.Publishing.Installer.Telemetry.Timer." + key;
            var raw = SessionState.GetString(k, "");
            SessionState.EraseString(k);
            return long.TryParse(raw, out var ticks) ? (long)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - ticks).TotalMilliseconds : 0;
        }

        /// <summary>True the first time it is asked for <paramref name="key"/> in this session — for
        /// events that must not repeat on every screen re-entry (re-scans, reload restores).</summary>
        internal static bool Once(string key)
        {
            var k = "CAS.Publishing.Installer.Telemetry.Once." + key + ":" + SessionId;
            if (SessionState.GetBool(k, false)) return false;
            SessionState.SetBool(k, true);
            return true;
        }

        // ── Sending ──────────────────────────────────────────────────────────────

        // Runs every Editor frame: an exception here (a malformed endpoint or key from the catalog)
        // would be logged on every send attempt, in every client — swallow it, drop the request.
        private static void Tick()
        {
            try { TickUnsafe(); }
            catch (Exception) { Abandon(); }
        }

        private static void TickUnsafe()
        {
            if (_inFlight != null)
            {
                if (_inFlight.isDone) Complete();
                return;
            }

            // timeSinceStartup restarts with the Editor, so a stale value from a previous run is
            // simply "long ago" — a send is due right away.
            var now = EditorApplication.timeSinceStartup;
            var last = SessionState.GetFloat(LastSendKey, -1000f);
            if (now >= last && now - last < SendIntervalSeconds) return;
            SessionState.SetFloat(LastSendKey, (float)now);

            Begin();
        }

        private static bool Begin()
        {
            var settings = ActiveSettings();
            if (settings == null) return false;
            var lines = Queue.Peek(BatchSize);
            if (lines.Count == 0) return false;

            var body = TelemetryPayload.Batch(BuildEnvelope(settings), lines);
            var req = new UnityWebRequest(settings.Endpoint.TrimEnd('/') + "/v1/events", "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = TimeoutSeconds,
            };
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(settings.Key)) req.SetRequestHeader("X-Hub-Key", settings.Key);

            _inFlight = req;
            _inFlightCount = lines.Count;
            req.SendWebRequest();
            return true;
        }

        private static void Complete()
        {
            var req = _inFlight;
            _inFlight = null;
            try
            {
                // A network error has responseCode 0 → kept for the next tick. An event that reached
                // the server twice (reload mid-request) is collapsed there by its id.
                if (TelemetryPayload.ShouldDrop(req.responseCode))
                    Queue.Drop(_inFlightCount);
            }
            finally { req.Dispose(); }
        }

        /// <summary>
        /// Sends what is queued and waits for it, up to <paramref name="timeoutSeconds"/>. For the
        /// headless runner, which exits the Editor right after its work. Whatever doesn't make it
        /// stays queued for the next run.
        /// </summary>
        internal static void FlushBlocking(double timeoutSeconds = 5)
        {
            // The headless runner calls this right before EditorApplication.Exit: an exception must
            // not skip the exit or turn an "ok" run into a failure code.
            try { FlushBlockingUnsafe(timeoutSeconds); }
            catch (Exception) { Abandon(); }
        }

        private static void Abandon()
        {
            try { _inFlight?.Abort(); _inFlight?.Dispose(); }
            catch (Exception) { }
            _inFlight = null;
        }

        private static void FlushBlockingUnsafe(double timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            for (var batch = 0; batch < 5 && DateTime.UtcNow < deadline; batch++)
            {
                if (_inFlight == null && !Begin()) return;
                while (!_inFlight.isDone && DateTime.UtcNow < deadline)
                    System.Threading.Thread.Sleep(20);
                if (!_inFlight.isDone) { _inFlight.Abort(); _inFlight.Dispose(); _inFlight = null; return; }
                Complete();
            }
        }

        // ── Envelope ─────────────────────────────────────────────────────────────

        private static TelemetryPayload.Envelope BuildEnvelope(TelemetrySettings settings)
        {
            var load = CatalogLoader.Load();
            var target = EditorUserBuildSettings.activeBuildTarget;
            var platform = target == BuildTarget.Android ? "Android" : target == BuildTarget.iOS ? "iOS" : target.ToString();
            return new TelemetryPayload.Envelope
            {
                InstallId = InstallId,
                SessionId = SessionId,
                ProjectHash = TelemetryPayload.Hash(Application.companyName + "/" + Application.productName),
                ProjectName = Application.companyName + "/" + Application.productName,
                CasId = CasIdFor(target, platform),
                Edition = EditionInfo.EditionId,
                InstallerVersion = InstallerVersion,
                CatalogVersion = load.Status == CatalogLoadStatus.Ok ? load.Catalog?.CatalogVersion : "",
                UnityVersion = Application.unityVersion,
                Os = SystemInfo.operatingSystem,
                Platform = platform,
                Mode = Mode,
                // The Unity account signed in to the Editor. Empty when nobody is signed in (offline,
                // some batch runs) — the CAS ID and installId still tie the events together.
                UserId = SignedIn(CloudProjectSettings.userId),
                UserName = SignedIn(CloudProjectSettings.userName),
                OrgId = SignedIn(CloudProjectSettings.organizationId),
                OrgName = SignedIn(CloudProjectSettings.organizationName),
            };
        }

        /// <summary>Random per-project, per-machine id: separates two people on one Unity account and
        /// one person's several projects.</summary>
        private static string InstallId
        {
            get
            {
                var id = EditorPrefs.GetString(InstallIdKey, "");
                if (string.IsNullOrEmpty(id))
                {
                    id = Guid.NewGuid().ToString();
                    EditorPrefs.SetString(InstallIdKey, id);
                }
                return id;
            }
        }

        // Unity reports "anonymous" (batch mode, signed out) instead of an empty value; sending it
        // would merge every signed-out machine into one "user".
        private static string SignedIn(string value) =>
            string.IsNullOrEmpty(value) || value == "anonymous" ? "" : value;

        // The id the CAS dashboard knows the app by — on iOS the App Store id, not the bundle id.
        // Falls back to the bundle id while CAS holds none (or the wizard assembly isn't loaded).
        private static string CasIdFor(BuildTarget target, string platform)
        {
            try
            {
                var id = CasIdProvider?.Invoke(target == BuildTarget.iOS ? "iOS" : "Android");
                if (!string.IsNullOrEmpty(id)) return id;
            }
            catch (Exception) { }
            var named = target == BuildTarget.iOS ? NamedBuildTarget.iOS : NamedBuildTarget.Android;
            return PlayerSettings.GetApplicationIdentifier(named);
        }

        private static string InstallerVersion
        {
            get
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(HubTelemetry).Assembly);
                return info != null && !string.IsNullOrEmpty(info.version) ? info.version : "0.0.0";
            }
        }

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);
    }
}
