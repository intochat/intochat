using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DigitalBrain.UI;
using static IntoChat.OS.Architecture.Tests.Unit.NodeSchema;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Units all the way: the client renders DigitalBrain.UI.View as the server serialises it. The Dart node models are generated
// from the C# records here (one sealed class per [JsonDerivedType], plain classes for the records they hold, Dart enums for the
// C# enums), and the file on disk must be what this generator writes. With INTOCHAT_REGENERATE_DART=1 the fact rewrites the
// file instead; run `dart format` on it afterwards (the comparison ignores layout and trailing commas, which dart format owns).
public sealed partial class DartNodeModelsFacts
{
    private static readonly string Target = Path.Combine(Repository.Root, "ui", "lib", "s" + "rc", "generated", "node_models.dart");

    [Fact]
    public void TheDartNodeModelsAreGeneratedFromTheCSharpNodeModel()
    {
        var generated = Generate();
        if (Environment.GetEnvironmentVariable("INTOCHAT_REGENERATE_DART") == "1")
        {
            File.WriteAllText(Target, generated);
            return;
        }
        Assert.True(File.Exists(Target), $"{Target} is missing; run this fact with INTOCHAT_REGENERATE_DART=1, then dart format.");
        Assert.True(Normalize(File.ReadAllText(Target)) == Normalize(generated),
            "node_models.dart differs from the C# node model; run this fact with INTOCHAT_REGENERATE_DART=1, then dart format.");
    }

    [Fact]
    public void EveryDerivedNodeKindIsGenerated()
    {
        var generated = Generate();
        foreach (var (type, kind) in DerivedKinds())
        {
            Assert.Contains($"final class {DartName(type)} extends Node", generated, StringComparison.Ordinal);
            Assert.Contains($"'{kind}' => {DartName(type)}.fromJson(json)", generated, StringComparison.Ordinal);
        }
    }

