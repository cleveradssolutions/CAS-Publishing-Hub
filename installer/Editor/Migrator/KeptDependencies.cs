using System.Collections.Generic;
using CAS.Publishing.Installer.Scanner;

namespace CAS.Publishing.Installer.Migrator
{
    /// <summary>
    /// Packages a removal would take along: removing a retired package from manifest.json also drops
    /// everything that was in the project only as its dependency. The old Tenjin wrapper is how many
    /// projects got Firebase, EDM and Unity IAP — removing it left Firebase.Editor without EDM and
    /// the game without IAP (QA 1.0.9 B-01, F-02). Such a dependency is kept by listing it in
    /// manifest.json directly, at the version installed now, so the project resolves as before.
    ///
    /// Only the top of each orphaned chain is kept (Firebase Analytics brings Firebase App and EDM
    /// back itself). Never kept: other retired packages, packages the same plan installs, and
    /// packages something else in the project still depends on. Pure — no Unity, no I/O.
    /// </summary>
    internal static class KeptDependencies
    {
        internal struct Keep
        {
            public string Id;
            public string Version;
            public string For;   // the removed package it was a dependency of
        }

        internal static List<Keep> Find(IReadOnlyCollection<string> removed, InstalledGraph graph,
            ICollection<string> retired, ICollection<string> planned)
        {
            var keeps = new List<Keep>();
            if (removed == null || removed.Count == 0 || graph == null || graph.Packages.Count == 0) return keeps;

            var removedSet = new HashSet<string>(removed);

            // Everything still reachable from the direct dependencies that stay.
            var reachable = new HashSet<string>();
            foreach (var root in graph.Direct)
                if (!removedSet.Contains(root)) Reach(root, graph, reachable);

            foreach (var gone in removed)
            {
                if (!graph.Packages.TryGetValue(gone, out var node)) continue;
                // Breadth-first so a chain is kept at its top and the rest rides along.
                var queue = new Queue<string>(node.Dependencies);
                var seen = new HashSet<string>();
                while (queue.Count > 0)
                {
                    var id = queue.Dequeue();
                    if (!seen.Add(id)) continue;
                    if (removedSet.Contains(id) || retired.Contains(id) || reachable.Contains(id)) continue;
                    if (!graph.Packages.TryGetValue(id, out var dep)) continue;
                    if (planned.Contains(id) || id.StartsWith("com.unity.modules."))
                    {
                        Reach(id, graph, reachable);
                        continue;
                    }

                    if (string.IsNullOrEmpty(dep.Version)) continue;
                    keeps.Add(new Keep { Id = id, Version = dep.Version, For = gone });
                    Reach(id, graph, reachable);
                }
            }
            return keeps;
        }

        private static void Reach(string id, InstalledGraph graph, HashSet<string> reachable)
        {
            if (!reachable.Add(id)) return;
            if (!graph.Packages.TryGetValue(id, out var node)) return;
            foreach (var d in node.Dependencies) Reach(d, graph, reachable);
        }
    }
}
