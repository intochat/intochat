using System.Collections.Concurrent;
using System.Reflection;

namespace DigitalBrain.Framework.Surface;

// The CLR contract behind a kernel neuron type, from what this process references: calling a neuron
// needs its contract; knowing it exists does not.
internal static class LoadedContracts
{
    private static readonly ConcurrentDictionary<string, Type?> Found = new(StringComparer.Ordinal);

    public static Type Find(string name)
        => Found.GetOrAdd(name, Search) ?? throw new TypeLoadException($"{name} is a neuron type of this brain, but this host does not carry its contract.");

    private static Type? Search(string name)
        => Loaded(name) ?? (LoadReferenced() ? Loaded(name) : null);

    private static Type? Loaded(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Where(assembly => !assembly.IsDynamic).Select(assembly => assembly.GetType(name, throwOnError: false))
        .FirstOrDefault(type => type is { IsInterface: true } && typeof(INeuron).IsAssignableFrom(type));

    // Referenced contract assemblies load lazily; load what the entry assembly references, transitively.
    private static bool LoadReferenced()
    {
        if (Assembly.GetEntryAssembly() is not { } entry) { return false; }
        var seen = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name).ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<AssemblyName>(entry.GetReferencedAssemblies());
        while (pending.TryDequeue(out var reference))
        {
            if (!reference.Name!.StartsWith("DigitalBrain", StringComparison.Ordinal) || !seen.Add(reference.Name)) { continue; }
            try { foreach (var next in Assembly.Load(reference).GetReferencedAssemblies()) { pending.Enqueue(next); } }
            catch (FileNotFoundException) { }
        }
        return true;
    }
}
