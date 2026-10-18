using System.Reflection;

namespace DigitalBrain;

// A type's rename-safe name: its own [NeuronAlias] first, then its Orleans [Alias] read exactly as before. Both carry the same strings.
public static class NeuronAliases
{
    public static string? Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias
            ?? type.GetCustomAttributes<AliasAttribute>().FirstOrDefault()?.Alias;
    }
}
