using Microsoft.Extensions.DependencyInjection;
using System.Collections.Frozen;
using IntoChat.OS.Authority;
using IntoChat.OS.Connections.Platform.Connections;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-06c, D-04): Authority's raw grains stay grains and are reached only here. An interface passes when a
// concrete grain class in the Authority assembly implements it; anything else is refused, so the accessor cannot reach a module's neurons (T-06-22). Shared
// bases every grain implements (Orleans' key markers, the kernel's INeuron and the other IntoChat.OS contracts) do not name an
// Authority grain and are refused too.
internal sealed class AuthorityGrains(IGrainFactory grains, IServiceProvider services) : IAuthorityGrains
{
    // A closed set, fixed at type load: the interfaces of the Authority assembly's concrete grain classes.
    private static readonly FrozenSet<Type> Contracts = typeof(IAuthorityGrains).Assembly.GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(Grain).IsAssignableFrom(type))
        .SelectMany(type => type.GetInterfaces())
        .Where(contract => contract.Assembly != typeof(IGrain).Assembly && contract.Assembly != typeof(INeuron).Assembly)
        .ToFrozenSet();

    public T Get<T>(string key) where T : IGrainWithStringKey
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!IsAllowListed(typeof(T)))
        { throw new InvalidOperationException($"{typeof(T).FullName} is not served by an allow-listed Authority grain."); }
        return grains.GetGrain<T>(key);
    }

    public IBrain Brain(string brainId)
    {
        ArgumentException.ThrowIfNullOrEmpty(brainId);
        // Resolved per call: the plain seam depends on the call filter, whose grant stage reads identity through Authority.
        return services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>().Get<IBrain>(IBrain.Key(brainId));
    }

    // Connections are plain neurons (06-08, R4-25): the three lookups go through the plain seam by the key's connector.
    private IntoChat.OS.Runtime.IPlainNeurons Neurons => services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>();

    public T Plain<T>(string fullKey) where T : class => Neurons.Get<T>(fullKey);

    public IConnection Connection(string connectionKey) => Neurons.GetConnection(connectionKey);

    public IConnectionInbound ConnectionInbound(string connectionKey) => Neurons.GetConnectionInbound(connectionKey);

    public T CallerConnection<T>(IServiceProvider services, string connectionKey) where T : class
        => Neurons.GetNeuron<T>(services, connectionKey);

    internal static bool IsAllowListed(Type contract) => Contracts.Contains(contract);
}
