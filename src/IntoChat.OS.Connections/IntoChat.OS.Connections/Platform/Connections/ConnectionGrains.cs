using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;

namespace IntoChat.OS.Framework.Platform.Connections;

// Every connector's neuron implements IConnection, so a contract alone names no grain type once two
// connectors are composed. The key does: each connection neuron is [GrainType(connectorId)].
public static class ConnectionGrains
{
    public static GrainId IdFor(string key)
        => GrainId.Create(ConnectionKeys.Parse(key).ConnectorId, key);

    public static T GetConnectionGrain<T>(this IGrainFactory grains, string key) where T : IAddressable
    {
        ArgumentNullException.ThrowIfNull(grains);
        return grains.GetGrain<T>(IdFor(key));
    }

    public static IConnection GetConnection(this IGrainFactory grains, string key) => grains.GetConnectionGrain<IConnection>(key);

    public static IConnectionInbound GetConnectionInbound(this IGrainFactory grains, string key) => grains.GetConnectionGrain<IConnectionInbound>(key);

    // Addressing by a caller-supplied key: a canonical connection key resolves by its connector, but
    // only when the cluster's manifest says that grain type is a connection neuron, so a crafted key
    // can never activate an arbitrary grain type. Any other key, or a malformed connection key,
    // resolves by the contract's default grain type and the neuron's own Key refuses it.
    public static T GetNeuronGrain<T>(this IGrainFactory grains, IServiceProvider services, string key) where T : IGrainWithStringKey
    {
        ArgumentNullException.ThrowIfNull(grains);
        ArgumentNullException.ThrowIfNull(services);
        if (!ConnectionKeys.Is(key)) { return grains.GetGrain<T>(key); }
        string connector;
        try { connector = ConnectionKeys.Parse(key).ConnectorId; }
        catch (ArgumentException) { return grains.GetGrain<T>(key); }
        if (!IsConnectionGrainType(services, connector))
        { throw new ArgumentException($"No connection neuron serves connector '{connector}'.", nameof(key)); }
        return grains.GetGrain<T>(GrainId.Create(connector, key));
    }

    public static bool IsConnectionGrainType(IServiceProvider services, string connector)
    {
        ArgumentNullException.ThrowIfNull(services);
        var connection = services.GetRequiredService<GrainInterfaceTypeResolver>().GetGrainInterfaceType(typeof(IConnection)).ToString();
        return services.GetRequiredService<GrainPropertiesResolver>().TryGetGrainProperties(GrainType.Create(connector), out var grain)
            && grain.Properties.Any(property => property.Key.StartsWith(WellKnownGrainTypeProperties.ImplementedInterfacePrefix, StringComparison.Ordinal)
                && property.Value == connection);
    }
}
