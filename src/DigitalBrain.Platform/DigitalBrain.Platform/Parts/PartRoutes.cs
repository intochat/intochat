using System.Reflection;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;

namespace DigitalBrain.Platform.Parts;

// T20-9: the grain classes whose state is a part's log — every Neuron<TState> and every grain with cluster
// persistent state — read once from the silo's grain classes. Machine-local grains (secrets, integrations,
// compute) are never routed; a routed grain's part is PartId.Of(its key).
internal sealed class PartRoutes
{
    private readonly HashSet<GrainType> _logged;

    public PartRoutes(IOptions<GrainTypeOptions> types, GrainTypeResolver resolver)
    {
        _logged = [.. types.Value.Classes.Where(IsLogged).Select(resolver.GetGrainType)];
    }

    public bool IsPartLogged(GrainType type) => _logged.Contains(type);

    // The part a routed grain belongs to; null for a grain that is not part-logged or keyed outside every brain.
    public PartId? PartOf(GrainId grain) => IsPartLogged(grain.Type) ? KeyedPart(grain) : null;

    // The part a grain's key names, routed or not; null for a key outside every brain.
    public static PartId? KeyedPart(GrainId grain)
    {
        var key = grain.Key.ToString();
        try { return key is null ? null : PartId.Of(key); }
        catch (ArgumentException) { return null; }
    }

    private static bool IsLogged(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Neuron<>)) { return true; }
        }
        return type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.GetCustomAttribute<PersistentStateAttribute>() is { } state
                && state.StorageName == DigitalBrainNames.ClusterGrainStorage);
    }
}
