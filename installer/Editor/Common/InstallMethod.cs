namespace CAS.Publishing.Installer.Common
{
    /// <summary>
    /// How the installer writes component dependencies into manifest.json. The UI no longer offers a
    /// choice — every install path passes <see cref="Upm"/>; <see cref="Git"/> remains as planner
    /// capability (git-installed projects are migrated TO UPM via "Switch to UPM").
    /// </summary>
    public enum InstallMethod
    {
        /// <summary>Scoped registry + version (the only user-facing method).</summary>
        Upm,
        /// <summary>git-URL dependencies (clean, no scoped registry).</summary>
        Git,
    }
}
