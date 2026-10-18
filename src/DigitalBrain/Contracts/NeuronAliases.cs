using System.Reflection;

namespace DigitalBrain;

// A type's rename-safe name. Both attributes carry the same strings and inherit the same way: the nearest
// type in the base chain that declares either wins, and on one type [NeuronAlias] wins over the Orleans [Alias].
// A derived signal with no alias of its own therefore keeps its base class alias, as it always has.
public static class NeuronAliases
{
    public static string? Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        for (var current = type; current is not null; current = current.BaseType)
        {
            var alias = current.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias
                ?? current.GetCustomAttribute<AliasAttribute>(inherit: false)?.Alias;
            if (alias is not null) { return alias; }
        }
        return null;
    }
}
