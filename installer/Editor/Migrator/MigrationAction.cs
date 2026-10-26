namespace CAS.Publishing.Installer.Migrator
{
    // ── Base ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Abstract base for all migration actions produced by <see cref="MigrationPlanner"/>.
    /// Each derived type represents one atomic operation the migrator executor (Phase 4b)
    /// will carry out when <c>Apply selected</c> is invoked.
    ///
    /// Actions are pure data; they have no side effects. All derived types are sealed.
    /// </summary>
    public abstract class MigrationAction { }

    // ── Manifest mutations ────────────────────────────────────────────────────

    /// <summary>
    /// Add a new entry under <c>dependencies</c> in manifest.json.
    /// </summary>
    public sealed class AddPackage : MigrationAction
    {
        /// <summary>UPM package id to add (e.g. "com.example.debug").</summary>
        public string Id { get; }

        /// <summary>Version string to record (e.g. "1.2.3" or "1.0.0-preview.1").</summary>
        public string Version { get; }

        public AddPackage(string id, string version)
        {
            Id      = id;
            Version = version;
        }
    }

    /// <summary>
    /// Remove an existing entry from <c>dependencies</c> in manifest.json.
    /// No-op at execution time if the id is already absent.
    /// </summary>
    public sealed class RemovePackage : MigrationAction
    {
        /// <summary>UPM package id to remove (may be a legacy id or a canonical id).</summary>
        public string Id { get; }

        public RemovePackage(string id)
        {
            Id = id;
        }
    }

    /// <summary>
    /// Add (or, idempotently, leave) a git-URL entry under <c>dependencies</c> in manifest.json,
    /// e.g. <c>"com.tenjin.sdk": "https://github.com/CAS-Publishing/tenjin-sdk.git#1.15.14"</c>.
    /// Used by the Git install method instead of a registry + version pair.
    /// </summary>
    public sealed class AddGitPackage : MigrationAction
    {
        /// <summary>UPM package id to add.</summary>
        public string Id { get; }

        /// <summary>Full git dependency spec written as the value: <c>url#tag</c>.</summary>
        public string Spec { get; }

        public AddGitPackage(string id, string spec)
        {
            Id   = id;
            Spec = spec;
        }
    }

    /// <summary>
    /// Replace the version value of an existing <c>dependencies</c> entry in manifest.json.
    /// </summary>
    public sealed class UpdatePackageVersion : MigrationAction
    {
        /// <summary>Canonical UPM package id whose version should be updated.</summary>
        public string Id { get; }

        /// <summary>Target version string.</summary>
        public string Version { get; }

        public UpdatePackageVersion(string id, string version)
        {
            Id      = id;
            Version = version;
        }
    }

    /// <summary>
    /// Add a completely new <c>scopedRegistries</c> block to manifest.json.
    /// Used when no registry with the target URL is registered yet.
    /// </summary>
    public sealed class AddScopedRegistry : MigrationAction
    {
        /// <summary>Registry display name (e.g. "CAS Publishing").</summary>
        public string Name { get; }

        /// <summary>Registry URL (e.g. "https://registry.example.com/").</summary>
        public string Url { get; }

        /// <summary>
        /// The single scope string that caused this registry to be added
        /// (e.g. "com.example"). Additional scope merges are separate
        /// <see cref="AddScopeToRegistry"/> actions.
        /// </summary>
        public string Scope { get; }

        public AddScopedRegistry(string name, string url, string scope)
        {
            Name  = name;
            Url   = url;
            Scope = scope;
        }
    }

    /// <summary>
    /// Append one scope to an existing <c>scopedRegistries</c> block identified by URL.
    /// Used when the registry URL is already registered but the required scope is missing.
    /// </summary>
    public sealed class AddScopeToRegistry : MigrationAction
    {
        /// <summary>
        /// URL of the existing scoped-registry entry to which the scope should be appended.
        /// </summary>
        public string Url { get; }

        /// <summary>Scope string to add (e.g. "com.google").</summary>
        public string Scope { get; }

        public AddScopeToRegistry(string url, string scope)
        {
            Url   = url;
            Scope = scope;
        }
    }

    /// <summary>
    /// Delete a file or directory under <c>Assets/</c>. (The name is historical — there is NO backup
    /// archive.) The runner deletes via <c>AssetDatabase.DeleteAsset</c> / <c>File</c>/<c>Directory</c>;
    /// recovery is delegated entirely to the client's git, which is why <see cref="MigrationRunner"/>
    /// refuses untracked/dirty paths unless <see cref="IgnoreGitGuard"/> is set. With
    /// <see cref="IgnoreGitGuard"/> = true the deletion is PERMANENT and unrecoverable.
    /// </summary>
    public sealed class BackupAndDeletePath : MigrationAction
    {
        /// <summary>
        /// Path relative to <c>Assets/</c> (e.g. "Plugins/Firebase").
        /// The executor resolves this against <c>Application.dataPath</c>.
        /// </summary>
        public string RelativePath { get; }

        /// <summary>
        /// When true, the runner deletes even if git can't recover the path (untracked/dirty).
        /// PathSafety (no escaping Assets/) is still enforced. Set only after an explicit
        /// "Delete anyway" confirmation — the deletion is permanent.
        /// </summary>
        public bool IgnoreGitGuard { get; }

        public BackupAndDeletePath(string relativePath, bool ignoreGitGuard = false)
        {
            RelativePath = relativePath;
            IgnoreGitGuard = ignoreGitGuard;
        }
    }
}
