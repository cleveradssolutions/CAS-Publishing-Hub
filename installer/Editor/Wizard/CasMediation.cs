using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Activates a CAS mediation solution BY NAME via reflection into CAS's editor
    /// <c>DependencyManager</c> — PublishingAds and TenjinMMP as well as the older
    /// OptimalAds / FamiliesAds. Best-effort: any missing CAS type/member logs a warning and returns
    /// false, so a CAS API change never throws — the installer's asset-field writes still apply.
    /// The reflected members (Create / solutions / Dependency.name / ActivateDependencies) must be
    /// confirmed in Unity by the owner; this code never assumes they exist.
    /// </summary>
    internal static class CasMediation
    {
        // Entry names in CAS<Platform>Mediation.list (the "simple" array). Verified on 4.8.1-beta2:
        // PublishingAds = id 64, TenjinMMP = id 66. Solutions are independent — enabling one does not
        // switch the others off, so moving Optimal -> Publishing needs an explicit Disable.
        public const string PublishingAds = "PublishingAds";
        public const string TenjinMmp = "TenjinMMP";
        public const string OptimalAds = "OptimalAds";
        public const string FamiliesAds = "FamiliesAds";

        /// <summary>
        /// Enables or disables ONE CAS solution by name, mirroring its own per-solution checkboxes.
        /// Best-effort: a missing CAS type or member logs a warning and returns false, never throws.
        /// Neighbouring solutions are left alone.
        /// </summary>
        public static bool SetSolution(BuildTarget platform, string solutionName, int audience, bool enable)
        {
            try
            {
                var dmType = FindType("DependencyManager");
                if (dmType == null) return Warn("DependencyManager type not found");

                var audienceType = FindType("Audience");
                if (audienceType == null) return Warn("Audience type not found");
                var audienceVal = Enum.ToObject(audienceType, audience);

                var create = dmType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(BuildTarget), audienceType, typeof(bool) }, null);
                if (create == null) return Warn("DependencyManager.Create(BuildTarget,Audience,bool) not found");

                var manager = create.Invoke(null, new object[] { platform, audienceVal, false });
                if (manager == null) return Warn("DependencyManager.Create returned null");

                var solutionsMember = dmType.GetField("solutions") != null
                    ? (MemberInfo)dmType.GetField("solutions")
                    : dmType.GetProperty("solutions");
                var solutions = GetValue(solutionsMember, manager) as Array;
                if (solutions == null) return Warn("solutions not found");

                var wantName = solutionName;
                object target = null;
                foreach (var sol in solutions)
                {
                    if (sol == null) continue;
                    var nameMember = (MemberInfo)sol.GetType().GetField("name") ?? sol.GetType().GetProperty("name");
                    if (GetValue(nameMember, sol) as string == wantName) { target = sol; break; }
                }
                if (target == null) return Warn($"solution '{wantName}' not present");

                var methodName = enable ? "ActivateDependencies" : "DisableDependencies";
                var method = target.GetType().GetMethod(methodName);
                if (method == null) return Warn("Dependency." + methodName + " not found");
                method.Invoke(target, new[] { (object)platform, manager });

                AssetDatabase.Refresh();
                RefreshInspector();
                return true;
            }
            catch (Exception e)
            {
                return Warn("reflection error: " + e.Message);
            }
        }

        /// <summary>
        /// Best-effort read: true when the named solution is installed
        /// (IsInstalled() == installedVersion present). Returns false on any reflection failure.
        /// </summary>
        public static bool IsSolutionInstalled(BuildTarget platform, string solutionName) =>
            TryIsSolutionInstalled(platform, solutionName, out var installed) && installed;

        /// <summary>
        /// Like <see cref="IsSolutionInstalled"/>, but tells "not installed" apart from "can't tell":
        /// returns false when CAS (or the reflected API) is missing, so a rule built on it can stay
        /// out of the way instead of declaring CAS unconfigured forever after a CAS API change.
        /// </summary>
        public static bool TryIsSolutionInstalled(BuildTarget platform, string solutionName, out bool installed)
        {
            installed = false;
            try
            {
                var dmType = FindType("DependencyManager");
                var audienceType = FindType("Audience");
                if (dmType == null || audienceType == null) return false;

                var create = dmType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(BuildTarget), audienceType, typeof(bool) }, null);
                if (create == null) return false;

                var manager = create.Invoke(null, new object[] { platform, Enum.ToObject(audienceType, 0), false });
                if (manager == null) return false;

                var solutionsMember = dmType.GetField("solutions") != null
                    ? (MemberInfo)dmType.GetField("solutions")
                    : dmType.GetProperty("solutions");
                if (!(GetValue(solutionsMember, manager) is Array solutions)) return false;

                foreach (var sol in solutions)
                {
                    if (sol == null) continue;
                    var nameMember = (MemberInfo)sol.GetType().GetField("name") ?? sol.GetType().GetProperty("name");
                    if (GetValue(nameMember, sol) as string != solutionName) continue;
                    var isInstalled = sol.GetType().GetMethod("IsInstalled");
                    if (isInstalled == null) return false;
                    installed = isInstalled.Invoke(sol, null) is bool b && b;
                    return true;
                }
                return false; // this CAS has no such solution — nothing to require
            }
            catch { return false; }
        }

        /// <summary>
        /// Whether CAS has Tenjin MMP enabled for <paramref name="platform"/> ("Android"/"iOS") —
        /// the part of the Placements step that a project configured before the Hub never got:
        /// formats chosen, so Placements was skipped, and with it the solutions it enables. True when
        /// it can't be read (no CAS, an API change), so the rule never blocks the flow on a guess.
        /// </summary>
        public static bool TenjinMmpReady(string platform)
        {
            var target = platform == "iOS" ? BuildTarget.iOS : BuildTarget.Android;
            return !TryIsSolutionInstalled(target, TenjinMmp, out var installed) || installed;
        }

        // Type lookups walk every loaded assembly; the configuration rules ask on every redraw. A
        // domain reload (any CAS install or removal) clears the cache.
        private static readonly System.Collections.Generic.Dictionary<string, Type> TypeCache =
            new System.Collections.Generic.Dictionary<string, Type>();

        private static Type FindType(string simpleName)
        {
            if (TypeCache.TryGetValue(simpleName, out var cached)) return cached;
            var found = FindTypeUncached(simpleName);
            TypeCache[simpleName] = found;
            return found;
        }

        private static Type FindTypeUncached(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                    if (t.Name == simpleName && t.Namespace != null && t.Namespace.StartsWith("CAS"))
                        return t;
            }
            return null;
        }

        private static object GetValue(MemberInfo m, object target) =>
            m is FieldInfo f ? f.GetValue(target) : (m as PropertyInfo)?.GetValue(target);

        private static void RefreshInspector()
        {
            try { ActiveEditorTracker.sharedTracker.ForceRebuild(); }
            catch { /* refresh is cosmetic — the XML on disk is already correct */ }
        }

        private static bool Warn(string why)
        {
            // Don't claim the formats were written: that is a separate call which may have failed
            // for the same reason (no CAS in the project). The old wording lied in that case.
            Debug.LogWarning("[CAS Hub] CAS mediation solution not applied (" + why +
                "). If CAS is installed, pick the solution in CAS settings; otherwise install CAS first.");
            return false;
        }
    }
}
