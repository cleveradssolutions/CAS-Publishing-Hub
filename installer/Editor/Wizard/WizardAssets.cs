using UnityEditor;
using UnityEngine.UIElements;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Central package-relative asset paths for the wizard UI, plus typed loaders.
    /// UPM mounts the package under the virtual "Packages/&lt;name&gt;/" path, so these
    /// AssetDatabase loads resolve regardless of where the package lives on disk.
    /// </summary>
    internal static class WizardAssets
    {
        public const string Root  = "Packages/" + CAS.Publishing.Installer.Common.EditionInfo.InstallerPackageId + "/Editor/Wizard";
        public const string Uxml  = Root + "/Uxml";
        public const string Uss   = Root + "/Uss";
        public const string Icons = Root + "/Icons";
        public const string Fonts = Root + "/Fonts";

        /// <summary>The installer package's own version, read from its package.json at runtime.</summary>
        public static string InstallerVersion
        {
            get
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(WizardAssets).Assembly);
                return info != null && !string.IsNullOrEmpty(info.version) ? info.version : "0.0.0";
            }
        }

        public static VisualTreeAsset LoadTree(string name)
            => AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{Uxml}/{name}.uxml");

        public static StyleSheet LoadStyle(string name)
            => AssetDatabase.LoadAssetAtPath<StyleSheet>($"{Uss}/{name}.uss");

        /// <summary>
        /// Loads the named UXML and clones it into <paramref name="target"/>.
        /// Logs (instead of throwing) when the asset is missing, so one missing template
        /// can never abort window creation.
        /// </summary>
        public static void CloneInto(string treeName, VisualElement target)
        {
            var tree = LoadTree(treeName);
            if (tree != null)
                tree.CloneTree(target);
            else
                UnityEngine.Debug.LogError($"[CAS Hub] Missing UXML asset: {treeName}.uxml");
        }
    }
}
