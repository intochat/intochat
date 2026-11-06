using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Signals;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Os.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Os;

[Module("os")]
public sealed class OsModule : Module
{
    public const string Id = "os";

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);

    public static System.Reflection.Assembly? Contracts { get; } = typeof(IOsModule).Assembly;

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // System neurons and core capabilities
        services.TryAddSingleton<IInstalledModules, InstalledModulesNeuron>();
        services.TryAddSingleton<IUserSettings, UserSettingsNeuron>();
        services.TryAddSingleton<IProfile, ProfileNeuron>();
    }
}
