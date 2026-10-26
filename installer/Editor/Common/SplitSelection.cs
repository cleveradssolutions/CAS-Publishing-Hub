using System.Collections.Generic;

namespace CAS.Publishing.Installer.Common
{
    /// <summary>
    /// All-or-none selection for split-migration groups: toggling any member ticks or unticks
    /// the whole group, so a user can never apply a partial split (which would strip a legacy
    /// package without installing all its replacements).
    /// </summary>
    public static class SplitSelection
    {
        public static void SetGroup(List<string> selected, IReadOnlyList<string> members, bool select)
        {
            if (selected == null || members == null) return;
            foreach (var id in members)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (select)
                {
                    if (!selected.Contains(id)) selected.Add(id);
                }
                else
                {
                    selected.Remove(id);
                }
            }
        }
    }
}
