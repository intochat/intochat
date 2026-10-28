using DigitalBrain;
using System.Collections.Concurrent;

namespace IntoChat.OS.Authority.Neurons;

// The CLR type behind a signal name the shell knows: short ("ButtonChanged") or full. Only the
// brain's own signal types (the kernel's type table) are watchable; an unknown name costs one pass
// over that table and is never remembered.
internal static class SignalTypes
{
    private static readonly ConcurrentDictionary<string, Type> Loaded = new(StringComparer.Ordinal);

    public static Type Find(string name, BrainType[] types)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(types);
        var matches = types.Where(entry => entry.Kind == DigitalBrain.TypeKind.Signal
                && (entry.Name == name || entry.Name.EndsWith("." + name, StringComparison.Ordinal)))
            .Select(entry => entry.Name).Distinct(StringComparer.Ordinal).ToArray();
        // An ambiguous short name resolves to nothing, so the caller is told to pass the full name.
        if (matches.Length != 1) { throw new ArgumentException($"{name} is not a signal type of this brain."); }
        return Loaded.GetOrAdd(matches[0], Load) ?? throw new ArgumentException($"{name} is a signal type of this brain, but this host does not carry it.");
    }

    private static Type Load(string fullName)
        => AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic)
            .Select(assembly => assembly.GetType(fullName, throwOnError: false))
            .FirstOrDefault(type => type is not null && (typeof(Signal).IsAssignableFrom(type) || typeof(global::DigitalBrain.Signal).IsAssignableFrom(type)))
            ?? throw new ArgumentException($"{fullName} is a signal type of this brain, but this host does not carry it.");
}
