using System.Collections.Generic;
using Newtonsoft.Json;

namespace CAS.Publishing.Installer.Catalog
{
    public sealed class PackageCatalog
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion;

        [JsonProperty("catalogVersion")]
        public string CatalogVersion;

        [JsonProperty("registries")]
        public Dictionary<string, string> Registries;

        [JsonProperty("categories")]
        public List<Category> Categories;

        [JsonProperty("packages")]
        public List<PackageRecord> Packages;

        [JsonProperty("external")]
        public List<ExternalRecord> External;

        [JsonProperty("overrides")]
        public Dictionary<string, object> Overrides;

        /// <summary>
        /// Optional list of legacy packages that need to be removed with no replacement.
        /// Absent in catalog.json → Newtonsoft leaves this null; all callers must
        /// treat null the same as an empty list.  Adding this field is backward-compatible:
        /// older installer versions simply ignore the <c>uninstall</c> key.
        /// </summary>
        [JsonProperty("uninstall")]
        public List<UninstallRecord> Uninstall;

        /// <summary>
        /// Where the wizard reports its usage events. Absent → telemetry is off. Lives in the
        /// catalog so the endpoint can be turned on, moved or switched off without an installer
        /// release; older installers ignore the key.
        /// </summary>
        [JsonProperty("telemetry")]
        public TelemetrySettings Telemetry;
    }

    public sealed class TelemetrySettings
    {
        [JsonProperty("endpoint")]  public string Endpoint;
        /// <summary>Write key sent as <c>X-Hub-Key</c>. Not a secret — it only tags the edition.</summary>
        [JsonProperty("key")]       public string Key;
        [JsonProperty("enabled")]   public bool Enabled = true;
    }

    public sealed class Category
    {
        [JsonProperty("id")]          public string Id;
        [JsonProperty("displayName")] public string DisplayName;
    }

    public sealed class PackageRecord
    {
        [JsonProperty("id")]                 public string Id;
        [JsonProperty("displayName")]        public string DisplayName;
        [JsonProperty("registry")]           public string Registry;
        [JsonProperty("category")]           public string Category;

        /// <summary>
        /// Where this record installs from: <c>"git"</c> — the git source (<see cref="Git"/>);
        /// absent or anything else — the scoped registry, as before. Needed for versions the registry
        /// doesn't carry: CAS prereleases reach neither openupm nor the private registry, while the
        /// GitHub tag is public. Changed as data, without an installer release.
        /// </summary>
        [JsonProperty("installMethod")]      public string InstallMethodName;


        /// <summary>Which editions may see this record: "internal" / "external".
        /// Null/empty = visible to both. Enforced at metadata publish time (the external
        /// catalog artifact physically strips internal-only records); the installer
        /// runtime does not filter on it.</summary>
        [JsonProperty("audience")]           public List<string> Audience;

        [JsonProperty("legacyNpmIds")]       public List<string> LegacyNpmIds;
        [JsonProperty("legacyAssetPaths")]   public List<string> LegacyAssetPaths;
        [JsonProperty("minVersion")]         public string MinVersion;
        [JsonProperty("recommendedVersion")] public string RecommendedVersion;

        /// <summary>
        /// Optional scoped-registry scopes to register when installing this package. Absent →
        /// the planner defaults to the package id itself (an exact-id scope is always correct).
        /// </summary>
        [JsonProperty("scopes")]             public List<string> Scopes;

        /// <summary>
        /// Optional package ids that must ALREADY be present in the project (manifest dependency
        /// or embedded under Packages/) before this package is offered. Presence check ONLY — the
        /// installer never installs, resolves, or registers a scope for a required id
        /// (e.g. com.psv.core is git-distributed and not on the registry).
        /// </summary>
        [JsonProperty("requires")]           public List<string> Requires;

        /// <summary>
        /// Optional loaded-type markers (matched like <see cref="ExternalModule.AssetMarkers"/>)
        /// gating this package during a compound legacy migration: absent → always installed with
        /// its split group; present → installed only when a marker matches a loaded identifier
        /// (e.g. the remoteconfig adapter only when Firebase.RemoteConfig types are loaded).
        /// </summary>
        [JsonProperty("detectMarkers")]      public List<string> DetectMarkers;

        /// <summary>Optional post-install configuration requirements (per platform), used to
        /// render the Setup readiness checklist. Absent → no config to verify.</summary>
        [JsonProperty("config")]             public List<ConfigRequirement> Config;

        /// <summary>
        /// Optional git-install chain for this component. When the Git method is chosen, the
        /// installer writes one git-URL dependency per entry here (top-level + transitive),
        /// with no scoped registry. Absent → git method falls back to UPM for this component.
        /// </summary>
        [JsonProperty("git")]                public GitInstall Git;
    }

    public sealed class ExternalRecord
    {
        [JsonProperty("id")]                 public string Id;
        [JsonProperty("displayName")]        public string DisplayName;
        [JsonProperty("registry")]           public string Registry;
        [JsonProperty("scopes")]             public List<string> Scopes;
        [JsonProperty("category")]           public string Category;

        /// <summary>
        /// Where this record installs from: <c>"git"</c> — the git source (<see cref="Git"/>);
        /// absent or anything else — the scoped registry, as before. Needed for versions the registry
        /// doesn't carry: CAS prereleases reach neither openupm nor the private registry, while the
        /// GitHub tag is public. Changed as data, without an installer release.
        /// </summary>
        [JsonProperty("installMethod")]      public string InstallMethodName;

        /// <summary>
        /// With <c>"installMethod": "git"</c>: try the scoped registry first. Once the registry
        /// publishes a version at or above <c>minVersion</c>, the record installs that version from
        /// the registry; until then it installs its git source (see <see cref="RegistryVersions"/>).
        /// Installers before 1.0.9 ignore the field and keep using git, so the record stays safe for them.
        /// </summary>
        [JsonProperty("registryFirst")]      public bool RegistryFirst;


        /// <summary>Which editions may see this record: "internal" / "external".
        /// Null/empty = visible to both. Enforced at metadata publish time (the external
        /// catalog artifact physically strips internal-only records); the installer
        /// runtime does not filter on it.</summary>
        [JsonProperty("audience")]           public List<string> Audience;

        /// <summary>
        /// Optional markers identifying a NON-UPM (e.g. .unitypackage) copy of this SDK in the
        /// project: substrings matched (case-insensitive) against asmdef name/rootNamespace and
        /// precompiled DLL file names found under <c>Assets/</c>. Used to detect a manual install
        /// so the hub doesn't offer Install (which would duplicate it). Match assembly/namespace,
        /// not folder names, so a leftover settings folder is not a false positive.
        /// Absent → no out-of-UPM detection for this external.
        /// </summary>
        [JsonProperty("assetMarkers")]       public List<string> AssetMarkers;

        /// <summary>
        /// Optional manifest dependency ids that are a LEGACY form of this SDK — a different package
        /// that already provides it (e.g. the bundled git package <c>com.psv.tenjin</c> for Tenjin).
        /// When manifest.json contains one of these (and not the canonical <see cref="Id"/>), the SDK
        /// is already working, so the scanner reports <see cref="Scanner.ExternalState.InstalledLegacy"/>
        /// and the hub offers no Install/Migrate (installing the canonical id would duplicate the SDK;
        /// the legacy wrapper's namespace may also differ). Manifest is authoritative, so this also
        /// avoids reflection false-positives. Absent → no legacy recognition.
        /// </summary>
        [JsonProperty("legacyManifestIds")]  public List<string> LegacyManifestIds;

        /// <summary>
        /// Optional git-install chain for this component. When the Git method is chosen, the
        /// installer writes one git-URL dependency per entry here (top-level + transitive),
        /// with no scoped registry. Absent → git method falls back to UPM for this component.
        /// </summary>
        [JsonProperty("git")]                public GitInstall Git;

        /// <summary>
        /// Optional sub-modules that share this SDK's on-disk footprint (e.g. Firebase ships
        /// Analytics / RemoteConfig / Installations under one <c>Assets/Firebase</c> folder).
        /// When migrating, the installer installs the UPM id of EVERY module whose markers are
        /// detected on disk — not just <see cref="Id"/> — so a multi-module SDK isn't reduced to
        /// one module (which would leave the others deleted-but-not-reinstalled). Absent → the
        /// external behaves as a single package keyed by <see cref="Id"/> (unchanged).
        /// </summary>
        [JsonProperty("modules")]            public List<ExternalModule> Modules;

        /// <summary>
        /// Assets-relative folders WHOLLY OWNED by this SDK that migration deletes when moving it to UPM
        /// (its install folder + satellites like EDM / PlayServicesResolver / its own
        /// <c>Editor Default Resources/&lt;sdk&gt;</c> subfolder). Only the ones that actually exist are
        /// deleted, each through the git/path-safety guard. NEVER list a shared folder
        /// (<c>Assets/Plugins</c>, <c>Resources</c>, …) — those are surfaced as a manual-cleanup warning.
        /// This is the ONLY source of migration delete targets — there is no file-walk. Absent → the
        /// migrator reports "couldn't locate, remove manually" (safe no-op).
        /// </summary>
        [JsonProperty("assetRoots")]         public List<string> AssetRoots;

        /// <summary>Exact/glob file names (single '*') of this SDK's native libs in Assets/Plugins to
        /// auto-delete on migrate (e.g. "libFirebaseCppApp.a"). Precise — never broad globs.</summary>
        [JsonProperty("pluginFiles")] public List<string> PluginFiles;

        /// <summary>
        /// Individual SDK files the manual (.unitypackage) install SCATTERS outside <see cref="AssetRoots"/>
        /// into shared folders (e.g. Tenjin's <c>BuildPostProcessor.cs</c> / <c>Dependencies.xml</c> dropped
        /// into <c>Assets/Editor</c>). Each is matched by file NAME + a CONTENT signature (all
        /// <see cref="LegacyAssetFile.Contains"/> markers must appear) so a user's unrelated file of the
        /// same name is never touched. Found anywhere under Assets/ (outside <see cref="AssetRoots"/>) and
        /// surfaced in the migrate confirm window for the user to approve before deletion. Absent → none.
        /// </summary>
        [JsonProperty("legacyAssetFiles")]   public List<LegacyAssetFile> LegacyAssetFiles;

        /// <summary>
        /// Optional static string member exposing the SDK's own version at runtime, used to detect a
        /// DOWNGRADE before migrating a manual (Assets) install to the catalog-pinned UPM version.
        /// Read by reflection from the LOADED manual copy as <c>VersionType.VersionField</c> (a static
        /// field / property / const string, e.g. Firebase: <c>"Firebase.VersionInfo"</c> + <c>"SdkVersion"</c>).
        /// When the on-disk version is newer than the version we'd install, the migrate dialog warns.
        /// Absent / type or member not found → no downgrade check (migration proceeds as before).
        /// </summary>
        [JsonProperty("versionType")]        public string VersionType;
        [JsonProperty("versionField")]       public string VersionField;

        /// <summary>
        /// Recommended version string used by the migrator when generating
        /// <c>AddPackage</c> actions for this external. Optional — when absent,
        /// <see cref="MinVersion"/> is used; when both are absent the planner emits
        /// a <c>PlannerWarning</c> instead of an <c>AddPackage</c> action.
        /// </summary>
        [JsonProperty("recommendedVersion")] public string RecommendedVersion;

        /// <summary>Minimum acceptable version. Fallback for the migrator when
        /// <see cref="RecommendedVersion"/> is absent.</summary>
        [JsonProperty("minVersion")]         public string MinVersion;

        /// <summary>Optional post-install configuration requirements (per platform), used to
        /// render the Setup readiness checklist. Absent → no config to verify.</summary>
        [JsonProperty("config")]             public List<ConfigRequirement> Config;
    }

    /// <summary>
    /// A single scattered legacy SDK file to find and remove on migration (see
    /// <see cref="ExternalRecord.LegacyAssetFiles"/>). Matched by exact file <see cref="Name"/> AND
    /// every marker in <see cref="Contains"/> appearing in the file's text — name alone is never
    /// enough, so a user's same-named file is safe.
    /// </summary>
    public sealed class LegacyAssetFile
    {
        /// <summary>Exact file name to look for, e.g. "BuildPostProcessor.cs".</summary>
        [JsonProperty("name")]     public string Name;

        /// <summary>Content markers that must ALL appear in the file for it to count as this SDK's
        /// (e.g. ["class BuildPostProcessor", "TenjinSDK"]). Empty → name-only (discouraged).</summary>
        [JsonProperty("contains")] public List<string> Contains;
    }

    /// <summary>
    /// One declarative post-install configuration requirement for a component, evaluated by the
    /// installer's Setup checker. Data-driven: the catalog (metadata package) declares WHAT each
    /// component needs; the installer ships the generic handlers for each <see cref="Kind"/>.
    /// </summary>
    public sealed class ConfigRequirement
    {
        /// <summary>"Android" | "iOS" | null (any/both) — which column the requirement belongs to.</summary>
        [JsonProperty("platform")] public string Platform;

        /// <summary>Requirement kind the installer knows how to check:
        /// "assetFile" (a file must exist in Assets) | "settingsAssetField" (a ScriptableObject
        /// field must be set) | "publishingField" (a field of the installer's own
        /// PublishingSettings asset must be set — the Tenjin key; only <see cref="Field"/>,
        /// <see cref="Regex"/>, <see cref="Hint"/> and <see cref="Help"/> apply).</summary>
        [JsonProperty("kind")] public string Kind;

        /// <summary>Short human label for the row/cell.</summary>
        [JsonProperty("label")] public string Label;

        // ── assetFile ──
        /// <summary>File name to locate anywhere under Assets/ (e.g. "google-services.json").</summary>
        [JsonProperty("fileName")] public string FileName;

        /// <summary>Optional help URL shown when the file is missing (e.g. Firebase console).</summary>
        [JsonProperty("help")] public string Help;

        // ── settingsAssetField ──
        /// <summary>ScriptableObject type name to locate via AssetDatabase (e.g. "TenjinSettings").</summary>
        [JsonProperty("assetType")] public string AssetType;

        /// <summary>Explicit asset path (alternative to <see cref="AssetType"/>),
        /// e.g. "Assets/CleverAdsSolutions/Resources/CASSettingsAndroid.asset".</summary>
        [JsonProperty("assetPath")] public string AssetPath;

        /// <summary>Serialized field name that must be non-empty (e.g. "androidKey", "managerIds").</summary>
        [JsonProperty("field")] public string Field;

        /// <summary>A value that counts as "not configured" (e.g. the CAS placeholder "demo").</summary>
        [JsonProperty("placeholder")] public string Placeholder;

        /// <summary>Optional per-platform validation regex for the wizard input (e.g. CAS bundle/numeric).</summary>
        [JsonProperty("regex")] public string Regex;

        /// <summary>Optional placeholder hint text shown in the empty wizard input.</summary>
        [JsonProperty("hint")] public string Hint;

        /// <summary>When true, the Configuration screen renders this settings field as an editable
        /// input that writes the value back to the settings asset (e.g. CAS managerId). Default false.</summary>
        [JsonProperty("editable")] public bool Editable;

        /// <summary>When true, the Configuration screen renders CAS ad-format toggles and audience/network controls for this component.</summary>
        [JsonProperty("adFormats")] public bool AdFormats;

        /// <summary>Optional Unity menu path to open the relevant settings window (e.g. CAS).</summary>
        [JsonProperty("openMenu")] public string OpenMenu;
    }

    /// <summary>
    /// Describes a legacy package that should be removed from the client project
    /// without any replacement. The catalog entry lists the old npm ids that may
    /// still appear in manifest.json; the migrator removes them.
    /// </summary>
    public sealed class UninstallRecord
    {
        /// <summary>
        /// One or more legacy npm package ids that should be removed from
        /// <c>manifest.json</c> (e.g. <c>["com.psv.unity.edm"]</c>).
        /// </summary>
        [JsonProperty("legacyNpmIds")]
        public List<string> LegacyNpmIds;

        /// <summary>
        /// Human-readable explanation of why the package is being uninstalled
        /// (e.g. "arrives transitively via firebase — no separate entry needed").
        /// Informational only; not used by the planner.
        /// </summary>
        [JsonProperty("reason")]
        public string Reason;

        /// <summary>
        /// Identifiers (a namespace, type names) the game's own code contains when it calls into
        /// this package. Removing the package then breaks compilation, so the Hub lists the files
        /// that mention them before the removal is confirmed (<c>RetiredCodeProbe</c>). Optional.
        /// </summary>
        [JsonProperty("codeMarkers")]
        public List<string> CodeMarkers;

        /// <summary>
        /// Where this package kept the Tenjin SDK keys, so the Hub can carry them over into
        /// <c>PublishingSettings</c> once the package is gone (<c>TenjinKeyMigration</c>). Optional.
        /// </summary>
        [JsonProperty("legacySettings")]
        public LegacySettingsRef LegacySettings;

        /// <summary>
        /// Folders under Assets/ a manual (.unitypackage) copy of the retired package lives in, e.g.
        /// <c>["Tenjin"]</c>. With <see cref="LegacyAssetFiles"/>, this is how the Hub notices a
        /// manual install that never touched manifest.json and offers to delete it. Optional.
        /// </summary>
        [JsonProperty("assetRoots")]
        public List<string> AssetRoots;

        /// <summary>Signature-matched files of such a manual copy scattered outside
        /// <see cref="AssetRoots"/> — same contract as <see cref="ExternalRecord.LegacyAssetFiles"/>. Optional.</summary>
        [JsonProperty("legacyAssetFiles")]
        public List<LegacyAssetFile> LegacyAssetFiles;
    }

    /// <summary>
    /// A retired package's settings asset: a ScriptableObject under <c>Resources/</c> and the two
    /// serialized string fields holding the per-platform keys.
    /// </summary>
    public sealed class LegacySettingsRef
    {
        /// <summary>Resource name — the asset file is <c>Resources/&lt;resource&gt;.asset</c>.</summary>
        [JsonProperty("resource")]        public string Resource;
        [JsonProperty("androidKeyField")] public string AndroidKeyField;
        [JsonProperty("iosKeyField")]     public string IosKeyField;
    }

    /// <summary>
    /// One sub-module of a multi-module external (see <see cref="ExternalRecord.Modules"/>).
    /// Detected independently by its own <see cref="AssetMarkers"/>; installed at
    /// <see cref="RecommendedVersion"/> when present, otherwise the parent record's version.
    /// </summary>
    public sealed class ExternalModule
    {
        [JsonProperty("id")]                 public string Id;
        [JsonProperty("assetMarkers")]       public List<string> AssetMarkers;

        /// <summary>Optional per-module version override; null → inherit the parent external's version.</summary>
        [JsonProperty("recommendedVersion")] public string RecommendedVersion;
    }

    /// <summary>The flat set of packages to write as git-URL dependencies for one component.</summary>
    public sealed class GitInstall
    {
        [JsonProperty("packages")] public List<GitPackage> Packages;
    }

    /// <summary>One git-URL dependency: id plus repo URL plus tag.</summary>
    public sealed class GitPackage
    {
        [JsonProperty("id")]  public string Id;
        [JsonProperty("url")] public string Url;
        [JsonProperty("tag")] public string Tag;

        /// <summary>The manifest dependency value: <c>url#tag</c>.</summary>
        public string Spec => string.IsNullOrEmpty(Tag) ? Url : $"{Url}#{Tag}";
    }
}
