using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.AI.Inference;

namespace IntoChat.OS.Architecture.Tests.Unit;

internal static class PersistedStateRules
{
    internal static IEnumerable<MemberInfo> Members(Type type) => type
        .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Where(member => member is PropertyInfo or FieldInfo && member.IsDefined(typeof(IdAttribute)))
        .Concat(PrimaryConstructorMembers(type));

    // Orleans gives positional record parameters their own serialization layer, before
    // explicitly numbered members. Adding Id attributes moves an existing field between
    // layers, even when its numeric ID is unchanged, and breaks stored messages.
    private static IEnumerable<PropertyInfo> PrimaryConstructorMembers(Type type)
    {
        var serializer = type.GetCustomAttributesData().SingleOrDefault(attribute => attribute.AttributeType == typeof(GenerateSerializerAttribute));
        if (serializer is null || serializer.NamedArguments.Any(argument =>
            argument.MemberName == nameof(GenerateSerializerAttribute.IncludePrimaryConstructorParameters)
            && argument.TypedValue.Value is false)) { yield break; }

        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        // A synthesized Deconstruct describes the positional record parameters. Matching
        // the constructor prevents an ordinary class with a similarly named method from
        // acquiring implicit serialization merely because it has writable properties.
        var deconstruct = type.GetMethods(declared).SingleOrDefault(method => method.Name == "Deconstruct"
            && method.IsDefined(typeof(CompilerGeneratedAttribute), false));
        if (deconstruct is null) { yield break; }
        var parameters = deconstruct.GetParameters();
        if (!type.GetConstructors(declared).Any(constructor => constructor.GetParameters().Select(parameter => (parameter.Name, parameter.ParameterType))
            .SequenceEqual(parameters.Select(parameter => (parameter.Name, parameter.ParameterType.GetElementType()!))))) { yield break; }
        foreach (var parameter in parameters)
        {
            var property = type.GetProperty(parameter.Name!, declared);
            if (property is not null && property.PropertyType == parameter.ParameterType.GetElementType()
                && !property.IsDefined(typeof(IdAttribute))
                && !property.IsDefined(typeof(System.Runtime.Serialization.IgnoreDataMemberAttribute))
                && !property.IsDefined(typeof(NonSerializedAttribute))) { yield return property; }
        }
    }

    internal static IEnumerable<string> IdViolations(Type type)
    {
        var ids = Members(type).Select(member => member.GetCustomAttribute<IdAttribute>()).OfType<IdAttribute>().Select(attribute => attribute.Id);
        foreach (var duplicate in ids.GroupBy(id => id).Where(group => group.Count() > 1))
        {
            yield return $"{type.FullName}: duplicate Id({duplicate.Key})";
        }
    }

    internal static IEnumerable<string> ShapeViolations(Type type)
    {
        foreach (var violation in IdViolations(type)) { yield return violation; }
        var implicitMembers = PrimaryConstructorMembers(type).ToHashSet<MemberInfo>();
        foreach (var member in ContractRules.PublicMembers(type).Where(member => member.DeclaringType == type
            && (member is FieldInfo || member is PropertyInfo { SetMethod: not null })))
        {
            if (!member.IsDefined(typeof(IdAttribute))
                && !implicitMembers.Contains(member)
                && !member.IsDefined(typeof(System.Runtime.Serialization.IgnoreDataMemberAttribute))
                && !member.IsDefined(typeof(NonSerializedAttribute)))
            {
                yield return $"{type.FullName}.{member.Name}: persisted member has no Id";
            }
        }
        foreach (var member in Members(type).Where(member => ContainsInterfaceCollection(ContractRules.MemberType(member))))
        {
            // This published positional member must keep its constructor/getter/init/deconstruction
            // signatures and Id(1) interface codec for existing bundles and stored messages.
            // Constructor/init snapshot arrays; legacy lists remain JSON-readable. The focused
            // compatibility facts guard this one exception, not arbitrary interface collections.
            if (member.DeclaringType == typeof(AiMessage) && member.Name == nameof(AiMessage.Content)
                && ContractRules.MemberType(member) == typeof(IReadOnlyList<AiContent>)
                && member.GetCustomAttribute<IdAttribute>()!.Id == 1) { continue; }
            yield return $"{type.FullName}.{member.Name}: persisted collections must use concrete arrays, not collection interfaces";
        }
        foreach (var name in ConcreteCollectionMembers(type).Where(name => !UnmigratedConcreteCollections.Contains(name)))
        {
            yield return $"{name}: persisted collections must be arrays (a keyed map is an entry array), not Dictionary/List/HashSet/SortedDictionary/Queue/Stack";
        }
    }

