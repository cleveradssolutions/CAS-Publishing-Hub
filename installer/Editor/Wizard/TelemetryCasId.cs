using CAS.Publishing.Installer.Telemetry;
using UnityEditor;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>Hands telemetry the CAS manager id from the CAS settings asset — the reader lives
    /// in this assembly, the telemetry one below it.</summary>
    [InitializeOnLoad]
    internal static class TelemetryCasId
    {
        static TelemetryCasId() => HubTelemetry.CasIdProvider = CasIdApplier.ReadExisting;
    }
}
