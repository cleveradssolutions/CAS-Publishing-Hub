using System.Collections.Generic;

namespace CAS.Publishing.Installer.Scanner
{
    /// <summary>
    /// What the Package Manager has resolved right now: every registered package (direct and
    /// transitive) with its version and the ids it depends on, plus which of them manifest.json
    /// lists directly. The planner reads it to see what a removal would take along with it.
    /// </summary>
    public sealed class InstalledGraph
    {
        public sealed class Node
        {
            public readonly string Version;
            public readonly IReadOnlyList<string> Dependencies;

            public Node(string version, IReadOnlyList<string> dependencies)
            {
                Version = version;
                Dependencies = dependencies ?? new string[0];
            }
        }

        public readonly Dictionary<string, Node> Packages = new Dictionary<string, Node>();
        public readonly HashSet<string> Direct = new HashSet<string>();

        /// <summary>The live graph; empty when the Package Manager can't be read.</summary>
        internal static InstalledGraph Collect()
        {
            var graph = new InstalledGraph();
            try
            {
                foreach (var pkg in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (pkg == null || string.IsNullOrEmpty(pkg.name)) continue;
                    var deps = new List<string>();
                    if (pkg.dependencies != null)
                        foreach (var d in pkg.dependencies)
                            if (!string.IsNullOrEmpty(d.name)) deps.Add(d.name);
                    graph.Packages[pkg.name] = new Node(pkg.version, deps);
                    if (pkg.isDirectDependency) graph.Direct.Add(pkg.name);
                }
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogWarning($"[CAS Hub] Could not read the package graph: {e.Message}");
            }
            return graph;
        }
    }
}
