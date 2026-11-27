using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Secrets;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Modules.Identity.Hosting;

namespace IntoChat.OS.Kernel.Platform;

// What the Connections assembly adds to a host the Framework composed (D-18): the identity-backed brain membership,
// the connector type entries, secret redaction and the neuron type search.
// AddIntoChatOSServer and the MCP host call it; a bare Framework silo has none of them (membership fails closed).
public static class ConnectionsHosting
{
    public static IServiceCollection AddIntoChatOSConnections(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIdentityBrainMembership();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleTypeSource, ConnectionTypeSource>());
        services.TryAddSingleton<ISecretRedactor, SecretRedactor>();
        services.TryAddSingleton<IConnectionBindings, NoConnectionBindings>();
        services.TryAddSingleton(NeuronTypeSearch.Create);
        services.TryAddSingleton<DigitalBrain.Runtime.Http.Surface.INeuronContractSearch>(sp => sp.GetRequiredService<NeuronTypeSearch>());
        return services;
    }

    public static ISiloBuilder AddIntoChatOSConnections(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.AddIntoChatOSConnections();
        return silo;
    }
}
