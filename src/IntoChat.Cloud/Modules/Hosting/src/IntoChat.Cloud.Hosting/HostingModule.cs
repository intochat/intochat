using DigitalBrain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IntoChat.Cloud.Hosting;

// Hosted-allocation options for the kernel running Topology=cloud. Per-allocation grain storage and reminder tables stay out of
// this module: they are Orleans grain state, and the owner decision is that a cloud module does not carry that store.
[Module(Id)]
public sealed class HostingModule : Module
{
    public const string Id = "hosting";
    public const string OptionsSection = "IntoChatOS:Modules:" + Id + ":Options";

    public static bool LoadsUnder(string? topology) => string.Equals(topology?.Trim(), "cloud", StringComparison.OrdinalIgnoreCase);

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [Need.Http],
        Settings: [Setting.Of<HostedRuntimeOptions>(OptionsSection)],
        Secrets: []);

    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<HostedRuntimeOptions>().BindConfiguration(OptionsSection);
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<HostedRuntimeOptions>>().Value);
        services.AddSingleton<IHostedService, HostedStorageGuardStartup>();
    }
}

public sealed class HostedRuntimeOptions
{
    public string AccountId { get; set; } = "";
    public string BrainId { get; set; } = "";
    public string RuntimeId { get; set; } = "";
    public string ControllerCertificateSha256 { get; set; } = "";
    public bool RecoveryOnly { get; set; }

    public bool IsHostedAllocation => !string.IsNullOrWhiteSpace(RuntimeId);
}

// A configured Orleans grain-state account together with a hosted runtime id is refused. The journal is not a silent replacement
// of that store, and existing authority and secrets stay where they are.
public static class HostedStorageGuard
{
    public const string RuntimeIdKey = "Hosting:RuntimeId";
    public const string GrainStateKey = "IntoChatOS:Cloud:Storage";
    public const string MigrationMessage = "Hosted journal storage requires an explicit migration from the configured Orleans grain-state store; existing authority and secrets must not be replaced.";

    public static void Ensure(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var runtimeId = configuration[RuntimeIdKey];
        var store = configuration[GrainStateKey];
        if (!string.IsNullOrWhiteSpace(runtimeId) && !string.IsNullOrWhiteSpace(store))
        {
            throw new InvalidOperationException(MigrationMessage);
        }
    }
}

internal sealed class HostedStorageGuardStartup(IConfiguration configuration) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        HostedStorageGuard.Ensure(configuration);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
