using System.Reflection;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Devices.Runtimes.FoundryLocal;
using IntoChat.OS.Modules.Devices.Runtimes.SherpaOnnx;
using IntoChat.OS.Modules.Devices.Voice;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
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

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        DevicesEndpoints.Map(endpoints);
        EnrollmentEdgeEndpoints.Map(endpoints);
    }

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

                if (string.IsNullOrWhiteSpace(options.SherpaOnnx.ModelDirectory))
                {
                    var home = StateHome.Resolve(
                        configuration,
                        new RuntimeOptions { HomeKey = IntoChatOSNames.HomeKey, DefaultHomeDirectoryName = ".intochat" }).Path;
                    options.SherpaOnnx.ModelDirectory = Path.Combine(home, "models", "sherpa-onnx");
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
        DigitalBrain.Invocation.RuntimeProtocol.Register(DeviceWireJson.Default);
        DigitalBrain.Invocation.RuntimeProtocol.Register(DeviceLinkJson.Default);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMachineKeyProtector>(static _ => MachineKeyProtectors.ForCurrentPlatform());
        services.TryAddSingleton(sp =>
        {
            var home = StateHome.Resolve(
                sp.GetService<IConfiguration>() ?? configuration,
                sp.GetService<RuntimeOptions>() ?? new RuntimeOptions { HomeKey = IntoChatOSNames.HomeKey, DefaultHomeDirectoryName = ".intochat" });
            return new DeviceCredentials(
                sp.GetRequiredService<IMachineKeyProtector>(),
                Path.Combine(home.Path, "devices", "link.bin"));
        });
        services.AddHttpClient("devices.cloud", static client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.TryAddSingleton(static sp => new CloudClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("devices.cloud"),
            sp.GetRequiredService<DeviceCredentials>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(static sp => new CloudRelayServiceClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("devices.cloud"),
            sp.GetService<IServiceAssertionSigner>()));
        services.TryAddSingleton<IDeviceEnrollmentRedeemer, DeviceEnrollmentRedeemer>();
        services.TryAddSingleton<IDeviceChatRoute>(static sp => new RelayDeviceChatRoute(
            sp.GetRequiredService<CloudClient>(),
            sp.GetRequiredService<DeviceCredentials>(),
            sp.GetRequiredService<DigitalBrain.Identity.Local.Machine.MachineId>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<CloudRelayServiceClient>()));
        services.TryAddSingleton<IConnectedComputeTransport>(static sp => new RelayConnectedComputeTransport(
            sp.GetRequiredService<CloudClient>(),
            sp.GetRequiredService<DeviceCredentials>(),
            sp.GetService<IOptionsMonitor<DevicesOptions>>(),
            sp.GetService<CloudRelayServiceClient>()));
        services.TryAddSingleton<IConnectedComputeRouter>(static sp => new ConnectedComputeRouter(
            sp.GetRequiredService<CloudClient>(),
            sp.GetRequiredService<DeviceCredentials>(),
            sp.GetRequiredService<DigitalBrain.Identity.Local.Machine.MachineId>(),
            sp.GetService<IOptionsMonitor<DevicesOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IConnectedComputeTransport>(),
            sp.GetService<global::Microsoft.Extensions.Logging.ILogger<ConnectedComputeRouter>>(),
            sp.GetService<CloudRelayServiceClient>()));
        services.TryAddSingleton<IClientCodeSandboxConsent>(static sp =>
        {
            var initialAllow = sp.GetService<IOptionsMonitor<DevicesOptions>>()?.CurrentValue.Compute.AllowClientCodeSandbox
                ?? ClientComputeConsentPolicy.DefaultAllowClientCodeSandboxForCurrentPlatform();
            return new ClientCodeSandboxConsentStore(initialAllow);
        });
        services.TryAddSingleton<IHardwareProbe, HardwareProbe>();
        services.TryAddSingleton<DeviceInstaller>();
        services.AddHostedService(static sp => sp.GetRequiredService<DeviceInstaller>());
        services.TryAddSingleton<DeviceWorker>();
        services.AddHostedService(static sp => sp.GetRequiredService<DeviceWorker>());
        services.AddHostedService<DevicePrefetch>();
        services.AddHttpClient("devices.ollama", static client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient("devices.openai-compat");
        services.AddHttpClient(SherpaOnnxRuntime.HttpClientName, static client => client.Timeout = Timeout.InfiniteTimeSpan);

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

        if (initial.SherpaOnnx.Enabled)
        {
            services.TryAddSingleton<ISherpaApi, SherpaApi>();
            services.TryAddSingleton<SherpaOnnxRuntime>();
            services.AddSingleton<ILocalRuntime>(static sp => sp.GetRequiredService<SherpaOnnxRuntime>());
            services.TryAddSingleton<IVoiceActivityDetector>(static sp => sp.GetRequiredService<SherpaOnnxRuntime>());
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
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("devices.openai-compat"),
                sp.GetRequiredService<TimeProvider>()));
        }
    }
}
