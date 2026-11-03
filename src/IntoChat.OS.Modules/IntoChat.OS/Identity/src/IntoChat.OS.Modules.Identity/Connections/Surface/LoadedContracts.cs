using DigitalBrain.Contracts;
using System.Collections.Concurrent;
using System.Reflection;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Surface;

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
        .FirstOrDefault(type => type is not null && DigitalBrain.Contracts.PlainNeuronContracts.Is(type));

    // Referenced contract assemblies load lazily; load what the entry assembly and base directory deploy, transitively.
    private static bool LoadReferenced()
    {
        var seen = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name).ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<AssemblyName>();
        if (Assembly.GetEntryAssembly() is { } entry)
        {
            foreach (var reference in entry.GetReferencedAssemblies()) { pending.Enqueue(reference); }
        }
        if (Directory.Exists(AppContext.BaseDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.StartsWith("IntoChat.OS", StringComparison.Ordinal) || fileName.StartsWith("DigitalBrain", StringComparison.Ordinal))
                {
                    try { pending.Enqueue(AssemblyName.GetAssemblyName(file)); }
                    catch (BadImageFormatException) { continue; }
                    catch (FileLoadException) { continue; }
                }
            }
        }
        while (pending.TryDequeue(out var reference))
        {
            if (!(reference.Name!.StartsWith("IntoChat.OS", StringComparison.Ordinal) || reference.Name.StartsWith("DigitalBrain", StringComparison.Ordinal))
                || !seen.Add(reference.Name)) { continue; }
            try { foreach (var next in Assembly.Load(reference).GetReferencedAssemblies()) { pending.Enqueue(next); } }
            catch (FileNotFoundException) { continue; } // an optional reference that is not deployed
            catch (FileLoadException) { continue; }
            catch (BadImageFormatException) { continue; }
        }
        return true;
    }
}
