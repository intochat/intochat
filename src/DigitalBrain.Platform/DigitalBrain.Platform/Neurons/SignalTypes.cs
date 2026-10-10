using System.Collections.Concurrent;
using System.Reflection;

namespace DigitalBrain.Platform.Neurons;

// The CLR type behind a signal name the shell knows: short ("ButtonChanged") or full. The
// contract assemblies this host carries are the only place a signal can come from.
internal static class SignalTypes
{
    private static readonly ConcurrentDictionary<string, Type?> Found = new(StringComparer.Ordinal);

    public static Type Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Found.GetOrAdd(name, Search) ?? throw new ArgumentException($"{name} is not a signal type of this brain.");
    }

    // An ambiguous short name resolves to nothing, so the caller is told to pass the full name.
    private static Type? Search(string name)
    {
        var matches = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic)
            .SelectMany(Types)
            .Where(type => typeof(Signal).IsAssignableFrom(type) && !type.IsAbstract && (type.FullName == name || type.Name == name))
            .Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.Where(type => type is not null)!; }
    }
}
