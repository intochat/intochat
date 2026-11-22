using DigitalBrain.Modules;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps;

[Module("os-apps")]
public sealed class ShippedShellAppsModule : Module
{
    public static ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton(new CompiledAppsRegistration(typeof(ShippedShellAppsModule).Assembly));
    }
}
