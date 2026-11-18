using System.Collections.Concurrent;
using System.Reflection;
using DigitalBrain.Contracts;

namespace DigitalBrain.Runtime.Http.Surface;

/// <summary>
/// The CLR contract behind a neuron type, from what this process references.
/// </summary>
public static class LoadedContracts
{
    private static readonly string[] DefaultPrefixes = ["DigitalBrain"];
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Type?>> Found = new(StringComparer.Ordinal);

    public static Type Find(string name)
        => Find(name, allowedPrefixes: null);

    public static Type Find(string name, string[]? allowedPrefixes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var prefixes = NormalizePrefixes(allowedPrefixes);
        var cacheKey = string.Join('\0', prefixes);
        var cache = Found.GetOrAdd(cacheKey, static _ => new ConcurrentDictionary<string, Type?>(StringComparer.Ordinal));
        return cache.GetOrAdd(name, static (typeName, p) => Search(typeName, p), prefixes)
            ?? throw new TypeLoadException($"{name} is a neuron type of this brain, but this host does not carry its contract.");
    }

    private static string[] NormalizePrefixes(string[]? allowedPrefixes)
    {
        if (allowedPrefixes is null || allowedPrefixes.Length == 0) { return DefaultPrefixes; }
        var normalized = allowedPrefixes
            .Where(static prefix => !string.IsNullOrWhiteSpace(prefix))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return normalized.Length > 0 ? normalized : DefaultPrefixes;
    }

    private static Type? Search(string name, string[] prefixes)
        => Loaded(name) ?? (LoadReferenced(prefixes) ? Loaded(name) : null);

    private static Type? Loaded(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Where(assembly => !assembly.IsDynamic).Select(assembly => assembly.GetType(name, throwOnError: false))
        .FirstOrDefault(type => type is not null && PlainNeuronContracts.Is(type));

    private static bool LoadReferenced(string[] prefixes)
    {
        var seen = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name).ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<AssemblyName>();
        if (Assembly.GetEntryAssembly() is { } entry)
        {
            try
            {
                foreach (var reference in entry.GetReferencedAssemblies()) { pending.Enqueue(reference); }
            }
            catch (PlatformNotSupportedException) { }
        }
        if (Directory.Exists(AppContext.BaseDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (StartsWithAny(fileName, prefixes))
                {
                    try { pending.Enqueue(AssemblyName.GetAssemblyName(file)); }
                    catch (PlatformNotSupportedException) { continue; }
                    catch (BadImageFormatException) { continue; }
                    catch (FileLoadException) { continue; }
                }
            }
        }
        while (pending.TryDequeue(out var reference))
        {
            if (!StartsWithAny(reference.Name, prefixes)
                || !seen.Add(reference.Name!)) { continue; }
            try { foreach (var next in Assembly.Load(reference).GetReferencedAssemblies()) { pending.Enqueue(next); } }
            catch (PlatformNotSupportedException) { continue; }
            catch (FileNotFoundException) { continue; }
            catch (FileLoadException) { continue; }
            catch (BadImageFormatException) { continue; }
        }
        return true;
    }

    private static bool StartsWithAny(string? name, string[] prefixes)
        => name is not null && prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
}
