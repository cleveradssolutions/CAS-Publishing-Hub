using UnityEngine.UIElements;

namespace CAS.Publishing.Installer.Wizard
{
    /// <summary>
    /// One wizard screen. <see cref="Root"/> is the screen's VisualElement (built from its
    /// own UXML); the router parents it into the content host and toggles its visibility.
    /// <see cref="OnEnter"/> fires each time the screen becomes visible — wire buttons once
    /// (guarded) and refresh any transient state there.
    /// </summary>
    internal interface IWizardScreen
    {
        string Id { get; }
        VisualElement Root { get; }
        void OnEnter(WizardRouter router);
    }
}
