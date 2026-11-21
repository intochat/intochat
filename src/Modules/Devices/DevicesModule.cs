using System.Reflection;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Devices;

[DigitalBrain.Modules.Module(ManifestName)]
public sealed class DevicesModule : DigitalBrain.Modules.Module, IHttpModule
{
    internal const string ManifestName = "devices";

    internal static readonly string LegacyAiOllamaEndpointKey =
        ModuleConfiguration.ModuleOptionsSection(new ModuleId("ai")) + ":Ollama:Endpoint";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [Setting.Of<DevicesOptions>(ModuleConfiguration.ModuleOptionsSection(ManifestName))],
        Secrets: []);

    public static Assembly? Contracts => typeof(IDevice).Assembly;

    public void Configure(IEndpointRouteBuilder endpoints) => DevicesEndpoints.Map(endpoints);

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ModuleConfiguration.ModuleOptionsSection(ManifestName));
        services.AddOptions<DevicesOptions>()
            .Bind(section)
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(section["Ollama:Endpoint"]))
                {
                    var legacy = configuration[LegacyAiOllamaEndpointKey];
                    if (!string.IsNullOrWhiteSpace(legacy))
                    {
                        options.Ollama.Endpoint = legacy.Trim();
                    }
                }
            })
            .Validate(static options =>
            {
                options.Validate();
                return true;
            })
            .ValidateOnStart();

        services.TryAddSingleton<IHardwareProbe, HardwareProbe>();
    }
}
