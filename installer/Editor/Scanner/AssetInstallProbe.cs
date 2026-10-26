using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CAS.Publishing.Installer.Catalog;
using UnityEngine;

namespace CAS.Publishing.Installer.Scanner
{
    /// <summary>
    /// Detects an SDK installed OUTSIDE UPM (e.g. via a .unitypackage or manual drop).
    ///
    /// Presence is decided by REFLECTION over loaded types (<see cref="CollectLoadedIdentifiers"/>
    /// + <see cref="IsPresentInIdentifiers"/>): this catches every install shape — asmdef, precompiled
    /// DLL, AND raw .cs scripts with no asmdef (they compile into Assembly-CSharp). The matched
    /// identifier is a type's namespace, or — for types in the GLOBAL namespace (e.g. Tenjin's
    /// <c>Tenjin</c>/<c>BaseTenjin</c> classes) — its simple name, so namespace-less SDKs are still
    /// seen. UPM packages also load types, but the classifier only consults this when the package is
    /// ABSENT from the manifest, so a positive means a non-UPM copy in the project.
    ///
    /// Read-only; never throws.
    /// </summary>
    internal static class AssetInstallProbe
    {
        // ── Presence (reflection, per scan) ──────────────────────────────────

        /// <summary>
        /// The marker-matching identifier for a loaded type: its namespace when it has one, otherwise
        /// its simple name. SDKs whose public types live in the GLOBAL namespace (e.g. Tenjin's
        /// <c>Tenjin</c> / <c>BaseTenjin</c> classes) have no namespace to match against — using the
        /// simple name there lets a "Tenjin" marker still detect them. Namespaced types keep using the
        /// namespace, so detection of CAS / Firebase (already namespaced) is unchanged.
        /// </summary>
        internal static string TypeIdentifier(string ns, string simpleName)
            => string.IsNullOrEmpty(ns) ? simpleName : ns;

