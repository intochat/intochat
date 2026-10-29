using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Microsoft.Orleans;
using IntoChat.OS.Modules.Microsoft.Orleans.Cluster;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Kernel.Composition;

// The Orleans host wiring the former Microsoft/Orleans module carried (06-02, R4-12): the cloud topology implementation,
// the deployment's grain-state container on cloud, and the legacy cluster neuron's membership feed until 06-03 moves it.
public static class OrleansHostWiring
{
    // The cloud topology comes from DigitalBrain.Runtime.Orleans.Azure, which only the composition references. The production
    // path never replaces the Key Vault source (T-03-59).
    public static IReadOnlyList<DigitalBrain.Runtime.Orleans.ITopologyProfile> TopologyProfiles { get; } =
        [DigitalBrain.Runtime.Orleans.Azure.CloudTopologyExtensions.AddCloudTopology()];

    public static void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (CloudStorage.IsCloud(services)) { CloudStorage.ConfigureServices(services); }
        if (services.Any(descriptor => !descriptor.IsKeyedService && descriptor.ImplementationInstance is
            DigitalBrain.Runtime.Orleans.ITopologyProfile { Topology: DigitalBrain.Runtime.Orleans.Topology.Local or DigitalBrain.Runtime.Orleans.Topology.LocalCloud }))
        {
            services.TryAddSingleton<Operations.ILocalProcessProbe, Operations.SystemLocalProcessProbe>();
            services.TryAddSingleton<DigitalBrain.Runtime.Orleans.Operations.IOperationDriverLifetime>(provider =>
                new Operations.LocalOperationDriverLifetime(provider.GetService<IntoChat.OS.Framework.Machine.IMachineIdentity>(),
                    provider.GetRequiredService<Operations.ILocalProcessProbe>(),
                    provider.GetService<Microsoft.Extensions.Logging.ILogger<Operations.LocalOperationDriverLifetime>>()));
        }
        services.TryAddSingleton<IClusterMembershipFeed, ClusterMembershipFeed>();
        services.AddOptions<global::Orleans.Hosting.ReminderOptions>().Configure<Microsoft.Extensions.Configuration.IConfiguration>((options, configuration) =>
        {
            var period = configuration.GetValue<TimeSpan?>(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ModuleOptionsSection("time") + ":MinimumReminderPeriod");
            if (period is { } value && value > TimeSpan.Zero) { options.MinimumReminderPeriod = value; }
        });
    }
}
