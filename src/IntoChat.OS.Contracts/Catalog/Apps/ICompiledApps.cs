namespace IntoChat.OS.Modules.Apps;

// One compiled app the host ships: its name and its manifest's title (empty when the manifest has none).
public sealed record CompiledApp(string Name, string Title);

// The compiled apps of the host, for code outside the catalog that only lists them (the brain's topology): every app of every
// assembly the composition registered as CompiledAppsRegistration, then of the catalog's own, in that order. An app two
// assemblies ship is listed twice. The catalog's own reading of them (bundles, origins, screen types) stays in the catalog.
public interface ICompiledApps
{
    IReadOnlyList<CompiledApp> List();
}
