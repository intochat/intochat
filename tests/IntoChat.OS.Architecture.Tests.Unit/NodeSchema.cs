using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The reflection over the C# UI model that the Dart generators share: node kinds, wire names, Dart types, control neurons with
// their commands ([On] reactions) and facts ([Publishes]), and showcase examples ([Showcase]). It writes node_commands.dart and showcase_catalog.json.
internal static class NodeSchema
{
    internal static (Type Type, string Kind)[] DerivedKinds() => [.. typeof(Node).GetCustomAttributes<JsonDerivedTypeAttribute>()
        .Select(attribute => (attribute.DerivedType, (string)attribute.TypeDiscriminator!))];

    internal static string DartName(Type type) => type == typeof(View) ? "AppView"
        : typeof(Node).IsAssignableFrom(type) && !type.Name.EndsWith("Node", StringComparison.Ordinal) ? type.Name + "Node"
        : type.Name;


    internal static PropertyInfo[] Properties(Type type) => [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.GetMethod is not null && property.Name != "EqualityContract"
            && property.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always })
        .OrderBy(property => property.MetadataToken)];

    internal static string JsonName(PropertyInfo property)
        => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    internal static Type? Element(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
        ? type.GetGenericArguments()[0] : null;

    // NullabilityInfoContext caches without locking and is not thread-safe: one shared instance raced when the catalog, commands and
    // UI Kit facts ran in parallel. A context per call costs little.
    internal static bool IsNullable(PropertyInfo property) => new NullabilityInfoContext().Create(property).ReadState == NullabilityState.Nullable;

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

    // A control neuron: a concrete Screen<TState> under IntoChat.OS.Modules.UI.Controls (src/Modules/UI/Controls, one file per control).
    internal const string ControlsNamespace = "IntoChat.OS.Modules.UI.Controls";

    internal static Type[] ControlNeurons() => [.. typeof(IntoChat.OS.Modules.UI.UiModule).Assembly.GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false } && StateOf(type) is not null
            && (type.Namespace == ControlsNamespace || type.Namespace?.StartsWith(ControlsNamespace + ".", StringComparison.Ordinal) == true))
        .OrderBy(type => type.Name, StringComparer.Ordinal)];

    // TState of the Screen<TState> a type derives from, or null.
    internal static Type? StateOf(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Screen<>)) { return current.GetGenericArguments()[0]; }
        }
        return null;
    }

    // TProps of the ControlNeuron<TState, TProps> a control derives from: the props record its parent writes on the slot.
    internal static Type? PropsOf(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(IntoChat.OS.Modules.UI.Controls.ControlNeuron<,>)) { return current.GetGenericArguments()[1]; }
        }
        return null;
    }

    // The catalog kind of a control neuron: its class name without "Neuron", camel-cased (CheckBox -> checkBox).
    internal static string NeuronKind(Type type) => JsonNamingPolicy.CamelCase.ConvertName(ControlName(type));

    internal static readonly string CommandsNamespace = typeof(DigitalBrain.UI.Commands.SetChecked).Namespace!;

    // A control's commands: the [On] reactions it declares whose one parameter is a DigitalBrain.UI.Commands record.
    internal static Type[] Commands(Type type) => [.. type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(method => method.GetCustomAttribute<OnAttribute>() is not null)
        .Select(method => method.GetParameters())
        .Where(parameters => parameters.Length == 1 && parameters[0].ParameterType.Namespace == CommandsNamespace)
        .Select(parameters => parameters[0].ParameterType)
        .Distinct().OrderBy(command => command.Name, StringComparer.Ordinal)];

    // A control's facts: its [Publishes] attributes (what it passes to Show(next, facts) is not visible to reflection).
    internal static Type[] Signals(Type type) => [.. type.GetCustomAttributes<PublishesAttribute>().Select(emits => emits.Signal)
        .Distinct().OrderBy(signal => signal.Name, StringComparer.Ordinal)];

    internal static string Alias(Type signal) => signal.GetCustomAttribute<SignalAttribute>(false)?.Alias ?? signal.Name;

    // Positional record properties of a command or signal record (EqualityContract excluded).
    internal static PropertyInfo[] ArgsFields(Type args) => Properties(args);

    private static string ControlName(Type type)
        => type.Name.EndsWith("Neuron", StringComparison.Ordinal) ? type.Name[..^"Neuron".Length]
        : type.Name.EndsWith("Node", StringComparison.Ordinal) ? type.Name[..^"Node".Length] : type.Name;

    private static string ArgDartType(PropertyInfo field)
        => field.PropertyType == typeof(IReadOnlyDictionary<string, string>) ? "Map<String, String>"
            : IsRecordArg(field.PropertyType) ? "Map<String, Object?>"
            : Element(field.PropertyType) is { } element && IsRecordArg(element) ? "List<Map<String, Object?>>"
            : DartType(field);

    // A record argument (a live chart's point) is sent as its wire JSON object; the client holds no model for it.
    private static bool IsRecordArg(Type type) => type is { IsClass: true } && type != typeof(string) && !typeof(Node).IsAssignableFrom(type) && type.Namespace == typeof(Node).Namespace;

    internal static string SenderName(Type neuron, Type command) => $"act{ControlName(neuron)}{command.Name}";

    internal static string GenerateCommands() => GenerateCommands(ControlNeurons());

    // One typed sender per control neuron command: act<Kind><Command>(ctx, address, <args>) posts it to the neuron at `address`.
    internal static string GenerateCommands(IEnumerable<Type> neurons)
    {
        var dart = new StringBuilder();
        dart.Append("// This is a generated file - do not edit.\n//\n// Generated from the control neurons by DartCommandsFacts (IntoChat.OS.Architecture.Tests.Unit).\n\n");
        var senders = neurons.OrderBy(NeuronKind, StringComparer.Ordinal)
            .SelectMany(neuron => Commands(neuron).Select(command => (neuron, command))).ToArray();
        if (senders.Length == 0)
        {
            dart.Append("// No control neuron declares a command yet.\n");
            return dart.ToString();
        }
        dart.Append("import '../nodes/node_context.dart';\n\n");
        foreach (var (neuron, command) in senders)
        {
            var fields = ArgsFields(command);
            var named = fields.Length == 0 ? "" : ", {" + string.Join(", ", fields.Select(field => $"required {ArgDartType(field)} {DartIdentifier(field)}")) + "}";
            dart.Append($"/// Sends `{command.Name}` to the `{NeuronKind(neuron)}` neuron at [address].\n");
            dart.Append($"Future<ActivationOutcome> {SenderName(neuron, command)}(NodeContext ctx, String address{named}) =>\n");
            dart.Append($"    ctx.act(address, '{Alias(command)}', {{{string.Join(", ", fields.Select(field => $"'{JsonName(field)}': {DartIdentifier(field)}"))}}});\n\n");
        }
        return dart.ToString();
    }

    private static string DartIdentifier(PropertyInfo property)
    {
        var name = JsonName(property);
        return name is "default" or "switch" or "enum" or "class" ? name + "Value" : name;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static string GenerateCatalog() => GenerateCatalog(ControlNeurons());

    // Every [ViewNode] kind and every control neuron kind as {kind, isNeuron, props, state, commands, signals, examples}, sorted by kind.
    // A node's props are its fields; a control neuron's props are its props record (what the parent writes on the slot) and its state
    // what the user changes (what the neuron stores).
    internal static string GenerateCatalog(IEnumerable<Type> neurons)
    {
        var entries = ViewNodeAttribute.DiscoverAll().Select(entry => (Kind: entry.Key, Type: entry.Value, IsNeuron: false))
            .Concat(neurons.Select(neuron => (Kind: NeuronKind(neuron), Type: neuron, IsNeuron: true)))
            .OrderBy(entry => entry.Kind, StringComparer.Ordinal).ToArray();
        var duplicates = entries.GroupBy(entry => entry.Kind, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicates.Length > 0) { throw new InvalidOperationException("Two catalog entries share a kind: " + string.Join(", ", duplicates)); }
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartArray();
            foreach (var (kind, type, isNeuron) in entries)
            {
                json.WriteStartObject();
                json.WriteString("kind", kind);
                json.WriteBoolean("isNeuron", isNeuron);
                // A neuron without a props record (a test sample) lists none; ControlSchemaFacts requires one of every control neuron.
                if ((isNeuron ? PropsOf(type) : type) is { } props) { WriteShape(json, "props", props); }
                else { json.WriteStartArray("props"); json.WriteEndArray(); }
                if (isNeuron) { WriteShape(json, "state", StateOf(type)!); }
                else { json.WriteStartArray("state"); json.WriteEndArray(); }
                WriteRecords(json, "commands", isNeuron ? Commands(type) : []);
                WriteRecords(json, "signals", isNeuron ? Signals(type) : []);
                json.WriteStartArray("examples");
                foreach (var example in isNeuron ? NeuronExamples(type) : Examples(kind, type)) { example.WriteTo(json); }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n") + "\n";
    }

    private static void WriteShape(Utf8JsonWriter json, string name, Type shape)
    {
        json.WriteStartArray(name);
        foreach (var property in Properties(shape))
        {
            json.WriteStartObject();
            json.WriteString("name", JsonName(property));
            json.WriteString("type", ArgDartType(property));
            json.WritePropertyName("default");
            var parameter = Parameter(shape, property);
            if (parameter is { HasDefaultValue: true }) { JsonSerializer.Serialize(json, parameter.DefaultValue, Web); }
            else { json.WriteNullValue(); }
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private static void WriteRecords(Utf8JsonWriter json, string name, Type[] records)
    {
        json.WriteStartArray(name);
        foreach (var record in records)
        {
            json.WriteStartObject();
            json.WriteString("name", record.Name);
            json.WriteString("signal", Alias(record));
            json.WriteStartObject("args");
            foreach (var field in ArgsFields(record)) { json.WriteString(JsonName(field), ArgDartType(field)); }
            json.WriteEndObject();
            json.WriteEndObject();
        }
        json.WriteEndArray();
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

    // A control neuron's examples are [Showcase] attributes on its state's properties, each a whole node of the kind its Render draws
    // with the slot's props beside it ({kind, key, <state>, props: {...}}).
    internal static IEnumerable<JsonElement> NeuronExamples(Type neuron)
    {
        var state = StateOf(neuron)!;
        return Properties(state).SelectMany(property => Attributes<ShowcaseAttribute>(state, property))
            .Select(showcase => JsonDocument.Parse(showcase.ExampleJson).RootElement.Clone());
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
