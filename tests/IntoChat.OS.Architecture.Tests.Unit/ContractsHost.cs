using System.Reflection;

namespace IntoChat.OS.Architecture.Tests.Unit;

// A module whose contracts no other module references keeps them in its own assembly. That assembly says so
// with assembly metadata (set by the IntoChatOSContractsHost project property), so the architecture facts find
// contract assemblies by what they declare, never by their name.
internal static class ContractsHost
{
    public const string MetadataKey = "IntoChat.OS.ContractsHost";

    public static bool Declares(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(entry => entry.Key == MetadataKey && entry.Value == "true");
    }

    // The contract surface of a hosting assembly: its neuron interfaces and signals plus every public type of the
    // assembly their members name (parameters, results, properties, constructors). The rest is the module's own.
    public static Type[] ContractTypes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var exported = assembly.GetExportedTypes().Where(type => type.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true).ToHashSet();
        var seen = new HashSet<Type>(exported.Where(type => DigitalBrain.Contracts.PlainNeuronContracts.Is(type)
            || (typeof(DigitalBrain.Signal).IsAssignableFrom(type) && type != typeof(DigitalBrain.Signal) && type.IsDefined(typeof(DigitalBrain.SignalAttribute), inherit: false))));
        var queue = new Queue<Type>(seen);
        while (queue.TryDequeue(out var type))
        {
            foreach (var part in Members(type).SelectMany(Unwrap).Where(part => part.Assembly == assembly && exported.Contains(part) && seen.Add(part))) { queue.Enqueue(part); }
        }
        return [.. seen.OrderBy(type => type.FullName, StringComparer.Ordinal)];
    }

    private static IEnumerable<Type> Members(Type type)
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return type.GetMethods(Declared).SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType)
                .Concat(method.CustomAttributes.Select(attribute => attribute.AttributeType)))
            .Concat(type.GetProperties(Declared).SelectMany(property => property.CustomAttributes.Select(attribute => attribute.AttributeType).Prepend(property.PropertyType)))
            .Concat(type.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType))
            .Concat(type.CustomAttributes.Select(attribute => attribute.AttributeType))
            .Concat(type.GetCustomAttributes<System.Text.Json.Serialization.JsonDerivedTypeAttribute>().Select(derived => derived.DerivedType))
            .Concat(type.GetInterfaces())
            .Concat(type.GetNestedTypes(BindingFlags.Public))
            .Concat(type.BaseType is { } baseType ? [baseType] : []);
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.IsByRef || type.IsArray) { return Unwrap(type.GetElementType()!); }
        if (type.IsGenericParameter) { return []; }
        return type.IsGenericType ? type.GetGenericArguments().SelectMany(Unwrap).Prepend(type.GetGenericTypeDefinition()) : [type];
    }
}
