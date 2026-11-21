using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using DigitalBrain.Contracts;

namespace DigitalBrain.Runtime.Http.Surface;

/// <summary>
/// The CLR contract behind a neuron type, from what this process references.
/// </summary>
public static class LoadedContracts
{
    private static readonly string[] DefaultPrefixes = ["DigitalBrain"];
    private static readonly IReadOnlyDictionary<string, Type> EmptyFormer = new Dictionary<string, Type>(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<Assembly, IReadOnlyDictionary<string, Type>> FormerByAssembly = [];
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Type?>> Found = new(StringComparer.Ordinal);

    public static Type Find(string name)
        => Find(name, allowedPrefixes: null);

    public static Type Find(string name, string[]? allowedPrefixes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Resolve(name, allowedPrefixes)
            ?? throw new TypeLoadException($"{name} is a neuron type of this brain, but this host does not carry its contract.");
    }

    /// <summary>Maps a contract's former alias or full type name to its current full type name, or returns <paramref name="name"/> unchanged.</summary>
    public static string CurrentName(string name)
        => CurrentName(name, allowedPrefixes: null);

    /// <summary>Maps a contract's former alias or full type name to its current full type name using <paramref name="allowedPrefixes"/>, or returns <paramref name="name"/> unchanged.</summary>
    public static string CurrentName(string name, string[]? allowedPrefixes)
        => string.IsNullOrWhiteSpace(name) ? name : Resolve(name, allowedPrefixes)?.FullName ?? name;

    private static Type? Resolve(string name, string[]? allowedPrefixes)
    {
        var prefixes = NormalizePrefixes(allowedPrefixes);
        var cacheKey = string.Join('\0', prefixes);
        var cache = Found.GetOrAdd(cacheKey, static _ => new ConcurrentDictionary<string, Type?>(StringComparer.Ordinal));
        return cache.GetOrAdd(name, static (typeName, p) => Search(typeName, p), prefixes);
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

    private static Type? Loaded(string name)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(static assembly => !assembly.IsDynamic).ToArray();
        var exact = assemblies
            .Select(assembly => assembly.GetType(name, throwOnError: false))
            .FirstOrDefault(static type => type is not null && PlainNeuronContracts.Is(type));
        if (exact is not null)
        {
            return exact;
        }

        foreach (var assembly in assemblies)
        {
            if (FormerNamesOf(assembly).TryGetValue(name, out var matched))
            {
                return matched;
            }
        }
        return null;
    }

    private static IReadOnlyDictionary<string, Type> FormerNamesOf(Assembly assembly)
        => FormerByAssembly.GetValue(assembly, static asm => BuildFormerMap(asm));

    private static IReadOnlyDictionary<string, Type> BuildFormerMap(Assembly assembly)
    {
        Dictionary<string, Type>? map = null;
        foreach (var type in SafeTypes(assembly))
        {
            if (!type.IsInterface || !PlainNeuronContracts.Is(type))
            {
                continue;
            }
            var alias = type.GetCustomAttribute<NeuronAliasAttribute>(inherit: false);
            if (alias is not { Formerly.Length: > 0 })
            {
                continue;
            }
            foreach (var former in alias.Formerly)
            {
                if (!string.IsNullOrWhiteSpace(former))
                {
                    map ??= new Dictionary<string, Type>(StringComparer.Ordinal);
                    map.TryAdd(former, type);
                }
            }
        }
        return map ?? EmptyFormer;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            return error.Types.Where(static t => t is not null)!;
        }
        catch (Exception)
        {
            return [];
        }
    }

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