        /// <summary>
        /// Collects marker-matching identifiers for all loaded types — namespaces for namespaced types,
        /// simple names for global-namespace types (see <see cref="TypeIdentifier"/>). Tolerant of
        /// partially-loaded assemblies.
        /// </summary>
        public static HashSet<string> CollectLoadedIdentifiers()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    var id = TypeIdentifier(t.Namespace, t.Name);
                    if (!string.IsNullOrEmpty(id)) set.Add(id);
                }
            }
            return set;
        }

        /// <summary>True when any loaded identifier matches a marker (case-insensitive substring).</summary>
        public static bool IsPresentInIdentifiers(IEnumerable<string> identifiers, IReadOnlyList<string> markers)
        {
            if (identifiers == null || markers == null || markers.Count == 0) return false;
            foreach (var id in identifiers)
                if (MatchesAny(id, markers)) return true;
            return false;
        }

        /// <summary>
        /// Disk-presence fallback for out-of-UPM detection: true when one of <paramref name="existingRoots"/>
        /// (already filtered to those that exist, e.g. from <see cref="AssetProbe.FindExisting"/>) is an
        /// SDK-IDENTITY folder — its last path segment matches one of <paramref name="markers"/>. Gating by
        /// marker (not "any root exists") is deliberate: <see cref="Catalog.ExternalRecord.AssetRoots"/> also
        /// lists SHARED satellite folders (ExternalDependencyManager, PlayServicesResolver) that other SDKs
        /// drop too — those must never mark THIS SDK present. Catches a manual (.unitypackage) install whose
        /// types aren't loaded (e.g. the project doesn't compile), where reflection alone is blind. Never throws.
        /// </summary>
        internal static bool AnyIdentityRootExists(
            IReadOnlyList<string> existingRoots, IReadOnlyList<string> markers,
            Func<string, bool> hasSdkPayload)
        {
            if (existingRoots == null || markers == null || markers.Count == 0) return false;
            foreach (var root in existingRoots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                var normalized = root.Replace('\\', '/').TrimEnd('/');
                var slash = normalized.LastIndexOf('/');
                var segment = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
                if (!MatchesAny(segment, markers)) continue;
                // The name is only half the signal — the folder must still hold the SDK. Settings
                // assets survive an uninstall (CAS keeps its own in Assets/ even for a UPM install),
                // and a folder holding nothing else is residue, not an install.
                if (hasSdkPayload == null || hasSdkPayload(root)) return true;
            }
            return false;
        }

        /// <summary>
        /// True when the files under an identity root are the SDK itself rather than what it leaves
        /// behind. Settings assets, their <c>.meta</c> files and the EDM dependency manifests survive
        /// an uninstall — CAS keeps its settings in <c>Assets/</c> even for a UPM install — so a folder
        /// holding only those is residue, not an install. Any other file counts as payload: erring
        /// towards "installed" keeps a genuinely manual copy from being duplicated.
        /// </summary>
        internal static bool LooksLikeSdkPayload(IEnumerable<string> filesUnderRoot)
        {
            if (filesUnderRoot == null) return false;
            foreach (var file in filesUnderRoot)
            {
                if (string.IsNullOrEmpty(file)) continue;
                var ext = Path.GetExtension(file);
                if (string.IsNullOrEmpty(ext)) return true; // extensionless — not one of the leftovers
                var residue = false;
                foreach (var known in ResidueExtensions)
                    if (string.Equals(ext, known, StringComparison.OrdinalIgnoreCase)) { residue = true; break; }
                if (!residue) return true;
            }
            return false;
        }

        /// <summary>What an uninstalled SDK leaves behind: settings assets and their <c>.meta</c>
        /// files, the EDM dependency manifests, docs and images. Anything else is treated as the SDK
        /// itself.</summary>
        private static readonly string[] ResidueExtensions =
        {
            ".asset", ".meta", ".json", ".xml", ".txt", ".md",
            ".png", ".jpg", ".jpeg", ".gif", ".prefab", ".unity",
        };

        /// <summary>Disk-backed <see cref="LooksLikeSdkPayload"/> for one Assets-relative root — the
        /// probe <see cref="AnyIdentityRootExists"/> takes in production. Never throws.</summary>
        internal static bool RootHasSdkPayload(string relativeRoot)
        {
            if (string.IsNullOrEmpty(relativeRoot)) return false;
            try
            {
                var dir = Path.GetFullPath(Path.Combine(Application.dataPath, relativeRoot));
                if (!Directory.Exists(dir)) return File.Exists(dir);
                return LooksLikeSdkPayload(Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories));
            }
            catch { return true; } // unreadable → keep the old, install-assuming answer
        }

        /// <summary>
        /// Reads a static string version member <c>typeFullName.memberName</c> (field, property, or const)
        /// from any loaded assembly via reflection — i.e. the version a manually-installed SDK reports at
        /// runtime. Used to detect a downgrade before migrating that manual copy to a pinned UPM version.
        /// Returns null when not configured, the type/member is absent, or the value isn't a non-empty
        /// string. Tolerant of partially-loaded assemblies; never throws.
        /// </summary>
        public static string ReadStaticVersion(string typeFullName, string memberName)
        {
            if (string.IsNullOrEmpty(typeFullName) || string.IsNullOrEmpty(memberName)) return null;

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Static | BindingFlags.FlattenHierarchy;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(typeFullName, throwOnError: false); }
                catch { continue; }
                if (t == null) continue;

                try
                {
                    var fi = t.GetField(memberName, Flags);     // covers const + static field
                    if (fi != null && fi.GetValue(null) is string fv && !string.IsNullOrEmpty(fv)) return fv;

                    var pi = t.GetProperty(memberName, Flags);
                    if (pi != null && pi.GetValue(null) is string pv && !string.IsNullOrEmpty(pv)) return pv;
                }
                catch { /* keep scanning other assemblies */ }
            }
            return null;
        }

        /// <summary>
        /// Read-only: relative-to-Assets paths of files under <paramref name="relativeDir"/> (e.g.
        /// "Plugins") whose file name matches a marker. Used to SURFACE this SDK's files left in a
        /// SHARED folder that the installer must NOT auto-delete (other SDKs may share it), so the
        /// user can prune them by hand. <c>.meta</c> files are skipped; capped at <paramref name="max"/>.
        /// Never throws.
        /// </summary>
        public static List<string> FindLooseFiles(IReadOnlyList<string> markers, string relativeDir, int max = 25)
        {
            var hits = new List<string>();
            if (markers == null || markers.Count == 0 || string.IsNullOrEmpty(relativeDir)) return hits;

            var assetsRoot = Application.dataPath;
            var dir = Path.GetFullPath(Path.Combine(assetsRoot, relativeDir));
            if (!Directory.Exists(dir)) return hits;

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetExtension(file), ".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!MatchesAny(Path.GetFileNameWithoutExtension(file), markers)) continue;

                    var full = Path.GetFullPath(file);
                    if (full.Length <= assetsRoot.Length) continue;
                    var rel = full.Substring(assetsRoot.Length).Replace('\\', '/').TrimStart('/');
                    hits.Add(rel);
                    if (hits.Count >= max) break;
                }
            }
            catch
            {
                // Read-only probe: any IO failure → return what we found so far.
            }
            return hits;
        }

        // ── Scattered legacy files (name + content signature) ───────────────

        /// <summary>
        /// True when <paramref name="fileName"/> equals the signature's <see cref="LegacyAssetFile.Name"/>
        /// (case-insensitive) AND <paramref name="content"/> contains EVERY one of its
        /// <see cref="LegacyAssetFile.Contains"/> markers (ordinal, they're code identifiers). Name match
        /// alone is never enough — a user's same-named file without the markers is rejected. An empty
        /// markers list falls back to name-only (discouraged). Never throws.
        /// </summary>
        internal static bool MatchesSignature(string fileName, string content, LegacyAssetFile signature)
        {
            if (signature == null || string.IsNullOrEmpty(signature.Name)) return false;
            if (!string.Equals(fileName, signature.Name, StringComparison.OrdinalIgnoreCase)) return false;
            if (signature.Contains == null || signature.Contains.Count == 0) return true;
            if (content == null) return false;
            foreach (var marker in signature.Contains)
            {
                if (string.IsNullOrEmpty(marker)) continue;
                if (content.IndexOf(marker, StringComparison.Ordinal) < 0) return false;
            }
            return true;
        }

        /// <summary>
        /// Read-only: relative-to-Assets paths of files anywhere under Assets/ that match one of
        /// <paramref name="signatures"/> by name + content (see <see cref="MatchesSignature"/>), EXCLUDING
        /// anything already under <paramref name="excludeRoots"/> (those are removed as whole folders). Used
        /// to find SDK files a manual install scattered into shared folders (e.g. Assets/Editor) so migration
        /// can offer to remove them. <c>.meta</c> files are skipped (Unity deletes them with the asset).
        /// Capped at <paramref name="max"/>. Never throws.
        /// </summary>
        public static List<string> FindSignatureFiles(
            IReadOnlyList<LegacyAssetFile> signatures,
            IReadOnlyList<string> excludeRoots,
            int max = 50)
        {
            var hits = new List<string>();
            if (signatures == null || signatures.Count == 0) return hits;

            var assetsRoot = Application.dataPath.Replace('\\', '/').TrimEnd('/');
            if (!Directory.Exists(assetsRoot)) return hits;

            var excluded = new List<string>();
            if (excludeRoots != null)
                foreach (var r in excludeRoots)
                    if (!string.IsNullOrEmpty(r))
                        excluded.Add(Path.GetFullPath(Path.Combine(assetsRoot, r)).Replace('\\', '/').TrimEnd('/'));

            try
            {
                foreach (var file in Directory.EnumerateFiles(assetsRoot, "*.*", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetExtension(file), ".meta", StringComparison.OrdinalIgnoreCase)) continue;

                    var fileName = Path.GetFileName(file);
                    var nameMatch = false;
                    foreach (var sig in signatures)
                        if (sig != null && string.Equals(fileName, sig.Name, StringComparison.OrdinalIgnoreCase)) { nameMatch = true; break; }
                    if (!nameMatch) continue;

                    var fullFwd = Path.GetFullPath(file).Replace('\\', '/');

                    var inExcluded = false;
                    foreach (var ex in excluded)
                        if (fullFwd.Equals(ex, StringComparison.OrdinalIgnoreCase) ||
                            fullFwd.StartsWith(ex + "/", StringComparison.OrdinalIgnoreCase)) { inExcluded = true; break; }
                    if (inExcluded) continue;

                    string content;
                    try { content = File.ReadAllText(file); } catch { continue; }

                    foreach (var sig in signatures)
                    {
                        if (!MatchesSignature(fileName, content, sig)) continue;
                        if (fullFwd.Length <= assetsRoot.Length) break;
                        hits.Add(fullFwd.Substring(assetsRoot.Length).TrimStart('/'));
                        break;
                    }
                    if (hits.Count >= max) break;
                }
            }
            catch { /* read-only probe: return what we found */ }
            return hits;
        }

        /// <summary>
        /// Case-insensitive file-name match supporting at most one '*' wildcard (prefix*suffix).
        /// Used for precise Plugins-lib targeting (e.g. "libFirebaseCpp*"). Pure/testable.
        /// </summary>
        public static bool MatchesFilePattern(string fileName, string pattern)
        {
            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(pattern)) return false;
            var f = fileName.ToLowerInvariant();
            var p = pattern.ToLowerInvariant();
            var star = p.IndexOf('*');
            if (star < 0) return f == p;
            var prefix = p.Substring(0, star);
            var suffix = p.Substring(star + 1);
            return f.Length >= prefix.Length + suffix.Length
                && f.StartsWith(prefix, System.StringComparison.Ordinal)
                && f.EndsWith(suffix, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Finds files under <paramref name="relativeDir"/> (default "Plugins") whose file name matches
        /// any of <paramref name="patterns"/> (exact or single-'*' glob). Returns Assets-relative paths.
        /// Precise by design — patterns must be specific lib names, not broad globs.
        /// </summary>
        public static List<string> FindPluginFiles(IReadOnlyList<string> patterns, string relativeDir = "Plugins", int max = 50)
        {
            var hits = new List<string>();
            if (patterns == null || patterns.Count == 0 || string.IsNullOrEmpty(relativeDir)) return hits;

            // Safety: this probe DRIVES deletion, so never act on an over-broad pattern. Keep exact
            // names and wildcards with a substantial literal prefix; drop the rest (e.g. "*", "*.a",
            // "lib*") with a warning, so a misconfigured catalog can't mass-delete shared Plugins files.
            var safe = new List<string>();
            foreach (var pat in patterns)
            {
                if (IsSafePluginPattern(pat)) safe.Add(pat);
                else Debug.LogWarning("[CAS Hub] Ignoring over-broad pluginFiles pattern '" + pat +
                                      "' — use an exact file name or a wildcard with a >=5-char prefix.");
            }
            if (safe.Count == 0) return hits;

            var assetsRoot = Application.dataPath;
            var dir = Path.GetFullPath(Path.Combine(assetsRoot, relativeDir));
            if (!Directory.Exists(dir)) return hits;

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetExtension(file), ".meta", System.StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileName(file);
                    var matched = false;
                    foreach (var pat in safe) if (MatchesFilePattern(name, pat)) { matched = true; break; }
                    if (!matched) continue;

                    var full = Path.GetFullPath(file);
                    if (full.Length <= assetsRoot.Length) continue;
                    var rel = full.Substring(assetsRoot.Length).Replace('\\', '/').TrimStart('/');
                    hits.Add(rel);
                    if (hits.Count >= max) break;
                }
            }
            catch { /* read-only probe: return what we have */ }
            return hits;
        }

        /// <summary>
        /// A <c>pluginFiles</c> pattern is safe to DRIVE deletion when it's an exact name, or a single
        /// '*' wildcard whose literal prefix is at least 5 chars — so "*", "*.a", "lib*", or a two-star
        /// pattern can never mass-match the shared Plugins folder. Pure/testable.
        /// </summary>
        internal static bool IsSafePluginPattern(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            var star = pattern.IndexOf('*');
            if (star < 0) return true;                              // exact name
            if (pattern.IndexOf('*', star + 1) >= 0) return false;  // more than one '*'
            return star >= 5;                                       // literal prefix length before '*'
        }

        // ── Shared helpers ───────────────────────────────────────────────────

        /// <summary>Case-insensitive substring match of <paramref name="value"/> against any marker.</summary>
        internal static bool MatchesAny(string value, IReadOnlyList<string> markers)
        {
            if (string.IsNullOrEmpty(value) || markers == null) return false;
            foreach (var m in markers)
            {
                if (string.IsNullOrEmpty(m)) continue;
                if (value.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

    }
}
