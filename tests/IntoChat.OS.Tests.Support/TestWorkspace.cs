using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Modules.LocalDisk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Tests.Support;

// The brain workspace (the cloud module's local-cloud/main) on a test host: its folders under a fresh temp folder, never
// the user's home, and an empty stand-in for the old temp fallback so nothing on this machine is copied in.
public static class TestWorkspace
{
    public static IServiceCollection AddTestWorkspace(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var root = Directory.CreateTempSubdirectory("intochat-workspace-test-").FullName;
        services.TryAddSingleton(new BrainWorkspaceRoots(Path.Combine(root, "home"), Path.Combine(root, "temp")));
        services.TryAddSingleton<ConfinedDiskCoordinator>();
        services.AddJournalPayloads(typeof(CloudModule).Assembly);
        return services;
    }
}
