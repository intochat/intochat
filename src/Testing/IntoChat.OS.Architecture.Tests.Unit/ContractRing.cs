using System.Reflection;
using IntoChat.OS.Client.Runtime;

namespace IntoChat.OS.Architecture.Tests.Unit;

// What counts as a contract assembly: a *.Contracts project, or a module assembly that declares it hosts its own
// contracts (IntoChatOSContractsHost) because no other module references them. A hosting assembly mixes contract
// types with the module's implementation, so the ring facts judge only the contract types in it.
internal static class ContractRing
{
    internal static bool IsContractsProject(Assembly assembly) => assembly.GetName().Name!.EndsWith(".Contracts", StringComparison.Ordinal);

    internal static bool IsHost(Assembly assembly) => ContractsHost.Declares(assembly);

    internal static bool IsContractAssembly(Assembly assembly) => IsContractsProject(assembly) || IsHost(assembly);

    // The contract types of an assembly: all of them in a Contracts project; in a host, the neuron interfaces and
    // signals plus every type of the assembly their members name (parameters, results, properties, constructors).
    internal static Type[] ContractTypes(Assembly assembly)
    {
        var exported = assembly.GetExportedTypes().Where(type => type.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true).ToArray();
        if (IsContractsProject(assembly)) { return exported; }
        var seen = new HashSet<Type>(exported.Where(type => (type.IsInterface && typeof(INeuron).IsAssignableFrom(type)) || typeof(Signal).IsAssignableFrom(type)));
        var queue = new Queue<Type>(seen);
        while (queue.TryDequeue(out var type))
        {
            foreach (var part in Members(type).SelectMany(Unwrap).Where(part => part.Assembly == assembly && exported.Contains(part) && seen.Add(part))) { queue.Enqueue(part); }
        }
        return [.. seen];
    }

    private static IEnumerable<Type> Members(Type type)
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return type.GetMethods(Declared).SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType))
            .Concat(type.GetProperties(Declared).Select(property => property.PropertyType))
            .Concat(type.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType));
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.IsByRef || type.IsArray) { return Unwrap(type.GetElementType()!); }
        if (type.IsGenericParameter) { return []; }
        return type.IsGenericType ? type.GetGenericArguments().SelectMany(Unwrap).Prepend(type.GetGenericTypeDefinition()) : [type];
    }
}
