using System;
using System.Collections.Generic;
using System.IO;
using CAS.Publishing.Installer.Catalog;
using UnityEngine;

namespace CAS.Publishing.Installer.Scanner
{
    /// <summary>
    /// Read-only: which of the game's own scripts still reference a package the catalog retires.
    ///
    /// Removing a retired package (the Tenjin attribution wrapper, say) is right by the catalog and
    /// wrong for the game the moment its code calls into it — the project stops compiling and the
    /// Hub has said nothing. The catalog names the identifiers such code would contain
    /// (<see cref="UninstallRecord.CodeMarkers"/>: a namespace, a few type names); this probe finds
    /// the files under <c>Assets/</c> that mention them, so the removal can be confirmed with the
    /// list in view (and the agent-mode report can hand it to whoever fixes the code). Never throws,
    /// never touches a file.
    /// </summary>
    public static class RetiredCodeProbe
    {
        /// <summary>One retired package and the project files that still mention it.</summary>
        public sealed class Hit
        {
            public string PackageId;
            public List<string> Files = new List<string>();   // relative to the project root
        }

        /// <summary>
        /// For every id in <paramref name="removedIds"/> covered by an uninstall record with code
        /// markers, the files under Assets/ that reference those markers. Ids without markers or
        /// without matching files produce no hit.
        /// </summary>
        public static List<Hit> FindReferences(PackageCatalog catalog, IEnumerable<string> removedIds, int maxFiles = 25,
            IReadOnlyCollection<string> excludeAssetPaths = null)
        {
            var hits = new List<Hit>();
            if (catalog?.Uninstall == null || removedIds == null) return hits;

            foreach (var id in removedIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                foreach (var record in catalog.Uninstall)
                {
                    if (record?.LegacyNpmIds == null || record.CodeMarkers == null || record.CodeMarkers.Count == 0) continue;
                    if (!record.LegacyNpmIds.Contains(id)) continue;

                    var files = FindFiles(record.CodeMarkers, maxFiles, excludeAssetPaths);
                    if (files.Count > 0) hits.Add(new Hit { PackageId = id, Files = files });
                    break;
                }
            }
            return hits;
        }

        /// <summary>
        /// Files under Assets/ (scripts and assembly definitions) containing any of the markers.
        /// A plain substring match: the markers are namespaces and type names, distinctive enough
        /// that a false hit only adds a line to a warning. Capped so a huge project can't stall the
        /// confirm dialog.
        /// </summary>
        public static List<string> FindFiles(IReadOnlyList<string> markers, int maxFiles = 25,
            IReadOnlyCollection<string> excludeAssetPaths = null)
        {
            var found = new List<string>();
            if (markers == null || markers.Count == 0) return found;

            var assets = Application.dataPath;
            var root = Path.GetFullPath(Path.Combine(assets, ".."));
            if (!Directory.Exists(assets)) return found;

            try
            {
                foreach (var file in Directory.EnumerateFiles(assets, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file);
                    if (!string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(ext, ".asmdef", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsUnder(file.Substring(assets.Length).TrimStart('\\', '/'), excludeAssetPaths)) continue;

                    string text;
                    try { text = File.ReadAllText(file); }
                    catch { continue; }

                    if (!ContainsAny(text, markers)) continue;

                    found.Add(file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/'));
                    if (found.Count >= maxFiles) break;
                }
            }
            catch { /* an unreadable folder is not worth failing the migration over */ }

            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }

        /// <summary>True when <paramref name="assetsRelative"/> (a path under Assets/) is one of
        /// <paramref name="roots"/> or inside one of them.</summary>
        internal static bool IsUnder(string assetsRelative, IReadOnlyCollection<string> roots)
        {
            if (roots == null || roots.Count == 0 || string.IsNullOrEmpty(assetsRelative)) return false;
            var p = assetsRelative.Replace('\\', '/').TrimStart('/');
            foreach (var r in roots)
            {
                if (string.IsNullOrEmpty(r)) continue;
                var root = r.Replace('\\', '/').Trim('/');
                if (root.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) root = root.Substring("Assets/".Length);
                if (p.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        internal static bool ContainsAny(string text, IReadOnlyList<string> markers)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var m in markers)
                if (!string.IsNullOrEmpty(m) && text.IndexOf(m, StringComparison.Ordinal) >= 0)
                    return true;
            return false;
        }

        /// <summary>One line per hit for a dialog or a log, or null when there is nothing to say.</summary>
        public static string Describe(IReadOnlyList<Hit> hits)
        {
            if (hits == null || hits.Count == 0) return null;
            var lines = new List<string>();
            foreach (var h in hits)
                lines.Add($"{h.Files.Count} script(s) still reference {h.PackageId} and will stop compiling " +
                          "once it is removed: " + string.Join(", ", h.Files));
            return string.Join("\n", lines);
        }
    }
}
