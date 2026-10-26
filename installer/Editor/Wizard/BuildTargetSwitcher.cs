using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    internal enum SwitchRoute
    {
        ModuleMissing,
        ClassicProfile,
        LegacyAsync,
    }

    /// <summary>
    /// Switches the active build target without ever failing silently. On Unity 6+ the public
    /// <see cref="EditorUserBuildSettings.SwitchActiveBuildTargetAsync(BuildTargetGroup, BuildTarget)"/>
    /// can no-op on a fresh project (Build Profiles own the platform state), so the primary route
    /// replays what the Build Profiles window's own Switch Platform button does for a classic
    /// platform: BuildProfileContext.instance.GetForClassicPlatform(target, subtarget) →
    /// EditorUserBuildSettings.SwitchActiveBuildTargetGuid(profile). The context lazily creates
    /// classic profiles for every installed platform on load, so no user-visible profile asset is
    /// needed. Those members are internal → reflection; on editors without the Build Profile
    /// system (&lt; 6000) the type is simply absent and everything degrades to the legacy API.
    /// </summary>
    internal static class BuildTargetSwitcher
    {
        private static PropertyInfo s_ContextInstance;
        private static MethodInfo s_GetForClassicPlatform;
        private static MethodInfo s_SwitchByGuid;
        private static bool s_Resolved;

        internal static bool ClassicProfileApiAvailable
        {
            get
            {
                ResolveApi();
                return s_ContextInstance != null && s_GetForClassicPlatform != null && s_SwitchByGuid != null;
            }
        }

        /// <summary>Pure routing decision — which action the Switch button performs.</summary>
        internal static SwitchRoute ResolveRoute(bool targetSupported, bool classicProfileAvailable)
        {
            if (!targetSupported) return SwitchRoute.ModuleMissing;
            return classicProfileAvailable ? SwitchRoute.ClassicProfile : SwitchRoute.LegacyAsync;
        }

        public static void Switch(BuildTargetGroup group, BuildTarget target)
        {
            var supported = BuildPipeline.IsBuildTargetSupported(group, target);
            switch (ResolveRoute(supported, ClassicProfileApiAvailable))
            {
                case SwitchRoute.ModuleMissing:
                    EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                        target + " Build Support is not installed for this Unity editor.\n\n" +
                        "Add the module in Unity Hub (Installs → ⚙ → Add modules), " +
                        "restart the editor, then switch again.", "OK");
                    return;

                case SwitchRoute.ClassicProfile:
                    if (TrySwitchViaClassicProfile(target)) return;
                    goto case SwitchRoute.LegacyAsync; // profile lookup came up empty — legacy API still applies

                case SwitchRoute.LegacyAsync:
                    if (!EditorUserBuildSettings.SwitchActiveBuildTargetAsync(group, target))
                        ShowManualFallback(target);
                    return;
            }
        }

        // Never throws: any reflection surprise logs and reports false so Switch() falls back.
        private static bool TrySwitchViaClassicProfile(BuildTarget target)
        {
            try
            {
                var context = s_ContextInstance.GetValue(null);
                if (context == null) return false;

                // Second arg is StandaloneBuildSubtarget (irrelevant for Android/iOS) — taken from
                // the resolved parameter type so we don't hardcode its namespace: 0 == Default.
                var subtargetType = s_GetForClassicPlatform.GetParameters()[1].ParameterType;
                var profile = s_GetForClassicPlatform.Invoke(context,
                    new[] { (object)target, Enum.ToObject(subtargetType, 0) });
                if (profile == null) return false;

                return (bool)s_SwitchByGuid.Invoke(null, new[] { profile });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CAS Hub] Classic-profile platform switch failed, " +
                                 "falling back to the legacy API: " + e.Message);
                return false;
            }
        }

        private static void ShowManualFallback(BuildTarget target)
        {
            var open = EditorUtility.DisplayDialog("CAS.AI Publishing Hub",
                "Couldn't switch the active platform to " + target + " automatically.\n\n" +
                "Switch it manually in the build window, then return to the Hub.",
                "Open Build Window", "Close");
            if (!open) return;
            if (!EditorApplication.ExecuteMenuItem("File/Build Profiles"))
                EditorApplication.ExecuteMenuItem("File/Build Settings...");
        }

        private static void ResolveApi()
        {
            if (s_Resolved) return;
            s_Resolved = true;

            var asm = typeof(EditorUserBuildSettings).Assembly;
            var context = asm.GetType("UnityEditor.Build.Profile.BuildProfileContext");
            var profileType = asm.GetType("UnityEditor.Build.Profile.BuildProfile");
            if (context == null || profileType == null) return; // pre-Build-Profiles editor

            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic;
            s_ContextInstance = context.GetProperty("instance", any | BindingFlags.Static);
            s_GetForClassicPlatform = context.GetMethods(any | BindingFlags.Instance).FirstOrDefault(m =>
                m.Name == "GetForClassicPlatform" &&
                m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType == typeof(BuildTarget) &&
                m.GetParameters()[1].ParameterType.IsEnum);
            s_SwitchByGuid = typeof(EditorUserBuildSettings).GetMethods(any | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "SwitchActiveBuildTargetGuid" &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == profileType &&
                    m.ReturnType == typeof(bool));
        }
    }
}
