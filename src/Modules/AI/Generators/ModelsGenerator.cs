using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace IntoChat.OS.Modules.AI.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class ModelsGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidCatalog = new(
        id: "ICAI001",
        title: "Invalid models.json catalog",
        messageFormat: "{0}",
        category: "IntoChat.AI",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly HashSet<string> ValidProviders = new(StringComparer.Ordinal)
    {
        "OpenAI", "Anthropic", "Google", "XAI", "Ollama", "FoundryLocal", "OpenRouter", "TypeSafe", "Host", "OpenAICompatible",
    };

    private static readonly HashSet<string> ValidWheres = new(StringComparer.Ordinal)
    {
        "Device", "Server", "Cloud",
    };

    private static readonly HashSet<string> ValidAbilities = new(StringComparer.Ordinal)
    {
        "None", "Text", "Tools", "Vision", "Reasoning", "StructuredOutput", "Hear", "Speak", "Talk", "Embed", "Image",
    };

    private static readonly HashSet<string> ValidTiers = new(StringComparer.Ordinal)
    {
        "Fast", "Balanced", "Deep",
    };

    private static readonly HashSet<string> ValidRoles = new(StringComparer.Ordinal)
    {
        "Chat", "Think", "Fast", "Hear", "Speak", "Talk", "Embed", "Image",
    };

    private static readonly HashSet<string> AllowedRootKeys = new(StringComparer.Ordinal)
    {
        "version", "defaults", "models",
    };

    private static readonly HashSet<string> AllowedDefaultsKeys = new(StringComparer.Ordinal)
    {
        "device", "cloud",
    };

    private static readonly HashSet<string> AllowedModelKeys = new(StringComparer.Ordinal)
    {
        "id", "member", "provider", "family", "name", "where", "can", "tier", "bytes", "aliases", "price",
    };

    private static readonly HashSet<string> AllowedPriceKeys = new(StringComparer.Ordinal)
    {
        "input", "output", "cached", "reasoning",
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Where(static file => string.Equals(Path.GetFileName(file.Path), "models.json", StringComparison.OrdinalIgnoreCase))
            .Select(static (file, cancellationToken) => (file.Path, Text: file.GetText(cancellationToken)?.ToString()));

        context.RegisterSourceOutput(files, static (spc, item) => Execute(spc, item.Path, item.Text));
    }

    private static void Execute(SourceProductionContext context, string path, string? text)
    {
        if (text is null)
        {
            Report(context, $"Could not read '{path}'.");
            return;
        }

        JsonValue rootValue;
        try
        {
            rootValue = JsonParser.Parse(text);
        }
        catch (FormatException ex)
        {
            Report(context, $"Malformed models.json ('{path}'): {ex.Message}");
            return;
        }

        if (rootValue.Kind != JsonKind.Object || rootValue.ObjectValue is null)
        {
            Report(context, "models.json root must be a JSON object.");
            return;
        }

        var root = rootValue.ObjectValue;
        foreach (var pair in root)
        {
            if (!AllowedRootKeys.Contains(pair.Key))
            {
                Report(context, $"Unknown top-level member '{pair.Key}' in models.json.");
                return;
            }
        }

        if (!TryGetProperty(root, "version", out var versionVal)
            || versionVal.Kind != JsonKind.Number
            || !int.TryParse(versionVal.RawNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            || version != 1)
        {
            Report(context, "models.json must specify \"version\": 1.");
            return;
        }

        if (!TryGetProperty(root, "models", out var modelsVal)
            || modelsVal.Kind != JsonKind.Array
            || modelsVal.ArrayValue is null)
        {
            Report(context, "models.json must contain a \"models\" array.");
            return;
        }

        var entries = new List<ModelEntry>(modelsVal.ArrayValue.Count);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenMembers = new HashSet<string>(StringComparer.Ordinal);
        var seenLookupKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < modelsVal.ArrayValue.Count; i++)
        {
            var item = modelsVal.ArrayValue[i];
            if (item.Kind != JsonKind.Object || item.ObjectValue is null)
            {
                Report(context, $"models[{i}] must be a JSON object.");
                return;
            }

            if (!ParseModelEntry(context, i, item.ObjectValue, out var entry))
            {
                return;
            }

            if (!seenIds.Add(entry.Id))
            {
                Report(context, $"Duplicate model id '{entry.Id}'.");
                return;
            }

            if (!seenMembers.Add(entry.Member))
            {
                Report(context, $"Duplicate model member '{entry.Member}'.");
                return;
            }

            var idKey = entry.Id.ToLowerInvariant();
            if (seenLookupKeys.TryGetValue(idKey, out var conflictMember) && conflictMember != entry.Member)
            {
                Report(context, $"Model '{entry.Id}' conflicts on lookup key '{idKey}' with '{conflictMember}'.");
                return;
            }
            seenLookupKeys[idKey] = entry.Member;

            foreach (var alias in entry.Aliases)
            {
                var aliasKey = alias.Trim().ToLowerInvariant();
                if (aliasKey.Length == 0) { continue; }
                if (seenLookupKeys.TryGetValue(aliasKey, out var existing) && existing != entry.Member)
                {
                    Report(context, $"Model '{entry.Id}' alias '{alias}' conflicts with '{existing}'.");
                    return;
                }
                seenLookupKeys[aliasKey] = entry.Member;
            }

            entries.Add(entry);
        }

        var deviceDefaults = new List<KeyValuePair<string, string>>();
        var cloudDefaults = new List<KeyValuePair<string, string>>();
        if (TryGetProperty(root, "defaults", out var defaultsVal) && defaultsVal.Kind != JsonKind.Null)
        {
            if (defaultsVal.Kind != JsonKind.Object || defaultsVal.ObjectValue is null)
            {
                Report(context, "models.json \"defaults\" must be an object.");
                return;
            }

            foreach (var pair in defaultsVal.ObjectValue)
            {
                if (!AllowedDefaultsKeys.Contains(pair.Key))
                {
                    Report(context, $"Unknown member '{pair.Key}' in models.json defaults.");
                    return;
                }
            }

            if (TryGetProperty(defaultsVal.ObjectValue, "device", out var deviceVal) && deviceVal.Kind != JsonKind.Null)
            {
                if (!ParseDefaultMap(context, "defaults.device", deviceVal, seenIds, deviceDefaults))
                {
                    return;
                }
            }

            if (TryGetProperty(defaultsVal.ObjectValue, "cloud", out var cloudVal) && cloudVal.Kind != JsonKind.Null)
            {
                if (!ParseDefaultMap(context, "defaults.cloud", cloudVal, seenIds, cloudDefaults))
                {
                    return;
                }
            }
        }

        var source = Emit(entries, deviceDefaults, cloudDefaults);
        context.AddSource("Models.g.cs", SourceText.From(source, Encoding.UTF8));
    }

    private static bool ParseModelEntry(
        SourceProductionContext context,
        int index,
        List<KeyValuePair<string, JsonValue>> obj,
        out ModelEntry entry)
    {
        entry = default!;
        var label = TryGetString(obj, "id") ?? TryGetString(obj, "member") ?? $"models[{index}]";

        foreach (var pair in obj)
        {
            if (!AllowedModelKeys.Contains(pair.Key))
            {
                Report(context, $"Unknown member '{pair.Key}' on model entry '{label}'.");
                return false;
            }
        }

        if (!RequireNonEmptyString(context, label, obj, "id", out var id)) { return false; }
        if (!RequireNonEmptyString(context, label, obj, "member", out var member)) { return false; }
        if (!IsValidIdentifier(member))
        {
            Report(context, $"Model entry '{label}' has invalid C# identifier member '{member}'.");
            return false;
        }

        if (!RequireEnum(context, label, obj, "provider", ValidProviders, out var provider)) { return false; }
        if (!RequireNonEmptyString(context, label, obj, "family", out var family)) { return false; }
        if (!RequireNonEmptyString(context, label, obj, "name", out var name)) { return false; }
        if (!RequireEnum(context, label, obj, "where", ValidWheres, out var where)) { return false; }
        if (!RequireEnum(context, label, obj, "tier", ValidTiers, out var tier)) { return false; }

        if (!TryGetProperty(obj, "can", out var canVal) || canVal.Kind != JsonKind.Array || canVal.ArrayValue is null || canVal.ArrayValue.Count == 0)
        {
            Report(context, $"Model entry '{label}' must define a non-empty \"can\" array.");
            return false;
        }

        var abilities = new List<string>(canVal.ArrayValue.Count);
        foreach (var item in canVal.ArrayValue)
        {
            if (item.Kind != JsonKind.String || item.StringValue is null || !ValidAbilities.Contains(item.StringValue))
            {
                Report(context, $"Model entry '{label}' has invalid ability '{item.StringValue ?? item.Kind.ToString()}'.");
                return false;
            }
            abilities.Add(item.StringValue);
        }

        long? bytes = null;
        if (TryGetProperty(obj, "bytes", out var bytesVal) && bytesVal.Kind != JsonKind.Null)
        {
            if (bytesVal.Kind != JsonKind.Number
                || !long.TryParse(bytesVal.RawNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBytes)
                || parsedBytes < 0)
            {
                Report(context, $"Model entry '{label}' has invalid \"bytes\" value.");
                return false;
            }
            bytes = parsedBytes;
        }

        var aliases = new List<string>();
        if (TryGetProperty(obj, "aliases", out var aliasesVal) && aliasesVal.Kind != JsonKind.Null)
        {
            if (aliasesVal.Kind != JsonKind.Array || aliasesVal.ArrayValue is null)
            {
                Report(context, $"Model entry '{label}' \"aliases\" must be an array of strings.");
                return false;
            }

            foreach (var item in aliasesVal.ArrayValue)
            {
                if (item.Kind != JsonKind.String || string.IsNullOrWhiteSpace(item.StringValue))
                {
                    Report(context, $"Model entry '{label}' contains an invalid alias.");
                    return false;
                }
                aliases.Add(item.StringValue!);
            }
        }

        PriceEntry? price = null;
        if (TryGetProperty(obj, "price", out var priceVal) && priceVal.Kind != JsonKind.Null)
        {
            if (priceVal.Kind != JsonKind.Object || priceVal.ObjectValue is null)
            {
                Report(context, $"Model entry '{label}' \"price\" must be an object.");
                return false;
            }

            foreach (var pair in priceVal.ObjectValue)
            {
                if (!AllowedPriceKeys.Contains(pair.Key))
                {
                    Report(context, $"Unknown member '{pair.Key}' in \"price\" on model entry '{label}'.");
                    return false;
                }
            }

            if (!RequireDecimal(context, label, priceVal.ObjectValue, "input", out var input)) { return false; }
            if (!RequireDecimal(context, label, priceVal.ObjectValue, "output", out var output)) { return false; }
            if (!OptionalDecimal(context, label, priceVal.ObjectValue, "cached", out var cached)) { return false; }
            if (!OptionalDecimal(context, label, priceVal.ObjectValue, "reasoning", out var reasoning)) { return false; }
            price = new PriceEntry(input, output, cached, reasoning);
        }

        entry = new ModelEntry(id, member, provider, family, name, where, abilities, tier, bytes, aliases, price);
        return true;
    }

    private static bool ParseDefaultMap(
        SourceProductionContext context,
        string section,
        JsonValue val,
        HashSet<string> knownIds,
        List<KeyValuePair<string, string>> target)
    {
        if (val.Kind != JsonKind.Object || val.ObjectValue is null)
        {
            Report(context, $"models.json \"{section}\" must be an object.");
            return false;
        }

        foreach (var pair in val.ObjectValue)
        {
            if (!ValidRoles.Contains(pair.Key))
            {
                Report(context, $"Unknown role '{pair.Key}' in models.json {section}.");
                return false;
            }

            if (pair.Value.Kind != JsonKind.String || string.IsNullOrWhiteSpace(pair.Value.StringValue))
            {
                Report(context, $"Role '{pair.Key}' in models.json {section} must name a model id string.");
                return false;
            }

            var modelId = pair.Value.StringValue!;
            if (!knownIds.Contains(modelId))
            {
                Report(context, $"Role '{pair.Key}' in models.json {section} references unknown model id '{modelId}'.");
                return false;
            }

            target.Add(new KeyValuePair<string, string>(pair.Key, modelId));
        }

        return true;
    }

    private static string Emit(
        List<ModelEntry> entries,
        List<KeyValuePair<string, string>> deviceDefaults,
        List<KeyValuePair<string, string>> cloudDefaults)
    {
        var sb = new StringBuilder(16 * 1024);
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Collections.Immutable;");
        sb.AppendLine();
        sb.AppendLine("namespace IntoChat.OS.Modules.AI.Models;");
        sb.AppendLine();
        sb.AppendLine("public static partial class KnownModels");
        sb.AppendLine("{");

        foreach (var entry in entries)
        {
            sb.Append("    public static readonly ModelCard ");
            sb.Append(entry.Member);
            sb.Append(" = new(new(");
            sb.Append(Quote(entry.Id));
            sb.Append("), AiProvider.");
            sb.Append(entry.Provider);
            sb.Append(", ");
            sb.Append(Quote(entry.Family));
            sb.Append(", ");
            sb.Append(Quote(entry.Name));
            sb.Append(", Where.");
            sb.Append(entry.Where);
            sb.Append(", ");
            for (var i = 0; i < entry.Can.Count; i++)
            {
                if (i > 0) { sb.Append(" | "); }
                sb.Append("Abilities.");
                sb.Append(entry.Can[i]);
            }
            sb.Append(", Tier.");
            sb.Append(entry.Tier);
            sb.Append(", ");
            if (entry.Price is null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append("new ModelPrice(");
                sb.Append(FormatDecimal(entry.Price.Input));
                sb.Append(", ");
                sb.Append(FormatDecimal(entry.Price.Output));
                sb.Append(", ");
                sb.Append(entry.Price.Cached.HasValue ? FormatDecimal(entry.Price.Cached.Value) : "null");
                sb.Append(", ");
                sb.Append(entry.Price.Reasoning.HasValue ? FormatDecimal(entry.Price.Reasoning.Value) : "null");
                sb.Append(')');
            }
            sb.Append(", ");
            if (entry.Bytes.HasValue)
            {
                sb.Append(entry.Bytes.Value.ToString(CultureInfo.InvariantCulture));
                sb.Append('L');
            }
            else
            {
                sb.Append("null");
            }
            sb.AppendLine(");");
        }

        sb.AppendLine();
        sb.AppendLine("    public static IReadOnlyList<ModelCard> All { get; } =");
        sb.AppendLine("    [");
        foreach (var entry in entries)
        {
            sb.Append("        ");
            sb.Append(entry.Member);
            sb.AppendLine(",");
        }
        sb.AppendLine("    ];");

        sb.AppendLine();
        sb.AppendLine("    public static RoleMap Defaults { get; } = new(");
        sb.Append("        ImmutableDictionary<Role, ModelId>.Empty");
        foreach (var pair in deviceDefaults)
        {
            sb.AppendLine();
            sb.Append("            .Add(Role.");
            sb.Append(pair.Key);
            sb.Append(", new(");
            sb.Append(Quote(pair.Value));
            sb.Append("))");
        }
        sb.AppendLine(",");
        sb.Append("        ImmutableDictionary<Role, ModelId>.Empty");
        foreach (var pair in cloudDefaults)
        {
            sb.AppendLine();
            sb.Append("            .Add(Role.");
            sb.Append(pair.Key);
            sb.Append(", new(");
            sb.Append(Quote(pair.Value));
            sb.Append("))");
        }
        sb.AppendLine(");");

        sb.AppendLine();
        sb.AppendLine("    public static ModelCard? Find(string? idOrAlias)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (string.IsNullOrWhiteSpace(idOrAlias))");
        sb.AppendLine("        {");
        sb.AppendLine("            return null;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        var key = idOrAlias.Trim();");
        sb.AppendLine("        if (key.StartsWith(\"preset:\", StringComparison.OrdinalIgnoreCase))");
        sb.AppendLine("        {");
        sb.AppendLine("            key = key.Substring(\"preset:\".Length).Trim();");
        sb.AppendLine("            if (key.Length == 0)");
        sb.AppendLine("            {");
        sb.AppendLine("                return null;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        switch (key)");
        sb.AppendLine("        {");
        foreach (var entry in entries)
        {
            sb.Append("            case ");
            sb.Append(Quote(entry.Id));
            sb.Append(": return ");
            sb.Append(entry.Member);
            sb.AppendLine(";");
        }
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return key.ToLowerInvariant() switch");
        sb.AppendLine("        {");
        var emittedLowerKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var keys = new List<string>(1 + entry.Aliases.Count) { entry.Id.ToLowerInvariant() };
            foreach (var alias in entry.Aliases)
            {
                var lower = alias.Trim().ToLowerInvariant();
                if (lower.Length > 0) { keys.Add(lower); }
            }

            var uniqueForEntry = new List<string>();
            foreach (var k in keys)
            {
                if (emittedLowerKeys.Add(k))
                {
                    uniqueForEntry.Add(k);
                }
            }

            if (uniqueForEntry.Count == 0) { continue; }
            sb.Append("            ");
            for (var i = 0; i < uniqueForEntry.Count; i++)
            {
                if (i > 0) { sb.Append(" or "); }
                sb.Append(Quote(uniqueForEntry[i]));
            }
            sb.Append(" => ");
            sb.Append(entry.Member);
            sb.AppendLine(",");
        }
        sb.AppendLine("            _ => null,");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void Report(SourceProductionContext context, string message)
        => context.ReportDiagnostic(Diagnostic.Create(InvalidCatalog, Location.None, message));

    private static bool TryGetProperty(List<KeyValuePair<string, JsonValue>> obj, string name, out JsonValue value)
    {
        for (var i = 0; i < obj.Count; i++)
        {
            if (string.Equals(obj[i].Key, name, StringComparison.Ordinal))
            {
                value = obj[i].Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? TryGetString(List<KeyValuePair<string, JsonValue>> obj, string name)
        => TryGetProperty(obj, name, out var v) && v.Kind == JsonKind.String ? v.StringValue : null;

    private static bool RequireNonEmptyString(
        SourceProductionContext context,
        string label,
        List<KeyValuePair<string, JsonValue>> obj,
        string name,
        out string value)
    {
        if (!TryGetProperty(obj, name, out var v) || v.Kind != JsonKind.String || string.IsNullOrWhiteSpace(v.StringValue))
        {
            Report(context, $"Model entry '{label}' is missing required string member '{name}'.");
            value = string.Empty;
            return false;
        }
        value = v.StringValue!;
        return true;
    }

    private static bool RequireEnum(
        SourceProductionContext context,
        string label,
        List<KeyValuePair<string, JsonValue>> obj,
        string name,
        HashSet<string> allowed,
        out string value)
    {
        if (!RequireNonEmptyString(context, label, obj, name, out value)) { return false; }
        if (!allowed.Contains(value))
        {
            Report(context, $"Model entry '{label}' has invalid '{name}' value '{value}'.");
            return false;
        }
        return true;
    }

    private static bool RequireDecimal(
        SourceProductionContext context,
        string label,
        List<KeyValuePair<string, JsonValue>> obj,
        string name,
        out decimal value)
    {
        if (!TryGetProperty(obj, name, out var v)
            || v.Kind != JsonKind.Number
            || !decimal.TryParse(v.RawNumber, NumberStyles.Number, CultureInfo.InvariantCulture, out value)
            || value < 0m)
        {
            Report(context, $"Model entry '{label}' has missing or invalid price.{name}.");
            value = 0m;
            return false;
        }
        return true;
    }

    private static bool OptionalDecimal(
        SourceProductionContext context,
        string label,
        List<KeyValuePair<string, JsonValue>> obj,
        string name,
        out decimal? value)
    {
        if (!TryGetProperty(obj, name, out var v) || v.Kind == JsonKind.Null)
        {
            value = null;
            return true;
        }
        if (v.Kind != JsonKind.Number
            || !decimal.TryParse(v.RawNumber, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0m)
        {
            Report(context, $"Model entry '{label}' has invalid price.{name}.");
            value = null;
            return false;
        }
        value = parsed;
        return true;
    }

    private static bool IsValidIdentifier(string s)
    {
        if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_')) { return false; }
        for (var i = 1; i < s.Length; i++)
        {
            if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_')) { return false; }
        }
        return true;
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string FormatDecimal(decimal value)
        => value.ToString(CultureInfo.InvariantCulture) + "m";

    private sealed class PriceEntry
    {
        public PriceEntry(decimal input, decimal output, decimal? cached, decimal? reasoning)
        {
            Input = input;
            Output = output;
            Cached = cached;
            Reasoning = reasoning;
        }

        public decimal Input { get; }
        public decimal Output { get; }
        public decimal? Cached { get; }
        public decimal? Reasoning { get; }
    }

    private sealed class ModelEntry
    {
        public ModelEntry(
            string id,
            string member,
            string provider,
            string family,
            string name,
            string where,
            List<string> can,
            string tier,
            long? bytes,
            List<string> aliases,
            PriceEntry? price)
        {
            Id = id;
            Member = member;
            Provider = provider;
            Family = family;
            Name = name;
            Where = where;
            Can = can;
            Tier = tier;
            Bytes = bytes;
            Aliases = aliases;
            Price = price;
        }

        public string Id { get; }
        public string Member { get; }
        public string Provider { get; }
        public string Family { get; }
        public string Name { get; }
        public string Where { get; }
        public List<string> Can { get; }
        public string Tier { get; }
        public long? Bytes { get; }
        public List<string> Aliases { get; }
        public PriceEntry? Price { get; }
    }

    private enum JsonKind { Null, Bool, Number, String, Array, Object }

    private readonly struct JsonValue
    {
        public JsonKind Kind { get; }
        public string? StringValue { get; }
        public string? RawNumber { get; }
        public bool BoolValue { get; }
        public List<JsonValue>? ArrayValue { get; }
        public List<KeyValuePair<string, JsonValue>>? ObjectValue { get; }

        private JsonValue(
            JsonKind kind,
            string? str = null,
            string? num = null,
            bool b = false,
            List<JsonValue>? arr = null,
            List<KeyValuePair<string, JsonValue>>? obj = null)
        {
            Kind = kind;
            StringValue = str;
            RawNumber = num;
            BoolValue = b;
            ArrayValue = arr;
            ObjectValue = obj;
        }

        public static JsonValue Null() => new(JsonKind.Null);
        public static JsonValue Bool(bool value) => new(JsonKind.Bool, b: value);
        public static JsonValue Number(string raw) => new(JsonKind.Number, num: raw);
        public static JsonValue String(string value) => new(JsonKind.String, str: value);
        public static JsonValue Array(List<JsonValue> items) => new(JsonKind.Array, arr: items);
        public static JsonValue Object(List<KeyValuePair<string, JsonValue>> pairs) => new(JsonKind.Object, obj: pairs);
    }

    private sealed class JsonParser
    {
        private readonly string _text;
        private int _pos;

        private JsonParser(string text) => _text = text;

        public static JsonValue Parse(string text)
        {
            var p = new JsonParser(text);
            p.SkipWhitespace();
            var val = p.ReadValue();
            p.SkipWhitespace();
            if (p._pos != p._text.Length)
            {
                throw new FormatException($"Unexpected trailing character '{p._text[p._pos]}' at position {p._pos}.");
            }
            return val;
        }

        private JsonValue ReadValue()
        {
            SkipWhitespace();
            if (_pos >= _text.Length)
            {
                throw new FormatException("Unexpected end of JSON input.");
            }

            return _text[_pos] switch
            {
                '{' => ReadObject(),
                '[' => ReadArray(),
                '"' => JsonValue.String(ReadString()),
                't' => ReadLiteral("true", JsonValue.Bool(true)),
                'f' => ReadLiteral("false", JsonValue.Bool(false)),
                'n' => ReadLiteral("null", JsonValue.Null()),
                '-' or (>= '0' and <= '9') => ReadNumber(),
                _ => throw new FormatException($"Unexpected character '{_text[_pos]}' at position {_pos}."),
            };
        }

        private JsonValue ReadObject()
        {
            _pos++; // '{'
            var pairs = new List<KeyValuePair<string, JsonValue>>();
            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == '}')
            {
                _pos++;
                return JsonValue.Object(pairs);
            }

            while (true)
            {
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] != '"')
                {
                    throw new FormatException($"Expected string property name at position {_pos}.");
                }
                var key = ReadString();
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] != ':')
                {
                    throw new FormatException($"Expected ':' after property '{key}' at position {_pos}.");
                }
                _pos++;
                var val = ReadValue();
                pairs.Add(new KeyValuePair<string, JsonValue>(key, val));
                SkipWhitespace();
                if (_pos >= _text.Length)
                {
                    throw new FormatException("Unterminated JSON object.");
                }
                if (_text[_pos] == '}')
                {
                    _pos++;
                    return JsonValue.Object(pairs);
                }
                if (_text[_pos] != ',')
                {
                    throw new FormatException($"Expected ',' or '}}' at position {_pos}.");
                }
                _pos++;
            }
        }

        private JsonValue ReadArray()
        {
            _pos++; // '['
            var items = new List<JsonValue>();
            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == ']')
            {
                _pos++;
                return JsonValue.Array(items);
            }

            while (true)
            {
                items.Add(ReadValue());
                SkipWhitespace();
                if (_pos >= _text.Length)
                {
                    throw new FormatException("Unterminated JSON array.");
                }
                if (_text[_pos] == ']')
                {
                    _pos++;
                    return JsonValue.Array(items);
                }
                if (_text[_pos] != ',')
                {
                    throw new FormatException($"Expected ',' or ']' at position {_pos}.");
                }
                _pos++;
            }
        }

        private string ReadString()
        {
            _pos++; // '"'
            var sb = new StringBuilder();
            while (_pos < _text.Length)
            {
                var c = _text[_pos++];
                if (c == '"') { return sb.ToString(); }
                if (c == '\\')
                {
                    if (_pos >= _text.Length) { throw new FormatException("Unterminated escape sequence."); }
                    var esc = _text[_pos++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_pos + 4 > _text.Length) { throw new FormatException("Truncated \\u escape."); }
                            var hex = _text.Substring(_pos, 4);
                            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            {
                                throw new FormatException($"Invalid \\u{hex} escape.");
                            }
                            sb.Append((char)code);
                            _pos += 4;
                            break;
                        default:
                            throw new FormatException($"Invalid escape '\\{esc}'.");
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            throw new FormatException("Unterminated JSON string.");
        }

        private JsonValue ReadNumber()
        {
            var start = _pos;
            if (_text[_pos] == '-') { _pos++; }
            while (_pos < _text.Length && char.IsDigit(_text[_pos])) { _pos++; }
            if (_pos < _text.Length && _text[_pos] == '.')
            {
                _pos++;
                while (_pos < _text.Length && char.IsDigit(_text[_pos])) { _pos++; }
            }
            if (_pos < _text.Length && (_text[_pos] == 'e' || _text[_pos] == 'E'))
            {
                _pos++;
                if (_pos < _text.Length && (_text[_pos] == '+' || _text[_pos] == '-')) { _pos++; }
                while (_pos < _text.Length && char.IsDigit(_text[_pos])) { _pos++; }
            }
            return JsonValue.Number(_text.Substring(start, _pos - start));
        }

        private JsonValue ReadLiteral(string literal, JsonValue result)
        {
            if (_pos + literal.Length > _text.Length
                || string.Compare(_text, _pos, literal, 0, literal.Length, StringComparison.Ordinal) != 0)
            {
                throw new FormatException($"Expected '{literal}' at position {_pos}.");
            }
            _pos += literal.Length;
            return result;
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
            {
                _pos++;
            }
        }
    }
}