    // Members that still store a concrete collection. There is no JSON pin: this set is the list, it does not grow,
    // and a member leaves it only when its stored shape becomes an array.
    internal static readonly HashSet<string> UnmigratedConcreteCollections = new(StringComparer.Ordinal)
    {
        "IntoChat.OS.Modules.AI.Embeddings.VectorEntry.Payload",
        "IntoChat.OS.Modules.AI.Metering.IntentUsageStorage.Entries",
        "IntoChat.OS.Modules.AI.Routing.RouteResult.Probabilities",
        "IntoChat.OS.Modules.Apps.AppBundle.Files",
        "IntoChat.OS.Modules.Apps.AppPresentation.Blackboard",
        "IntoChat.OS.Authority.Integrations.IntegrationState.Settings",
        "IntoChat.OS.Authority.Secrets.SecretsState.Fields",
        "IntoChat.OS.Modules.Flame.Scene.SceneNode.Parameters",
        "IntoChat.OS.Modules.Flutter.Calendar.CalendarState.Selected",
        "IntoChat.OS.Modules.Flutter.Card.CardState.Children",
        "IntoChat.OS.Modules.Flutter.Chart.ChartState.Points",
        "IntoChat.OS.Modules.Flutter.Expander.ExpanderState.Children",
        "IntoChat.OS.Modules.Flutter.Form.FormFieldState.Choices",
        "IntoChat.OS.Modules.Flutter.Form.FormState.Fields",
        "IntoChat.OS.Modules.Flutter.Graph.GraphState.Edges",
        "IntoChat.OS.Modules.Flutter.Graph.GraphState.Nodes",
        "IntoChat.OS.Modules.Flutter.Map.MapState.Markers",
        "IntoChat.OS.Modules.Flutter.Sheet.SheetState.Cells",
        "IntoChat.OS.Modules.Flutter.Table.TableState.Columns",
        "IntoChat.OS.Modules.Flutter.Table.TableState.Rows",
        "IntoChat.OS.Modules.Flutter.Tabs.TabsState.Tabs",
        "IntoChat.OS.Modules.Flutter.Tree.TreeState.Nodes",
    };

    internal static IEnumerable<string> ConcreteCollectionMembers(Type type) => Members(type)
        .Where(member => !member.IsDefined(typeof(LegacyShapeAttribute)) && ContainsConcreteCollection(ContractRules.MemberType(member)))
        .Select(member => $"{type.FullName}.{member.Name}");

    private static readonly Type[] ConcreteCollections =
        [typeof(Dictionary<,>), typeof(List<>), typeof(HashSet<>), typeof(SortedDictionary<,>), typeof(Queue<>), typeof(Stack<>)];

    private static bool ContainsConcreteCollection(Type type) =>
        (type.IsGenericType && ConcreteCollections.Contains(type.GetGenericTypeDefinition()))
        || (type.HasElementType && ContainsConcreteCollection(type.GetElementType()!))
        || (type.IsGenericType && type.GetGenericArguments().Any(ContainsConcreteCollection));

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
                    && (ancestor.GetGenericTypeDefinition() == typeof(Grain<>)
                        // A plain neuron (06-04 on) persists its TState through the runtime; the same shape rules hold.
                        || ancestor.GetGenericTypeDefinition() == typeof(DigitalBrain.Neuron<>)))
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
