using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using CAS.Publishing.Installer.Common;
using UnityEngine;

namespace CAS.Publishing.Installer.Scanner
{
    /// <summary>
    /// Reads and parses the client project's Packages/manifest.json via the shared
    /// <see cref="ManifestIO"/> (tolerant of comments). A missing or malformed manifest is
    /// flagged unreadable rather than masqueraded as an empty manifest, so the scanner never
    /// produces a false "nothing installed".
    /// </summary>
    internal static class ManifestProbe
    {
        /// <summary>Reads the manifest for the current Unity project.</summary>
        public static ManifestData Read() => ReadFrom(ResolveManifestPath());

        /// <summary>Reads and maps the manifest at <paramref name="manifestPath"/>.</summary>
        internal static ManifestData ReadFrom(string manifestPath)
        {
            var r = ManifestIO.Read(manifestPath);
            switch (r.Status)
            {
                case ManifestReadStatus.FileMissing:
                    return ManifestData.Unreadable($"manifest.json not found at {manifestPath}");
                case ManifestReadStatus.ParseError:
                    return ManifestData.Unreadable($"manifest.json could not be parsed: {r.Error}");
                default:
                    return ManifestData.FromJObject(r.Root);
            }
        }

        private static string ResolveManifestPath()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(projectRoot, "Packages", "manifest.json");
        }
    }

    /// <summary>Immutable snapshot of a parsed manifest.json.</summary>
    internal sealed class ManifestData
    {
        /// <summary>package-id → version-string. Never null; empty when unreadable.</summary>
        public IReadOnlyDictionary<string, string> Dependencies { get; }

        /// <summary>Every scoped-registry entry. Never null; empty when unreadable.</summary>
        public IReadOnlyList<RegisteredScope> ScopedRegistries { get; }

        /// <summary>True when the manifest was read and parsed successfully.</summary>
        public bool Readable { get; }

        /// <summary>Human-readable reason when <see cref="Readable"/> is false; otherwise null.</summary>
        public string ReadError { get; }

        private ManifestData(
            IReadOnlyDictionary<string, string> dependencies,
            IReadOnlyList<RegisteredScope> scopedRegistries,
            bool readable,
            string readError)
        {
            Dependencies = dependencies;
            ScopedRegistries = scopedRegistries;
            Readable = readable;
            ReadError = readError;
        }

        public static ManifestData Unreadable(string error) =>
            new ManifestData(new Dictionary<string, string>(), new List<RegisteredScope>(), false, error);

        public static ManifestData FromJObject(JObject root)
        {
            var deps = new Dictionary<string, string>();
            if (root?["dependencies"] is JObject d)
                foreach (var p in d.Properties())
                    deps[p.Name] = p.Value?.Type == JTokenType.String ? p.Value.Value<string>() : p.Value?.ToString();

            var regs = new List<RegisteredScope>();
            if (root?["scopedRegistries"] is JArray arr)
                foreach (var tok in arr)
                    if (tok is JObject o)
                    {
                        var scopes = new List<string>();
                        if (o["scopes"] is JArray sa)
                            foreach (var s in sa)
                            {
                                var v = s?.Value<string>();
                                if (!string.IsNullOrEmpty(v)) scopes.Add(v);
                            }
                        regs.Add(new RegisteredScope(
                            o["name"]?.Value<string>() ?? string.Empty,
                            o["url"]?.Value<string>() ?? string.Empty,
                            scopes));
                    }

            return new ManifestData(deps, regs, true, null);
        }

        /// <summary>True if any registered scoped-registry entry declares the given scope.</summary>
        public bool HasRegisteredScope(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return false;
            foreach (var reg in ScopedRegistries)
                foreach (var s in reg.Scopes)
                    if (string.Equals(s, scope, StringComparison.OrdinalIgnoreCase))
                        return true;
            return false;
        }

        /// <summary>
        /// True when any registered scoped-registry scope COVERS <paramref name="packageId"/> under
        /// Unity's matching rule: the id equals the scope, or the scope is a dot-boundary prefix of
        /// the id ("com.example" covers "com.example.foo"; "com.ex" does not).
        /// </summary>
        public bool HasScopeCovering(string packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return false;
            foreach (var reg in ScopedRegistries)
                foreach (var s in reg.Scopes)
                {
                    if (string.IsNullOrEmpty(s)) continue;
                    if (string.Equals(packageId, s, StringComparison.OrdinalIgnoreCase)) return true;
                    if (packageId.StartsWith(s + ".", StringComparison.OrdinalIgnoreCase)) return true;
                }
            return false;
        }
    }

    /// <summary>One scoped-registry entry from manifest.json.</summary>
    internal sealed class RegisteredScope
    {
        public string Name { get; }
        public string Url { get; }
        public IReadOnlyList<string> Scopes { get; }

        public RegisteredScope(string name, string url, List<string> scopes)
        {
            Name = name;
            Url = url;
            Scopes = scopes;
        }
    }
}
