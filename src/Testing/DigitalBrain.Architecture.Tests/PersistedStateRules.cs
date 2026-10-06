using System.Reflection;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Architecture.Tests;

internal static class PersistedStateRules
{
    internal static IEnumerable<MemberInfo> Members(Type type) => type
        .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Where(member => member is PropertyInfo or FieldInfo && member.IsDefined(typeof(IdAttribute)));

    internal static IEnumerable<string> IdViolations(Type type)
    {
        var ids = Members(type).Select(member => member.GetCustomAttribute<IdAttribute>()!.Id);
        foreach (var duplicate in ids.GroupBy(id => id).Where(group => group.Count() > 1))
        {
            yield return $"{type.FullName}: duplicate Id({duplicate.Key})";
        }
    }

    internal static IEnumerable<string> ShapeViolations(Type type)
    {
        foreach (var violation in IdViolations(type)) { yield return violation; }
        foreach (var member in ContractRules.PublicMembers(type).Where(member => member.DeclaringType == type
            && (member is FieldInfo || member is PropertyInfo { SetMethod: not null })))
        {
            if (!member.IsDefined(typeof(IdAttribute))
                && !member.IsDefined(typeof(System.Runtime.Serialization.IgnoreDataMemberAttribute))
                && !member.IsDefined(typeof(NonSerializedAttribute)))
            {
                yield return $"{type.FullName}.{member.Name}: persisted member has no Id";
            }
        }
        foreach (var member in Members(type).Where(member => ContainsInterfaceCollection(ContractRules.MemberType(member))))
        {
            yield return $"{type.FullName}.{member.Name}: persisted collections must use concrete arrays, not collection interfaces";
        }
    }

    private static bool ContainsInterfaceCollection(Type type) =>
        (type.IsInterface && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
        || (type.HasElementType && ContainsInterfaceCollection(type.GetElementType()!))
        || (type.IsGenericType && type.GetGenericArguments().Any(ContainsInterfaceCollection));

    internal static Type[] Discover(IEnumerable<Type> productionTypes)
    {
        var types = productionTypes.ToArray();
        var assemblies = types.Select(type => type.Assembly).ToHashSet();
        var pending = new Queue<Type>(types.SelectMany(type =>
            type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType)
                .Concat(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Select(field => field.FieldType)))
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IPersistentState<>))
            .Select(type => type.GetGenericArguments()[0]));
        foreach (var type in types)
        {
            for (var ancestor = type.BaseType; ancestor is not null; ancestor = ancestor.BaseType)
            {
                if (ancestor.IsGenericType && !ancestor.ContainsGenericParameters
                    && ancestor.GetGenericTypeDefinition() == typeof(Grain<>))
                {
                    pending.Enqueue(ancestor.GetGenericArguments()[0]);
                }
            }
        }
        HashSet<Type> visited = [];
        HashSet<Type> states = [];
        while (pending.TryDequeue(out var type))
        {
            if (!visited.Add(type)) { continue; }
            if (type.HasElementType) { pending.Enqueue(type.GetElementType()!); }
            foreach (var argument in type.GetGenericArguments()) { pending.Enqueue(argument); }
            if (!assemblies.Contains(type.Assembly)) { continue; }
            foreach (var derived in types.Where(candidate => candidate != type && type.IsAssignableFrom(candidate)
                && candidate.IsDefined(typeof(GenerateSerializerAttribute), false)))
            {
                pending.Enqueue(derived);
            }
            if (type.BaseType is { } parent) { pending.Enqueue(parent); }
            if (type.IsEnum || !type.IsDefined(typeof(GenerateSerializerAttribute), false)) { continue; }
            states.Add(type);
            foreach (var member in Members(type)) { pending.Enqueue(ContractRules.MemberType(member)); }
        }
        return states.OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
    }
}
