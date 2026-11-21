using System.Reflection;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Modules.Devices.Runtimes.FoundryLocal;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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

        DigitalBrain.Invocation.RuntimeProtocol.Register(DevicesJson.Default);
        DigitalBrain.Invocation.RuntimeProtocol.Register(DeviceEnrollmentJson.Default);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDeviceEnrollmentRedeemer, DeviceEnrollmentRedeemer>();
        services.TryAddSingleton<IHardwareProbe, HardwareProbe>();
        services.TryAddSingleton<DeviceInstaller>();
        services.AddHostedService(static sp => sp.GetRequiredService<DeviceInstaller>());
        services.AddHostedService<DevicePrefetch>();
        services.AddHttpClient("devices.ollama", static client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient("devices.openai-compat");

        var initial = section.Get<DevicesOptions>() ?? new DevicesOptions();
        if (initial.Ollama.Enabled)
        {
            services.AddSingleton<ILocalRuntime>(static sp => new OllamaRuntime(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("devices.ollama"),
                sp.GetRequiredService<IOptionsMonitor<DevicesOptions>>(),
                sp.GetRequiredService<TimeProvider>()));
        }

        if (initial.FoundryLocal.Enabled)
        {
            services.TryAddSingleton<IFoundryLocalApi, SdkFoundryLocalApi>();
            services.TryAddSingleton<FoundryLocalHost>();
            services.AddSingleton<ILocalRuntime>(static sp => new FoundryLocalRuntime(
                sp.GetRequiredService<FoundryLocalHost>(),
                sp.GetRequiredService<IHardwareProbe>()));
        }

        foreach (var (name, entry) in initial.Attach)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Url))
            {
                continue;
            }
            var runtimeName = name;
            var endpoint = DevicesOptions.ValidateHttpUrl(entry.Url, $"Attach['{runtimeName}'].Url");
            services.AddSingleton<ILocalRuntime>(sp => new OpenAiCompatRuntime(
                runtimeName,
                endpoint,
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("devices.openai-compat")));
        }
    }
}
