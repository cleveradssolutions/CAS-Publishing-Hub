using System;
using System.IO;

namespace CAS.Publishing.Installer.Common
{
    /// <summary>
    /// Guards filesystem operations against escaping a trusted root directory.
    /// A migration deletes paths taken from the (auto-updated) catalog, so a stray
    /// <c>..</c> or rooted path must never be allowed to touch files outside <c>Assets/</c>.
    /// </summary>
    public static class PathSafety
    {
        /// <summary>
        /// Resolves <paramref name="relativePath"/> against <paramref name="rootAbsolute"/>,
        /// canonicalises it (collapsing <c>.</c>/<c>..</c>), and verifies the result stays
        /// strictly inside the root. Rejects empty input, rooted/absolute relatives, the root
        /// itself, and any path that escapes. On success <paramref name="resolved"/> is the
        /// canonical absolute path (forward-slash separators). On failure returns false with a
        /// human-readable <paramref name="error"/> and a null <paramref name="resolved"/>.
        /// Note: symlinks/junctions are NOT resolved — a symlinked directory inside the root
        /// that points outside will still pass containment; callers in environments where
        /// third-party assets may introduce links should resolve real paths first.
        /// </summary>
        public static bool TryResolveContained(string rootAbsolute, string relativePath, out string resolved, out string error)
        {
            resolved = null;
            error = null;

            if (string.IsNullOrWhiteSpace(rootAbsolute)) { error = "root is empty"; return false; }
            if (string.IsNullOrWhiteSpace(relativePath)) { error = "relative path is empty"; return false; }

            // Catalog paths may be written on Windows. On macOS/Linux a backslash is not a separator,
            // so "Plugins\..\..\etc" read as one odd file name and passed — normalise first, so a
            // path means the same thing on every OS.
            relativePath = relativePath.Replace('\\', '/');
            if (Path.IsPathRooted(relativePath)) { error = $"rooted path not allowed: '{relativePath}'"; return false; }

            string rootFull, candidate;
            try
            {
                rootFull = Path.GetFullPath(rootAbsolute).Replace('\\', '/').TrimEnd('/');
                candidate = Path.GetFullPath(Path.Combine(Path.GetFullPath(rootAbsolute), relativePath)).Replace('\\', '/').TrimEnd('/');
            }
            catch (Exception e) { error = $"path resolve failed: {e.Message}"; return false; }

            if (string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase))
            {
                error = "refusing to operate on the root itself";
                return false;
            }

            // OrdinalIgnoreCase: correct for Windows/macOS; on case-sensitive FS Unity's
            // Assets/ is always literal-cased, so this is a safe, deliberate trade-off.
            if (!candidate.StartsWith(rootFull + "/", StringComparison.OrdinalIgnoreCase))
            {
                error = $"path escapes root: '{relativePath}'";
                return false;
            }

            resolved = candidate;
            return true;
        }
    }
}
