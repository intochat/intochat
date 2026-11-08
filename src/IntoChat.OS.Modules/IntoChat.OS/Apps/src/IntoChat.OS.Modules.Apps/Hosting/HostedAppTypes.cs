using DigitalBrain.Runtime.Apps;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.Apps;

// Answers IHostedAppTypes from what the in-process host actually assigned, against the registered compiled-app assemblies.
internal sealed class HostedAppTypes(InProcessAppHost host, IEnumerable<CompiledAppsRegistration> registrations) : IHostedAppTypes
{
    private readonly HashSet<System.Reflection.Assembly> _assemblies = [.. registrations.Select(registration => registration.Assembly)];

    public bool IsHostLoaded(string appKey)
        => !string.IsNullOrWhiteSpace(appKey) && host.TryGetTurns(appKey)?.App is { } app && _assemblies.Contains(app.GetType().Assembly);
}
