using DigitalBrain.Modules;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.LocalDisk;

[DigitalBrain.Modules.Module(ManifestName)]
public sealed class LocalDiskModule : DigitalBrain.Modules.Module, IHttpModule
{
    internal const string ManifestName = "local-disk";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [DigitalBrain.Capabilities.Capability.Storage.V1],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);

    public void Configure(IEndpointRouteBuilder endpoints) => LocalDiskEndpoints.Map(endpoints);

    public static void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ConfinedDiskCoordinator>();
    }
}
