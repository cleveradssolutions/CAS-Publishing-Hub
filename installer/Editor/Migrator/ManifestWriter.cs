using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using CAS.Publishing.Installer.Common;

namespace CAS.Publishing.Installer.Migrator
{
    /// <summary>
    /// Applies manifest-level <see cref="MigrationAction"/>s to <c>Packages/manifest.json</c>.
    /// Reads + writes through <see cref="ManifestIO"/> (tolerant parse, atomic write with
    /// <c>.bak</c>). All mutations are idempotent and case-insensitive on dependency ids;
    /// registry matching ignores trailing-slash differences. <see cref="BackupAndDeletePath"/>
    /// and non-manifest action types are ignored here (the runner handles deletes).
    /// </summary>
    internal static class ManifestWriter
    {
        /// <summary>
        /// I/O entry point. Reads the manifest via <see cref="ManifestIO"/>, applies all
        /// manifest-mutating actions, and writes atomically only if something changed.
        /// Throws on a missing/unparseable manifest (callers must wrap) so the runner can
        /// abort BEFORE performing any irreversible delete.
        /// </summary>
        public static void ApplyActions(string manifestPath, IEnumerable<MigrationAction> actions)
        {
            if (string.IsNullOrEmpty(manifestPath)) throw new ArgumentNullException(nameof(manifestPath));
            if (actions == null) throw new ArgumentNullException(nameof(actions));

            var read = ManifestIO.Read(manifestPath);
            if (read.Status != ManifestReadStatus.Ok)
                throw new InvalidOperationException(
                    $"manifest.json could not be read ({read.Status}): {read.Error ?? manifestPath}");

            var manifest = read.Root;
            if (TryApply(manifest, actions))
            {
                ManifestIO.WriteAtomic(manifestPath, manifest);
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// Pure in-memory mutation. Applies all manifest-mutating actions to
        /// <paramref name="manifest"/>; returns true if anything changed. No I/O.
        /// </summary>
        public static bool TryApply(JObject manifest, IEnumerable<MigrationAction> actions)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (actions == null) throw new ArgumentNullException(nameof(actions));

            var modified = false;
            foreach (var action in actions)
            {
                switch (action)
                {
                    case AddPackage add:               modified |= ApplyAddPackage(manifest, add); break;
                    case AddGitPackage addGit:         modified |= ApplyAddGitPackage(manifest, addGit); break;
                    case RemovePackage remove:         modified |= ApplyRemovePackage(manifest, remove); break;
                    case UpdatePackageVersion update:  modified |= ApplyUpdatePackageVersion(manifest, update); break;
                    case AddScopedRegistry addReg:     modified |= ApplyAddScopedRegistry(manifest, addReg); break;
                    case AddScopeToRegistry addScope:  modified |= ApplyAddScopeToRegistry(manifest, addScope); break;
                    // BackupAndDeletePath and any other types are intentionally ignored.
                }
            }
            return modified;
        }

        // ── Mutation helpers ──────────────────────────────────────────────────

        private static bool ApplyAddPackage(JObject manifest, AddPackage action)
        {
            if (string.IsNullOrEmpty(action.Version)) return false; // never write an empty version
            var deps = EnsureDependencies(manifest);
            if (FindPropertyIgnoreCase(deps, action.Id) != null)
                return false; // already present (any casing) — idempotent
            deps[action.Id] = action.Version;
            return true;
        }

        private static bool ApplyAddGitPackage(JObject manifest, AddGitPackage action)
        {
            if (string.IsNullOrEmpty(action.Spec)) return false; // never write an empty spec
            var deps = EnsureDependencies(manifest);

            var existing = FindPropertyIgnoreCase(deps, action.Id);
            if (existing == null)
            {
                deps[action.Id] = action.Spec;
                return true;
            }

            // Already present. A REGISTRY version stays put — moving a package from the registry to
            // git is a method switch, which this action does not own. A GIT spec is different: for a
            // git-pinned record an update IS just a new tag on the same url, so refusing to rewrite it
            // made "Update" a silent no-op — UPM re-resolved the same old tag and the version never
            // moved, while the download it kicked off made it look like something had happened.
            var current = existing.Value as JValue;
            var currentSpec = current?.Value<string>();
            if (!CAS.Publishing.Installer.Scanner.StateClassifier.IsGitSpec(currentSpec)) return false;
            if (string.Equals(currentSpec, action.Spec, StringComparison.Ordinal)) return false;

            existing.Value = action.Spec;
            return true;
        }

        private static bool ApplyRemovePackage(JObject manifest, RemovePackage action)
        {
            var deps = manifest["dependencies"] as JObject;
            if (deps == null) return false;
            var prop = FindPropertyIgnoreCase(deps, action.Id);
            if (prop == null) return false; // already absent — idempotent
            prop.Remove();
            return true;
        }

        private static bool ApplyUpdatePackageVersion(JObject manifest, UpdatePackageVersion action)
        {
            if (string.IsNullOrEmpty(action.Version)) return false;
            var deps = manifest["dependencies"] as JObject;
            if (deps == null) return false;
            var prop = FindPropertyIgnoreCase(deps, action.Id);
            if (prop == null) return false; // update never creates — planner only updates installed packages
            if (string.Equals(prop.Value?.Value<string>(), action.Version, StringComparison.Ordinal))
                return false;
            prop.Value = action.Version;
            return true;
        }

        private static bool ApplyAddScopedRegistry(JObject manifest, AddScopedRegistry action)
        {
            var registries = EnsureScopedRegistries(manifest);
            var existing = FindRegistryByUrl(registries, action.Url);
            if (existing != null)
                return AddScopeToBlock(existing, action.Scope);

            registries.Add(new JObject(
                new JProperty("name", action.Name),
                new JProperty("url", action.Url),
                new JProperty("scopes", new JArray(action.Scope))));
            return true;
        }

        private static bool ApplyAddScopeToRegistry(JObject manifest, AddScopeToRegistry action)
        {
            var registries = manifest["scopedRegistries"] as JArray;
            if (registries == null) return false;
            var existing = FindRegistryByUrl(registries, action.Url);
            if (existing == null) return false;
            return AddScopeToBlock(existing, action.Scope);
        }

        // ── Low-level helpers ─────────────────────────────────────────────────

        private static JObject EnsureDependencies(JObject manifest)
        {
            if (manifest["dependencies"] is JObject deps) return deps;
            deps = new JObject();
            manifest["dependencies"] = deps;
            return deps;
        }

        private static JArray EnsureScopedRegistries(JObject manifest)
        {
            if (manifest["scopedRegistries"] is JArray arr) return arr;
            arr = new JArray();
            manifest["scopedRegistries"] = arr;
            return arr;
        }

        private static JProperty FindPropertyIgnoreCase(JObject obj, string name)
        {
            foreach (var prop in obj.Properties())
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    return prop;
            return null;
        }

        private static JObject FindRegistryByUrl(JArray registries, string url)
        {
            var target = NormaliseUrl(url);
            foreach (var token in registries)
                if (token is JObject obj && NormaliseUrl(obj["url"]?.Value<string>()) == target)
                    return obj;
            return null;
        }

        private static string NormaliseUrl(string url) =>
            string.IsNullOrEmpty(url) ? string.Empty : url.TrimEnd('/').ToLowerInvariant();

        private static bool AddScopeToBlock(JObject block, string scope)
        {
            // Only assign block["scopes"] when we actually create a new array. Re-assigning an
            // existing child array makes Newtonsoft CLONE it (a token can't have two parents),
            // orphaning our local reference so scopes.Add would mutate the detached copy.
            var scopes = block["scopes"] as JArray;
            if (scopes == null)
            {
                scopes = new JArray();
                block["scopes"] = scopes;
            }
            if (scopes.Any(s => string.Equals(s.Value<string>(), scope, StringComparison.OrdinalIgnoreCase)))
                return false;
            scopes.Add(scope);
            return true;
        }
    }
}
