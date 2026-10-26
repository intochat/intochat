using System.Collections.Frozen;
using IntoChat.OS.Authority;
using IntoChat.OS.Connections.Platform.Connections;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-06c, D-04): Authority's raw grains stay grains and are reached only here. An interface passes when a
// concrete grain class in the Authority assembly implements it, or it is the kernel brain grain (IBrain, served by the
// allow-listed BrainNeuron); anything else is refused, so the accessor cannot reach a module's neurons (T-06-22). Shared
// bases every grain implements (Orleans' key markers, the kernel's INeuron and the other IntoChat.OS contracts) do not name an
// Authority grain and are refused too.
internal sealed class AuthorityGrains(IGrainFactory grains) : IAuthorityGrains
{
    // A closed set, fixed at type load: the interfaces of the Authority assembly's concrete grain classes, plus IBrain.
    private static readonly FrozenSet<Type> Contracts = typeof(IAuthorityGrains).Assembly.GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(Grain).IsAssignableFrom(type))
        .SelectMany(type => type.GetInterfaces())
        .Where(contract => contract.Assembly != typeof(IGrain).Assembly && contract.Assembly != typeof(INeuron).Assembly)
        .Append(typeof(IBrain)).ToFrozenSet();

    public T Get<T>(string key) where T : IGrainWithStringKey
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!IsAllowListed(typeof(T)))
        { throw new InvalidOperationException($"{typeof(T).FullName} is not served by an allow-listed Authority grain."); }
        return grains.GetGrain<T>(key);
    }

    public IConnection Connection(string connectionKey) => grains.GetConnection(connectionKey);

    public IConnectionInbound ConnectionInbound(string connectionKey) => grains.GetConnectionInbound(connectionKey);

    public T CallerConnection<T>(IServiceProvider services, string connectionKey) where T : IGrainWithStringKey
        => grains.GetNeuronGrain<T>(services, connectionKey);

    internal static bool IsAllowListed(Type contract) => Contracts.Contains(contract);
}
