namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>Pure mapping between the installer's Optimal/Families choice and CAS's
    /// <c>audienceTagged</c> int (enum Audience: Mixed=0, Children=1, NotChildren=2).</summary>
    internal static class CasAudience
    {
        public const int Mixed = 0;
        public const int Children = 1;
        public const int NotChildren = 2;

        /// <summary>Families → Children, Optimal → NotChildren.</summary>
        public static int ForFamilies(bool families) => families ? Children : NotChildren;

        /// <summary>True when the stored audience is the Families (children) value.</summary>
        public static bool IsFamilies(int audience) => audience == Children;
    }
}
