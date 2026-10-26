namespace CAS.Publishing.Installer.Common
{
    /// <summary>
    /// Single source of truth for edition-specific identity (package ids, registry).
    /// The COMMITTED version is always the INTERNAL edition; the external artifact is
    /// produced at publish time by Tools~/build-edition.mjs, which regenerates this
    /// file. Do not scatter these values anywhere else in the codebase — the
    /// EditionSingleSourceTests grep-test enforces that.
    /// </summary>
    internal static class EditionInfo
    {
        public const string EditionId          = "external";
        public const string InstallerPackageId = "ai.cas.installer";
        public const string MetadataPackageId  = "ai.cas.installer.metadata";
        public const string RegistryName       = "CAS Publishing";
        public const string RegistryUrl        = "https://npm.cas.ai/";
        public const string RegistryScope      = "ai.cas";
        public const string InstallerGitUrl    = "https://github.com/cleveradssolutions/CAS-Publishing-Hub.git?path=/installer";
        public const string MetadataGitUrl     = "https://github.com/cleveradssolutions/CAS-Publishing-Hub.git?path=/metadata";
    }
}
