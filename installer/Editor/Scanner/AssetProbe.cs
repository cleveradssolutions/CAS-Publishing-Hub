using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CAS.Publishing.Installer.Scanner
{
    /// <summary>
    /// Checks which candidate paths (relative to Assets/) actually exist on disk.
    /// This class never mutates anything — read-only probe.
    /// </summary>
    internal static class AssetProbe
    {
        /// <summary>
        /// Given a list of paths relative to Assets/ (e.g. "Plugins/CAS"),
        /// returns only those that actually exist under Application.dataPath.
        /// Checks both files and directories. Never throws; returns empty list on any error.
        /// </summary>
        public static List<string> FindExisting(IReadOnlyList<string> candidatePaths)
        {
            var result = new List<string>();
            if (candidatePaths == null || candidatePaths.Count == 0)
                return result;

            var assetsRoot = Application.dataPath; // e.g. "C:/project/Assets"

            foreach (var relativePath in candidatePaths)
            {
                if (string.IsNullOrEmpty(relativePath))
                    continue;

                var fullPath = Path.GetFullPath(Path.Combine(assetsRoot, relativePath));

                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                    result.Add(relativePath);
            }

            return result;
        }
    }
}
