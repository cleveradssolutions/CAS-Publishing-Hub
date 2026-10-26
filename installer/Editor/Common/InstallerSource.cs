using UnityEditor.PackageManager;

namespace CAS.Publishing.Installer.Common
{
    /// <summary>
    /// How the installer package itself was installed in this project. Used to decide how to fetch
    /// the metadata catalog (git-installed installer → fetch metadata via git, no scoped registry).
    /// </summary>
    public static class InstallerSource
    {
        /// <summary>
        /// True when the installer package was added via a git URL (PackageSource.Git). Anything else
        /// — registry, embedded (dev project), local — returns false → the registry path is used.
        /// Never throws.
        /// </summary>
        public static bool IsGit()
        {
            try
            {
                var info = PackageInfo.FindForAssembly(typeof(InstallerSource).Assembly);
                return info != null && info.source == PackageSource.Git;
            }
            catch
            {
                return false; // can't tell → safe default is the registry path
            }
        }
    }
}
