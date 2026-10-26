using System.Runtime.CompilerServices;

// Grant the EditMode test assembly access to internal types (e.g. CatalogUpdater)
// so they can be unit-tested without widening the package's public API surface.
[assembly: InternalsVisibleTo("CAS.Publishing.Installer.Editor.Tests")]

// Grant the wizard UI assembly access to internal types (e.g. CatalogLoader) so it can
// read the live catalog + scan without widening the public API surface.
[assembly: InternalsVisibleTo("CAS.Publishing.Installer.Wizard.Editor")]
