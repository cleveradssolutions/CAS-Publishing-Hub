using System.Collections.Generic;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// The custom Android build templates a buildable EDM4U project needs under
    /// <c>Assets/Plugins/Android/</c>. Their presence is what enables Unity's "Custom … Template"
    /// and "Custom Main Manifest" toggles, so EDM4U can inject resolved dependencies.
    /// </summary>
    internal static class AndroidBuildTemplates
    {
        public const string PluginsAndroidDir = "Assets/Plugins/Android";

        public static readonly IReadOnlyList<string> Required = new[]
        {
            "mainTemplate.gradle",
            "launcherTemplate.gradle",
            "baseProjectTemplate.gradle",
            "gradleTemplate.properties",
            "settingsTemplate.gradle",
            "AndroidManifest.xml",
        };

        /// <summary>
        /// Marks the error_prone_annotations pin, so it is added once and a user's own block is
        /// recognised by the dependency name.
        /// </summary>
        public const string ErrorProneMarker = "// CAS Hub: error_prone_annotations pin";

        /// <summary>
        /// The Unity 2022.3 build fix: current Firebase (23.x) and Google Ads (25.x) Android libraries
        /// pull error_prone_annotations 2.38.0, which the D8 of Unity 2022's bundled AGP 7.4 fails on
        /// (NullPointerException). Unity 6 (AGP 8+) builds the same set fine, so only older Editors
        /// get the pin. Written as a resolutionStrategy block: CAS's CASPostGenerateGradle adds its
        /// own forces inside such blocks.
        /// </summary>
        public static string WithErrorProneForce(string baseProjectTemplate)
        {
            if (baseProjectTemplate == null) return null;
            if (baseProjectTemplate.Contains("error_prone_annotations")) return baseProjectTemplate;

            var nl = baseProjectTemplate.Contains("\r\n") ? "\r\n" : "\n";
            var text = baseProjectTemplate;
            if (text.Length > 0 && !text.EndsWith("\n")) text += nl;
            return text + nl +
                   ErrorProneMarker + " (Unity 2022.3 / AGP 7.4: D8 fails on 2.38.0)" + nl +
                   "allprojects {" + nl +
                   "    configurations.all {" + nl +
                   "        resolutionStrategy {" + nl +
                   "            force 'com.google.errorprone:error_prone_annotations:2.28.0'" + nl +
                   "        }" + nl +
                   "    }" + nl +
                   "}" + nl;
        }

        /// <summary>Required template names not present in <paramref name="presentFileNames"/> (case-insensitive). Pure.</summary>
        public static List<string> Missing(IEnumerable<string> presentFileNames)
        {
            var present = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (presentFileNames != null) foreach (var n in presentFileNames) if (!string.IsNullOrEmpty(n)) present.Add(n);
            var missing = new List<string>();
            foreach (var req in Required) if (!present.Contains(req)) missing.Add(req);
            return missing;
        }
    }
}
