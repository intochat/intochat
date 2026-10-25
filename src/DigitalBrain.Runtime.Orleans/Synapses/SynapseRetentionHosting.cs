using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Runtime.Orleans.Synapses;

/// <summary>Registers the synapse cursor registry and the retention policy.</summary>
public static class SynapseRetentionHosting
{
    internal static IServiceCollection AddRetentionOptions(IServiceCollection services)
    {
        services.AddOptions<RetentionOptions>().BindConfiguration(IntoChatOSNames.Signalling.Retention);
        return services;
    }
}
