using System.Reflection;
using DigitalBrain.Contracts;

namespace IntoChat.OS;

/// <summary>A type's effective alias across <see cref="NeuronAliasAttribute"/> and the Orleans <see cref="AliasAttribute"/>.</summary>
public static class NeuronAliases
{
    /// <summary>
    /// The nearest type in the base chain that declares either attribute wins, and on one type [NeuronAlias] wins over the
    /// Orleans [Alias]. A derived signal with no alias of its own keeps its base class alias.
    /// </summary>
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
