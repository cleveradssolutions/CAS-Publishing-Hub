namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// Pure, testable mapping from a failed auto-install step (id/name + raw error) to the
    /// copy shown on <see cref="ProgressScreen"/>'s failure panel. No Unity, no I/O.
    /// </summary>
    internal sealed class ProgressFailureModel
    {
        public string Title;
        public string Message;
        public string Log;

        public static ProgressFailureModel From(string step, string error) => new ProgressFailureModel
        {
            Title = step + " — installation failed",
            Message = error + " Check your internet connection.",
            Log = error,
        };
    }
}
