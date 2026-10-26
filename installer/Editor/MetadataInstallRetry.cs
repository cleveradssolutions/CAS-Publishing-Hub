using UnityEditor;
using UnityEditor.PackageManager;

namespace CAS.Publishing.Installer
{
    /// <summary>
    /// Self-heal for a TRANSIENT metadata-install failure ("exclusive access" while the Package
    /// Manager is busy during a fresh project import). The old behaviour waited for "the next
    /// domain reload" — which a quiet editor never produces, stranding metadata (and the Hub
    /// auto-open) until an editor restart. Armed only by <see cref="MetadataAutoInstall"/> on a
    /// transient failure; re-runs the install when the Package Manager finishes whatever it was
    /// doing (<see cref="Events.registeredPackages"/>) or after a short timer, whichever first.
    /// Capped per domain-reload epoch (statics reset on reload; every reload re-enters Bootstrap).
    /// </summary>
    internal static class MetadataInstallRetry
    {
        private const int MaxAttempts = 5;
        private const double DelaySeconds = 5.0;

        private static int _attempts;
        private static bool _armed;
        private static double _nextAt;

        internal static bool IsArmed => _armed;
        internal static int Attempts => _attempts;

        internal static void Arm()
        {
            if (_armed || _attempts >= MaxAttempts) return;
            _armed = true;
            _attempts++;
            _nextAt = EditorApplication.timeSinceStartup + DelaySeconds;
            EditorApplication.update += Tick;
            Events.registeredPackages += OnPackagesChanged;
        }

        private static void OnPackagesChanged(PackageRegistrationEventArgs args) => TriggerNow();

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup < _nextAt) return;
            TriggerNow();
        }

        private static void TriggerNow()
        {
            Disarm();
            MetadataAutoInstall.Run(); // idempotent: no-ops once metadata is installed
        }

        private static void Disarm()
        {
            if (!_armed) return;
            _armed = false;
            EditorApplication.update -= Tick;
            Events.registeredPackages -= OnPackagesChanged;
        }

        internal static void DisarmForTests() => Disarm();

        internal static void ResetForTests()
        {
            Disarm();
            _attempts = 0;
        }
    }
}