    // The app view fixtures (views/{app}/*.json) feed the client's app-view goldens: every node they hold must be a kind
    // the generated models know, or the golden shows "This component cannot be displayed.".
    [Fact]
    public void EveryViewFixtureNodeIsAGeneratedKind()
    {
        var known = DerivedKinds().Select(entry => entry.Kind).ToHashSet(StringComparer.Ordinal);
        var slots = DerivedKinds().SelectMany(entry => Properties(entry.Type))
            .Where(property => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(Node) || Element(property.PropertyType) == typeof(Node))
            .Select(JsonName).Append("root").ToHashSet(StringComparer.Ordinal);
        var views = Path.Combine(Repository.Projects["IntoChat.OS.Apps"].Directory, "views");
        var fixtures = Directory.EnumerateFiles(views, "*.json", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(fixtures);
        var offenders = new List<string>();
        void Visit(JsonElement element, bool slot, string file)
        {
            if (element.ValueKind == JsonValueKind.Array) { foreach (var item in element.EnumerateArray()) { Visit(item, slot, file); } return; }
            if (element.ValueKind != JsonValueKind.Object) { return; }
            if (slot && (!element.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || !known.Contains(kind.GetString()!)))
            { offenders.Add($"{Path.GetFileName(file)}: {(element.TryGetProperty("kind", out var k) ? k.ToString() : "(no kind)")}"); }
            foreach (var property in element.EnumerateObject()) { Visit(property.Value, slots.Contains(property.Name), file); }
        }
        foreach (var file in fixtures) { Visit(JsonDocument.Parse(File.ReadAllText(file)).RootElement, false, file); }
        Assert.True(offenders.Count == 0, "View fixtures hold node kinds the generated models do not know:\n" + string.Join('\n', offenders.Distinct()));
    }

    internal static string Normalize(string source)
        => TrailingComma().Replace(Whitespace().Replace(source, ""), "$1");

    private static string FieldName(PropertyInfo property)
    {
        var name = JsonName(property);
        return name is "default" or "switch" or "enum" or "class" ? name + "Value" : name;
    }

    // DigitalBrain.UI and its sub-namespaces (DigitalBrain.UI.Controls holds what control neurons draw).
    private static bool IsUiModel(Type type) => type.Namespace == typeof(Node).Namespace || type.Namespace?.StartsWith(typeof(Node).Namespace + ".", StringComparison.Ordinal) == true;

    internal static string Generate()
    {
        var records = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        var enums = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        void Walk(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsEnum) { enums.TryAdd(type.Name, type); return; }
            if (Element(type) is { } element) { Walk(element); return; }
            if (!IsUiModel(type) || type == typeof(Node) || !type.IsClass) { return; }
            if (!records.TryAdd(DartName(type), type)) { return; }
            foreach (var property in Properties(type)) { Walk(property.PropertyType); }
        }
        Walk(typeof(View));
        foreach (var (type, _) in DerivedKinds()) { Walk(type); }
        // A control's props ride on its slot (NeuronRef.Props as JSON); the client reads them with these models.
        foreach (var props in typeof(DigitalBrain.UI.ControlProps).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false } && type.IsSubclassOf(typeof(DigitalBrain.UI.ControlProps))))
        { Walk(props); }

        var kinds = DerivedKinds().ToDictionary(entry => entry.Type, entry => entry.Kind);
        var dart = new StringBuilder();
        dart.Append("// This is a generated file - do not edit.\n//\n// Generated from DigitalBrain.UI by DartNodeModelsFacts (IntoChat.OS.Architecture.Tests.Unit).\n\n");
        dart.Append("// ignore_for_file: prefer_const_constructors\n\n");
        dart.Append("Map<String, dynamic> _map(Object? value) =>\n    value is Map ? Map<String, dynamic>.from(value) : const <String, dynamic>{};\n\n");
        dart.Append("List<Object?> _list(Object? value) => value is List ? value : const [];\n\n");
        dart.Append("String _string(Object? value, [String fallback = '']) =>\n    value is String ? value : fallback;\n\n");
        dart.Append("String? _stringOrNull(Object? value) => value is String ? value : null;\n\n");
        dart.Append("double _double(Object? value, [double fallback = 0]) =>\n    value is num ? value.toDouble() : fallback;\n\n");
        dart.Append("double? _doubleOrNull(Object? value) => value is num ? value.toDouble() : null;\n\n");
        dart.Append("int _int(Object? value, [int fallback = 0]) =>\n    value is num ? value.toInt() : fallback;\n\n");
        dart.Append("int? _intOrNull(Object? value) => value is num ? value.toInt() : null;\n\n");
        dart.Append("bool _bool(Object? value, [bool fallback = false]) =>\n    value is bool ? value : fallback;\n\n");
        dart.Append("T _enum<T extends Enum>(List<T> values, Object? value) =>\n    _enumOrNull(values, value) ?? values.first;\n\n");
        dart.Append("T? _enumOrNull<T extends Enum>(List<T> values, Object? value) {\n  if (value is num) {\n    final index = value.toInt();\n    return index >= 0 && index < values.length ? values[index] : null;\n  }\n  final name = '$value'.toLowerCase();\n  for (final candidate in values) {\n    if (candidate.name.toLowerCase() == name) {\n      return candidate;\n    }\n  }\n  return null;\n}\n\n");

        foreach (var (name, type) in enums)
        {
            var values = Enum.GetNames(type).Select(JsonNamingPolicy.CamelCase.ConvertName);
            dart.Append($"enum {name} {{ {string.Join(", ", values)} }}\n\n");
        }

        dart.Append("/// A node of a view, discriminated by `kind`.\nsealed class Node {\n  const Node();\n\n  /// The wire discriminator.\n  String get kind;\n\n");
        dart.Append("  static Node fromJson(Map<String, dynamic> json) => switch (json['kind']) {\n");
        foreach (var (type, kind) in DerivedKinds()) { dart.Append($"    '{kind}' => {DartName(type)}.fromJson(json),\n"); }
        dart.Append("    final kind => UnknownNode(kind is String ? kind : '', json),\n  };\n}\n\n");
        dart.Append("/// A kind this client does not know; the renderer shows it as unsupported.\nfinal class UnknownNode extends Node {\n  const UnknownNode(this.kind, this.json);\n  @override\n  final String kind;\n  final Map<String, dynamic> json;\n}\n\n");

        foreach (var (name, type) in records)
        {
            var isNode = typeof(Node).IsAssignableFrom(type);
            var properties = Properties(type);
            dart.Append($"final class {name}{(isNode ? " extends Node" : "")} {{\n");
            dart.Append($"  const {name}({{{string.Join(", ", properties.Select(property => $"required this.{FieldName(property)}"))}}});\n\n");
            if (isNode && kinds.TryGetValue(type, out var kind)) { dart.Append($"  @override\n  String get kind => '{kind}';\n\n"); }
            foreach (var property in properties) { dart.Append($"  final {DartType(property)} {FieldName(property)};\n"); }
            dart.Append($"\n  factory {name}.fromJson(Map<String, dynamic> json) => {name}(\n");
            foreach (var property in properties) { dart.Append($"    {FieldName(property)}: {Read(property.PropertyType, IsNullable(property), $"json['{JsonName(property)}']", Default(type, property))},\n"); }
            dart.Append("  );\n}\n\n");
        }
        return dart.ToString();
    }

    // A value the server leaves out falls back to the record's own parameter default (a button is enabled), else the type's.
    private static string? Default(Type record, PropertyInfo property)
    {
        var parameter = record.GetConstructors().SelectMany(constructor => constructor.GetParameters())
            .FirstOrDefault(candidate => candidate.Name == property.Name && candidate.HasDefaultValue);
        return parameter?.DefaultValue switch
        {
            true => "true",
            double number when number != 0 => number.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture),
            int number when number != 0 => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string text when text.Length > 0 => "'" + text.Replace("'", "\'", StringComparison.Ordinal) + "'",
            _ => null,
        };
    }

    private static string Read(Type type, bool nullable, string value, string? fallback = null, int depth = 0)
    {
        var or = fallback is null ? "" : ", " + fallback;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (Element(type) is { } element)
        {
            var item = depth == 0 ? "item" : $"item{depth}";
            return $"{(nullable ? $"{value} == null ? null : " : "")}[for (final {item} in _list({value})) {Read(element, false, item, null, depth + 1)}]";
        }
        if (type == typeof(string)) { return nullable ? $"_stringOrNull({value})" : $"_string({value}{or})"; }
        if (type == typeof(double)) { return nullable ? $"_doubleOrNull({value})" : $"_double({value}{or})"; }
        if (type == typeof(int)) { return nullable ? $"_intOrNull({value})" : $"_int({value}{or})"; }
        if (type == typeof(bool)) { return $"_bool({value}{or})"; }
        if (type == typeof(JsonElement)) { return value; }
        if (type.IsEnum) { return nullable ? $"_enumOrNull({type.Name}.values, {value})" : $"_enum({type.Name}.values, {value})"; }
        var parse = type == typeof(Node) ? "Node.fromJson" : $"{DartName(type)}.fromJson";
        return nullable ? $"{value} is Map ? {parse}(_map({value})) : null" : $"{parse}(_map({value}))";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@",([\)\]\}])")]
    private static partial Regex TrailingComma();
}
