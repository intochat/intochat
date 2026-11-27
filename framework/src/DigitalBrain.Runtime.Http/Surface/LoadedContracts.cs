using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;

namespace DigitalBrain.Runtime.Http.Surface;

/// <summary>
/// Resolves CLR neuron contract interfaces and hosted neuron types from an explicit exact-assembly catalog
/// (or from currently loaded non-dynamic assemblies when no explicit allowlist is supplied).
/// </summary>
public static class LoadedContracts
{
    private static readonly string DigitalBrainAssemblyName = typeof(NeuronAliasAttribute).Assembly.GetName().Name!;
    private static readonly ConditionalWeakTable<Assembly, AssemblyContractIndex> Indexes = [];
    private static readonly ConcurrentDictionary<Type, string> ResolvedNeuronTypes = new();

    /// <summary>Finds a plain neuron contract by full name, alias, or former name across currently loaded assemblies.</summary>
    public static Type Find(string name)
        => Find(name, assemblies: (IEnumerable<Assembly>?)null);

    /// <summary>Finds a plain neuron contract within the exact assemblies supplied by <paramref name="source"/>.</summary>
    public static Type Find(string name, IContractAssemblySource? source)
        => Find(name, source?.ContractAssemblies());

    /// <summary>Finds a plain neuron contract within the exact <paramref name="assemblies"/> (or loaded assemblies when null).</summary>
    public static Type Find(string name, IEnumerable<Assembly>? assemblies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return TryFind(name, assemblies)
            ?? throw new TypeLoadException($"{name} is a neuron type of this brain, but this host does not carry its contract.");
    }

    /// <summary>Finds a plain neuron contract within assemblies whose simple names exactly match <paramref name="allowedAssemblyNames"/>.</summary>
    public static Type Find(string name, IReadOnlyCollection<string>? allowedAssemblyNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return TryFind(name, allowedAssemblyNames)
            ?? throw new TypeLoadException($"{name} is a neuron type of this brain, but this host does not carry its contract.");
    }

    /// <summary>Tries to resolve a plain neuron contract within <paramref name="source"/> (or loaded assemblies when null).</summary>
    public static Type? TryFind(string name, IContractAssemblySource? source = null)
        => TryFind(name, source?.ContractAssemblies());

