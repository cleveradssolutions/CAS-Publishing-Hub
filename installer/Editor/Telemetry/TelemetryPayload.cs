using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CAS.Publishing.Installer.Telemetry
{
    /// <summary>
    /// Pure helpers for the wire format: one event line in the queue, and the batch envelope sent
    /// to the collector. No Unity calls, so the format is unit-tested directly.
    /// </summary>
    internal static class TelemetryPayload
    {
        public const int MaxErrorLength = 1000;

        /// <summary>Environment of the sending Editor, stamped on every batch.</summary>
        internal struct Envelope
        {
            public string InstallId, SessionId, ProjectHash, CasId, Edition, InstallerVersion,
                          CatalogVersion, UnityVersion, Os, Platform, Mode,
                          UserId, UserName, OrgId, OrgName, ProjectName;
        }

        /// <summary>One queue line. Session and mode ride on the event because both can change between
        /// enqueue and send (a new wizard run, an Editor restart, a headless batch flushed later).</summary>
        public static string EventLine(string name, IDictionary<string, object> props, string sessionId, string mode,
                                       DateTime utcNow, string projectRoot = null)
        {
            var o = new JObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["name"] = name,
                ["ts"] = utcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
                ["sessionId"] = sessionId ?? "",
                ["mode"] = mode ?? "",
            };
            if (props != null && props.Count > 0)
            {
                var p = new JObject();
                foreach (var kv in props)
                {
                    if (kv.Value == null) continue;
                    // Every string goes through the sanitizer, not only "error": a value built from a
                    // manifest or an exception can carry a path or a URL with credentials.
                    p[kv.Key] = kv.Value is string str ? Sanitize(str, projectRoot) : JToken.FromObject(kv.Value);
                }
                o["props"] = p;
            }
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>The POST body: envelope fields plus the queued event lines.</summary>
        public static string Batch(Envelope env, IEnumerable<string> lines)
        {
            var events = new JArray();
            foreach (var line in lines)
            {
                try { events.Add(ParseLine(line)); }
                catch (Exception) { /* a torn line from a crash mid-write — skip it */ }
            }
            var o = new JObject
            {
                ["installId"] = env.InstallId ?? "",
                ["sessionId"] = env.SessionId ?? "",
                ["projectHash"] = env.ProjectHash ?? "",
                ["casId"] = env.CasId ?? "",
                ["edition"] = env.Edition ?? "",
                ["installerVersion"] = env.InstallerVersion ?? "",
                ["catalogVersion"] = env.CatalogVersion ?? "",
                ["unityVersion"] = env.UnityVersion ?? "",
                ["os"] = env.Os ?? "",
                ["platform"] = env.Platform ?? "",
                ["mode"] = env.Mode ?? "",
                ["userId"] = env.UserId ?? "",
                ["userName"] = env.UserName ?? "",
                ["orgId"] = env.OrgId ?? "",
                ["orgName"] = env.OrgName ?? "",
                ["projectName"] = env.ProjectName ?? "",
                ["events"] = events,
            };
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Parses a queue line keeping strings as written — Newtonsoft would otherwise turn
        /// the ISO timestamp into a DateTime and re-serialize it in another format.</summary>
        internal static JObject ParseLine(string line)
        {
            using (var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(line))
                   { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                return JObject.Load(reader);
        }

        private static readonly Regex WindowsUserPath = new Regex(@"[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex UnixUserPath = new Regex(@"/(Users|home)/[^/\s""']+", RegexOptions.Compiled);
        // user[:token]@ inside a URL — a private git mirror can be added with its access token.
        private static readonly Regex UrlCredentials = new Regex(@"(?<=://)[^/\s@""']+@", RegexOptions.Compiled);
        private static readonly Regex PlainVersion = new Regex(@"^[0-9A-Za-z.+\-]{1,64}$", RegexOptions.Compiled);

        /// <summary>
        /// Strips what identifies a person from an error text — home directories and the project's
        /// absolute path — and caps its length. Package names, versions and URLs stay: they are
        /// what makes an error actionable.
        /// </summary>
        public static string Sanitize(string text, string projectRoot = null)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var s = text;
            if (!string.IsNullOrEmpty(projectRoot))
            {
                // Both separators, whatever the root came with: Unity and UPM write "D:/Game/…",
                // .NET exceptions "D:\Game\…". Paths are case-insensitive on Windows and macOS.
                var root = projectRoot.TrimEnd('/', '\\');
                foreach (var form in new[] { root.Replace('\\', '/'), root.Replace('/', '\\') })
                    s = Regex.Replace(s, Regex.Escape(form), "<project>", RegexOptions.IgnoreCase);
            }
            s = UrlCredentials.Replace(s, "");
            s = WindowsUserPath.Replace(s, "<home>");
            s = UnixUserPath.Replace(s, "<home>");
            return s.Length > MaxErrorLength ? s.Substring(0, MaxErrorLength) : s;
        }

        /// <summary>
        /// A version fit to report: a plain version as is; for a git URL or a local path (what the
        /// manifest holds for git and file: installs) only its <c>#tag</c>, if that is a plain
        /// version; otherwise "". The URL or path itself never leaves the machine.
        /// </summary>
        public static string SafeVersion(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return "";
            var s = spec.Trim();
            if (PlainVersion.IsMatch(s)) return s;
            var hash = s.LastIndexOf('#');
            if (hash >= 0 && PlainVersion.IsMatch(s.Substring(hash + 1))) return s.Substring(hash + 1);
            return "";
        }

        /// <summary>Short stable hash, so repeat visits from one project group together.</summary>
        public static string Hash(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                var sb = new StringBuilder(16);
                for (var i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// What the collector's answer means for the batch: drop it (delivered, or rejected for good)
        /// or keep it for the next attempt (network down, server busy).
        /// </summary>
        public static bool ShouldDrop(long httpStatus)
        {
            if (httpStatus >= 200 && httpStatus < 300) return true;
            if (httpStatus == 408 || httpStatus == 429) return false;
            return httpStatus >= 400 && httpStatus < 500;
        }
    }
}
