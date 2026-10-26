using UnityEditor;
using UnityEngine;

namespace CAS.Publishing.Installer.Telemetry
{
    /// <summary>Local-testing switches: send events to a collector on this machine, and send right away.</summary>
    internal static class TelemetryDebugMenu
    {
        private const string LocalCollector = "http://127.0.0.1:8787";
        private const string LocalMenuPath = "Assets/CleverAdsSolutions/Hub Debug/Telemetry: Use Local Collector";

        [MenuItem(LocalMenuPath)]
        private static void ToggleLocal()
        {
            var on = HubTelemetry.EndpointOverride != LocalCollector;
            HubTelemetry.EndpointOverride = on ? LocalCollector : null;
            Debug.Log("[CAS Hub] Telemetry endpoint: " + (on ? LocalCollector : "from catalog"));
        }

        [MenuItem(LocalMenuPath, true)]
        private static bool ToggleLocalValidate()
        {
            UnityEditor.Menu.SetChecked(LocalMenuPath, HubTelemetry.EndpointOverride == LocalCollector);
            return true;
        }

        [MenuItem("Assets/CleverAdsSolutions/Hub Debug/Telemetry: Send Queued Now")]
        private static void SendNow()
        {
            var settings = HubTelemetry.ActiveSettings();
            if (settings == null)
            {
                Debug.LogWarning("[CAS Hub] Telemetry is off: no endpoint in the catalog and no override.");
                return;
            }
            HubTelemetry.FlushBlocking();
            Debug.Log("[CAS Hub] Telemetry flushed to " + settings.Endpoint);
        }
    }
}