    /// <summary>Tries to resolve a plain neuron contract within the exact <paramref name="assemblies"/> (or loaded assemblies when null).</summary>
    public static Type? TryFind(string name, IEnumerable<Assembly>? assemblies)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var scope = assemblies is not null
            ? [.. assemblies.Where(static assembly => assembly is not null && !assembly.IsDynamic).Distinct()]
            : LoadedAssemblies();
        return SearchPlain(name, scope);
    }

    /// <summary>Tries to resolve a plain neuron contract within assemblies whose simple names exactly match <paramref name="allowedAssemblyNames"/>.</summary>
    public static Type? TryFind(string name, IReadOnlyCollection<string>? allowedAssemblyNames)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (allowedAssemblyNames is null)
        {
            return SearchPlain(name, LoadedAssemblies());
        }

        var allowed = ExactNames(allowedAssemblyNames);
        if (allowed.Count == 0)
        {
            return null;
        }

        var scope = ResolveExactNamedAssemblies(allowed);
        return SearchPlain(name, scope);
    }

    /// <summary>
    /// Tries to resolve any interface (including internal or non-plain contracts) by exact full name or <see cref="NeuronAliasAttribute"/>
    /// (current alias or former names) within <paramref name="source"/> (or loaded assemblies when null).
    /// </summary>
    public static Type? TryFindInterface(string name, IContractAssemblySource? source = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var scope = source is not null
            ? [.. source.ContractAssemblies().Where(static assembly => assembly is not null && !assembly.IsDynamic).Distinct()]
            : LoadedAssemblies();

        foreach (var assembly in scope)
        {
            if (assembly.GetType(name, throwOnError: false) is { IsInterface: true } exact)
            {
                return exact;
            }
        }

        foreach (var assembly in scope)
        {
            if (IndexOf(assembly).TryGetInterface(name, out var aliased))
            {
                return aliased;
            }
        }

        return null;
    }

    /// <summary>Maps a contract's alias, former alias, or former full type name to its current full type name, or returns <paramref name="name"/> unchanged.</summary>
    public static string CurrentName(string name)
        => CurrentName(name, assemblies: (IEnumerable<Assembly>?)null);

    /// <summary>Maps a contract's alias or former name to its current full type name using <paramref name="source"/>.</summary>
    public static string CurrentName(string name, IContractAssemblySource? source)
        => CurrentName(name, source?.ContractAssemblies());

    /// <summary>Maps a contract's alias or former name to its current full type name using exact <paramref name="assemblies"/>.</summary>
    public static string CurrentName(string name, IEnumerable<Assembly>? assemblies)
        => string.IsNullOrWhiteSpace(name) ? name : TryFind(name, assemblies)?.FullName ?? name;

    /// <summary>Maps a contract's alias or former name to its current full type name using exact <paramref name="allowedAssemblyNames"/>.</summary>
    public static string CurrentName(string name, IReadOnlyCollection<string>? allowedAssemblyNames)
        => string.IsNullOrWhiteSpace(name) ? name : TryFind(name, allowedAssemblyNames)?.FullName ?? name;

    /// <summary>
    /// Resolves the canonical neuron type string for <paramref name="contract"/>:
    /// <see cref="DefaultNeuronTypeAttribute"/> / <see cref="PlainNeuronContracts.TypeOf"/>, else a hosted concrete
    /// <see cref="NeuronAttribute"/> class implementing <paramref name="contract"/>, else <see cref="NeuronAliasAttribute.Alias"/>.
    /// </summary>
    public static string? ResolveNeuronType(Type contract, IEnumerable<Assembly>? implementationAssemblies = null)
    {
        ArgumentNullException.ThrowIfNull(contract);

        if (contract.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type is { Length: > 0 } defaultType)
        {
            return defaultType;
        }

        if (PlainNeuronContracts.TypeOf(contract) is { Length: > 0 } plainType)
        {
            return plainType;
        }

        if (implementationAssemblies is null && ResolvedNeuronTypes.TryGetValue(contract, out var cached))
        {
            return cached;
        }

        var scope = implementationAssemblies is not null
            ? [.. implementationAssemblies.Where(static assembly => assembly is not null && !assembly.IsDynamic).Distinct()]
            : LoadedAssemblies();

        foreach (var assembly in scope)
        {
            foreach (var candidate in IndexOf(assembly).NeuronImplementations)
            {
                if (contract.IsAssignableFrom(candidate.Class))
                {
                    if (implementationAssemblies is null)
                    {
                        ResolvedNeuronTypes.TryAdd(contract, candidate.NeuronType);
                    }

                    return candidate.NeuronType;
                }
            }
        }

        var alias = contract.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias;
        if (!string.IsNullOrWhiteSpace(alias) && implementationAssemblies is null)
        {
            ResolvedNeuronTypes.TryAdd(contract, alias);
        }

        return alias;
    }

    private static Type? SearchPlain(string name, Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            if (assembly.GetType(name, throwOnError: false) is { } exact && PlainNeuronContracts.Is(exact))
            {
                return exact;
            }
        }

        foreach (var assembly in assemblies)
        {
            if (IndexOf(assembly).TryGetPlainByAlias(name, out var aliased))
            {
                return aliased;
            }
        }

        foreach (var assembly in assemblies)
        {
            if (IndexOf(assembly).TryGetPlainByFormer(name, out var former))
            {
                return former;
            }
        }

        return null;
    }

    private static Assembly[] LoadedAssemblies()
        => [.. AppDomain.CurrentDomain.GetAssemblies().Where(static assembly => !assembly.IsDynamic)];

    private static HashSet<string> ExactNames(IReadOnlyCollection<string> allowedAssemblyNames)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in allowedAssemblyNames)
        {
            if (!string.IsNullOrWhiteSpace(raw))
            {
                set.Add(raw.Trim());
            }
        }

        return set;
    }

    private static Assembly[] ResolveExactNamedAssemblies(HashSet<string> allowed)
    {
        var loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            var simpleName = assembly.GetName().Name;
            if (simpleName is not null && allowed.Contains(simpleName))
            {
                loaded.TryAdd(simpleName, assembly);
            }
        }

        foreach (var missing in allowed)
        {
            if (loaded.ContainsKey(missing))
            {
                continue;
            }

            if (TryLoadExact(missing) is { } assembly)
            {
                loaded.TryAdd(missing, assembly);
            }
        }

        return [.. loaded.Values];
    }

    private static Assembly? TryLoadExact(string simpleName)
    {
        try
        {
            return Assembly.Load(new AssemblyName(simpleName));
        }
        catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException or PlatformNotSupportedException)
        {
            // Fall through to exact file probe in AppContext.BaseDirectory.
        }

        if (!Directory.Exists(AppContext.BaseDirectory))
        {
            return null;
        }

        var candidatePath = Path.Combine(AppContext.BaseDirectory, simpleName + ".dll");
        if (!File.Exists(candidatePath))
        {
            return null;
        }

        try
        {
            var assemblyName = AssemblyName.GetAssemblyName(candidatePath);
            if (!string.Equals(assemblyName.Name, simpleName, StringComparison.Ordinal))
            {
                return null;
            }

            return Assembly.Load(assemblyName);
        }
        catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static AssemblyContractIndex IndexOf(Assembly assembly)
        => Indexes.GetValue(assembly, static asm => AssemblyContractIndex.Build(asm));

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

    private static bool CanDeclareContracts(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (string.Equals(name, DigitalBrainAssemblyName, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return assembly.GetReferencedAssemblies().Any(reference => string.Equals(reference.Name, DigitalBrainAssemblyName, StringComparison.Ordinal));
        }
        catch (PlatformNotSupportedException)
        {
            return true;
        }
    }

    private sealed class AssemblyContractIndex(
        Dictionary<string, Type>? plainAliases,
        Dictionary<string, Type>? plainFormer,
        Dictionary<string, Type>? allInterfaceAliases,
        (Type Class, string NeuronType)[] neuronImplementations)
    {
        private static readonly AssemblyContractIndex Empty = new(null, null, null, []);

        public (Type Class, string NeuronType)[] NeuronImplementations => neuronImplementations;

        public bool TryGetPlainByAlias(string name, out Type type)
        {
            if (plainAliases is not null && plainAliases.TryGetValue(name, out var found))
            {
                type = found;
                return true;
            }

            type = null!;
            return false;
        }

        public bool TryGetPlainByFormer(string name, out Type type)
        {
            if (plainFormer is not null && plainFormer.TryGetValue(name, out var found))
            {
                type = found;
                return true;
            }

            type = null!;
            return false;
        }

        public bool TryGetInterface(string name, out Type type)
        {
            if (allInterfaceAliases is not null && allInterfaceAliases.TryGetValue(name, out var found))
            {
                type = found;
                return true;
            }

            type = null!;
            return false;
        }

        public static AssemblyContractIndex Build(Assembly assembly)
        {
            if (!CanDeclareContracts(assembly))
            {
                return Empty;
            }

            Dictionary<string, Type>? plainAliases = null;
            Dictionary<string, Type>? plainFormer = null;
            Dictionary<string, Type>? allInterfaceAliases = null;
            List<(Type Class, string NeuronType)>? implementations = null;

            foreach (var type in SafeTypes(assembly))
            {
                if (type.IsInterface)
                {
                    var alias = type.GetCustomAttribute<NeuronAliasAttribute>(inherit: false);
                    var isPlain = PlainNeuronContracts.Is(type);
                    if (alias is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(alias.Alias))
                        {
                            allInterfaceAliases ??= new Dictionary<string, Type>(StringComparer.Ordinal);
                            allInterfaceAliases.TryAdd(alias.Alias, type);
                            if (isPlain)
                            {
                                plainAliases ??= new Dictionary<string, Type>(StringComparer.Ordinal);
                                plainAliases.TryAdd(alias.Alias, type);
                            }
                        }

                        if (alias.Formerly is { Length: > 0 })
                        {
                            foreach (var former in alias.Formerly)
                            {
                                if (string.IsNullOrWhiteSpace(former))
                                {
                                    continue;
                                }

                                allInterfaceAliases ??= new Dictionary<string, Type>(StringComparer.Ordinal);
                                allInterfaceAliases.TryAdd(former, type);
                                if (isPlain)
                                {
                                    plainFormer ??= new Dictionary<string, Type>(StringComparer.Ordinal);
                                    plainFormer.TryAdd(former, type);
                                }
                            }
                        }
                    }

                    continue;
                }

                if (type.IsClass && !type.IsAbstract
                    && type.GetCustomAttribute<NeuronAttribute>(inherit: false)?.Type is { Length: > 0 } neuronType)
                {
                    implementations ??= [];
                    implementations.Add((type, neuronType));
                }
            }

            if (plainAliases is null && plainFormer is null && allInterfaceAliases is null && implementations is null)
            {
                return Empty;
            }

            return new AssemblyContractIndex(
                plainAliases,
                plainFormer,
                allInterfaceAliases,
                implementations is null ? [] : [.. implementations]);
        }
    }
}
