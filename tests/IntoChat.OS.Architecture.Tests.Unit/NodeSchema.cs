using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.UI;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The reflection over the C# node model that the Dart generators share: node kinds, wire names, Dart types, event properties
// ([EventArgs]) and showcase examples ([Showcase]). It writes node_events.dart and showcase_catalog.json.
internal static class NodeSchema
{
    internal static (Type Type, string Kind)[] DerivedKinds() => [.. typeof(Node).GetCustomAttributes<JsonDerivedTypeAttribute>()
        .Select(attribute => (attribute.DerivedType, (string)attribute.TypeDiscriminator!))];

    internal static string DartName(Type type) => type == typeof(View) ? "AppView"
        : typeof(Node).IsAssignableFrom(type) && !type.Name.EndsWith("Node", StringComparison.Ordinal) ? type.Name + "Node"
        : type.Name;

    private static readonly NullabilityInfoContext Nullability = new();

    internal static PropertyInfo[] Properties(Type type) => [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.GetMethod is not null && property.Name != "EqualityContract"
            && property.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always })
        .OrderBy(property => property.MetadataToken)];

    internal static string JsonName(PropertyInfo property)
        => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    internal static Type? Element(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
        ? type.GetGenericArguments()[0] : null;

    internal static bool IsNullable(PropertyInfo property) => Nullability.Create(property).ReadState == NullabilityState.Nullable;

    internal static string DartType(PropertyInfo property) => DartType(property.PropertyType) + (IsNullable(property) ? "?" : "");

    internal static string DartType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (Element(type) is { } element) { return $"List<{DartType(element)}>"; }
        if (type == typeof(string)) { return "String"; }
        if (type == typeof(double)) { return "double"; }
        if (type == typeof(int)) { return "int"; }
        if (type == typeof(bool)) { return "bool"; }
        if (type == typeof(JsonElement)) { return "Object"; }
        if (type == typeof(Node)) { return "Node"; }
        if (type.IsEnum) { return type.Name; }
        if (type.Namespace == typeof(Node).Namespace) { return DartName(type); }
        throw new NotSupportedException($"No Dart mirror for {type}.");
    }

    private static ParameterInfo[] ConstructorParameters(Type type)
        => [.. type.GetConstructors().OrderByDescending(constructor => constructor.GetParameters().Length).First().GetParameters()];

    private static ParameterInfo? Parameter(Type type, PropertyInfo property)
        => ConstructorParameters(type).FirstOrDefault(parameter => parameter.Name == property.Name);

    // A positional record parameter's attributes sit on the parameter; [property: ...] puts them on the property.
    private static IEnumerable<T> Attributes<T>(Type type, PropertyInfo property) where T : Attribute
        => property.GetCustomAttributes<T>().Concat(Parameter(type, property)?.GetCustomAttributes<T>() ?? []);

    internal static IEnumerable<(PropertyInfo Property, Type Args)> Events(Type _)
        => []; // event args are gone; Task 4 rewrites the generator around commands

    // Positional record properties of an args record (EqualityContract excluded).
    internal static PropertyInfo[] ArgsFields(Type args) => Properties(args);

    private static string ControlName(Type type)
        => type.Name.EndsWith("Node", StringComparison.Ordinal) ? type.Name[..^"Node".Length] : type.Name;

    private static string EventName(PropertyInfo property)
        => property.Name.StartsWith("On", StringComparison.Ordinal) ? property.Name["On".Length..] : property.Name;

    internal static string GenerateEvents()
    {
        var dart = new StringBuilder();
        dart.Append("// This is a generated file - do not edit.\n//\n// Generated from DigitalBrain.UI by DartEventsFacts (IntoChat.OS.Architecture.Tests.Unit).\n\n");
        var helpers = DerivedKinds().OrderBy(entry => entry.Kind, StringComparer.Ordinal)
            .SelectMany(entry => Events(entry.Type).Select(@event => (entry.Type, @event.Property, @event.Args))).ToArray();
        if (helpers.Length == 0)
        {
            dart.Append("// No control declares an event yet.\n");
            return dart.ToString();
        }
        dart.Append("import '../nodes/node_context.dart';\nimport 'node_models.dart';\n\n");
        foreach (var (type, property, args) in helpers)
        {
            var fields = ArgsFields(args);
            var named = fields.Length == 0 ? "" : ", {" + string.Join(", ", fields.Select(field => $"required {DartType(field)} {JsonName(field)}")) + "}";
            dart.Append($"/// Emits `{JsonName(property)}` of a `{ControlName(type)}` with a `{args.Name}` payload.\n");
            dart.Append($"Future<void> emit{ControlName(type)}{EventName(property)}(NodeContext ctx, {DartName(type)} node{named}) =>\n");
            dart.Append($"    ctx.emitEvent(node.key, node.{JsonName(property)}, {{{string.Join(", ", fields.Select(field => $"'{JsonName(field)}': {JsonName(field)}"))}}});\n\n");
        }
        return dart.ToString();
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static string GenerateCatalog()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartArray();
            foreach (var (kind, type) in ViewNodeAttribute.DiscoverAll().OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                json.WriteStartObject();
                json.WriteString("kind", kind);
                json.WriteStartArray("props");
                var events = Events(type).Select(@event => @event.Property).ToHashSet();
                foreach (var property in Properties(type).Where(property => !events.Contains(property)))
                {
                    json.WriteStartObject();
                    json.WriteString("name", JsonName(property));
                    json.WriteString("type", DartType(property));
                    json.WritePropertyName("default");
                    var parameter = Parameter(type, property);
                    if (parameter is { HasDefaultValue: true }) { JsonSerializer.Serialize(json, parameter.DefaultValue, Web); }
                    else { json.WriteNullValue(); }
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("events");
                foreach (var (property, args) in Events(type))
                {
                    json.WriteStartObject();
                    json.WriteString("name", JsonName(property));
                    json.WriteStartObject("args");
                    foreach (var field in ArgsFields(args)) { json.WriteString(JsonName(field), DartType(field)); }
                    json.WriteEndObject();
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("examples");
                foreach (var example in Examples(kind, type)) { example.WriteTo(json); }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n") + "\n";
    }

    // A [Showcase] example is either a whole node (an object with `kind`) or one value of its property, shown as `{kind, prop: value}`.
    internal static IEnumerable<JsonElement> Examples(string kind, Type type)
    {
        foreach (var property in Properties(type))
        {
            foreach (var showcase in Attributes<ShowcaseAttribute>(type, property))
            {
                var value = JsonDocument.Parse(showcase.ExampleJson).RootElement.Clone();
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("kind", out _)) { yield return value; continue; }
                var node = new Dictionary<string, JsonElement> { ["kind"] = JsonSerializer.SerializeToElement(kind), [JsonName(property)] = value };
                yield return JsonSerializer.SerializeToElement(node);
            }
        }
    }

    // Check-or-regenerate: with INTOCHAT_REGENERATE_DART=1 the file is rewritten; otherwise it must match (layout ignored).
    internal static void CheckOrRegenerate(string target, string generated, string fact)
    {
        if (Environment.GetEnvironmentVariable("INTOCHAT_REGENERATE_DART") == "1")
        {
            File.WriteAllText(target, generated);
            return;
        }
        Assert.True(File.Exists(target), $"{target} is missing; run {fact} with INTOCHAT_REGENERATE_DART=1, then dart format.");
        Assert.True(DartNodeModelsFacts.Normalize(File.ReadAllText(target)) == DartNodeModelsFacts.Normalize(generated),
            $"{Path.GetFileName(target)} differs from the C# node model; run {fact} with INTOCHAT_REGENERATE_DART=1, then dart format.");
    }
}
