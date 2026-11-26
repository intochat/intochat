namespace IntoChat.OS.Modules.Apps;

// The compiled apps as ICompiledApps lists them: the registered assemblies, then the catalog's own, each read by CompiledApps.
internal sealed class CompiledAppsList(IEnumerable<CompiledAppsRegistration> registrations) : ICompiledApps
{
    public IReadOnlyList<CompiledApp> List()
        => [.. registrations.Select(registration => registration.Assembly).Append(typeof(AppsModule).Assembly).Distinct()
            .SelectMany(CompiledApps.Of)
            .Select(compiled => new CompiledApp(compiled.Bundle.Name, compiled.Bundle.Manifest.Title))];
}
