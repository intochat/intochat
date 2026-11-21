using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Access;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Surface;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Agents.Tools;

internal static class ToolSourceTools
{
    public const string ToolSourceContract = "IntoChat.OS.Modules.Tools.IToolSource";
    private const int MaxToolNameLength = 64;
    private const int MaxDescriptionLength = 1_000;

    public static Task<IReadOnlyList<AIFunction>> For(
        OsNeuronSurface surface,
        IReadOnlyList<Type> contracts,
        EdgeAuthority? edge,
        string conversation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(contracts);
        return For(
            surface.List,
            surface.Call,
            contracts,
            edge,
            conversation,
            ct);
    }

    internal static async Task<IReadOnlyList<AIFunction>> For(
        Func<string, CancellationToken, Task<IReadOnlyList<NeuronInstance>>> list,
        Func<string, string, string, string, CancellationToken, Task<JsonElement?>> call,
        IReadOnlyList<Type> contracts,
        EdgeAuthority? edge,
        string conversation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(contracts);

        var tools = new List<AIFunction>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var contract in contracts)
        {
            if (contract is null || !IsToolSource(contract) || string.IsNullOrWhiteSpace(contract.FullName))
            {
                continue;
            }

            IReadOnlyList<NeuronInstance> instances;
            try
            {
                instances = await list(contract.FullName, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                OsConsoleLog.For<ToolSourceFunction>().LogSwallowed(error, $"listing tool source instances for {contract.FullName}");
                continue;
            }

            foreach (var instance in instances)
            {
                JsonElement? rawSet;
                try
                {
                    rawSet = await call(contract.FullName, instance.Key, "Tools", "{}", ct).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    OsConsoleLog.For<ToolSourceFunction>().LogSwallowed(error, $"reading tools from source {instance.Key}");
                    continue;
                }

                if (rawSet is not { ValueKind: JsonValueKind.Object } setObj
                    || !TryGetProperty(setObj, "tools", "Tools", out var toolsEl)
                    || toolsEl.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var entry in toolsEl.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var rawName = GetString(entry, "name", "Name");
                    if (string.IsNullOrWhiteSpace(rawName))
                    {
                        continue;
                    }

                    var description = ClipDescription(GetString(entry, "description", "Description") ?? string.Empty);
                    var schemaJson = GetString(entry, "inputSchema", "InputSchema");
                    var toolName = NameOf(instance.Key, rawName);
                    if (!seenNames.Add(toolName))
                    {
                        continue;
                    }

                    tools.Add(new ToolSourceFunction(
                        contract.FullName,
                        instance.Key,
                        rawName,
                        toolName,
                        description,
                        ParseSchema(schemaJson),
                        edge,
                        conversation,
                        call));
                }
            }
        }

        return tools;
    }

    internal static string NameOf(string sourceKey, string tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);

        var server = LastSegment(sourceKey);
        var serverSnake = ToSnakeCase(server);
        var toolSnake = ToSnakeCase(tool);
        var combined = serverSnake.Length == 0
            ? toolSnake
            : toolSnake.Length == 0
                ? serverSnake
                : serverSnake + "_" + toolSnake;

        if (combined.Length <= MaxToolNameLength)
        {
            return combined;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        var suffix = Convert.ToHexStringLower(hash.AsSpan(0, 3));
        return combined[..57] + "_" + suffix;
    }

    internal static string LastSegment(string key)
    {
        var trimmed = key.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static bool IsToolSource(Type contract)
        => string.Equals(contract.FullName, ToolSourceContract, StringComparison.Ordinal)
           || contract.GetInterfaces().Any(static i => string.Equals(i.FullName, ToolSourceContract, StringComparison.Ordinal));

    private static string ToSnakeCase(string raw)
    {
        var sb = new StringBuilder(raw.Length + 4);
        var prevUnderscore = true;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (char.IsAsciiLetterUpper(c))
                {
                    var prevIsLowerOrDigit = i > 0 && (char.IsAsciiLetterLower(raw[i - 1]) || char.IsAsciiDigit(raw[i - 1]));
                    var nextIsLower = i + 1 < raw.Length && char.IsAsciiLetterLower(raw[i + 1]);
                    var prevIsUpper = i > 0 && char.IsAsciiLetterUpper(raw[i - 1]);
                    if (!prevUnderscore && (prevIsLowerOrDigit || (prevIsUpper && nextIsLower)))
                    {
                        sb.Append('_');
                    }

                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(c);
                }

                prevUnderscore = false;
            }
            else if (!prevUnderscore)
            {
                sb.Append('_');
                prevUnderscore = true;
            }
        }

        while (sb.Length > 0 && sb[^1] == '_')
        {
            sb.Length--;
        }

        return sb.ToString();
    }

    private static string ClipDescription(string description)
    {
        var trimmed = description.Trim();
        return trimmed.Length <= MaxDescriptionLength ? trimmed : trimmed[..MaxDescriptionLength];
    }

    private static JsonElement ParseSchema(string? schemaJson)
    {
        if (!string.IsNullOrWhiteSpace(schemaJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(schemaJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return doc.RootElement.Clone();
                }
            }
            catch (JsonException error)
            {
                OsConsoleLog.For<ToolSourceFunction>().LogSwallowed(error, "parsing tool source input schema; falling back to empty object schema");
            }
        }

        using var fallback = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return fallback.RootElement.Clone();
    }

    private static bool TryGetProperty(JsonElement obj, string camel, string pascal, out JsonElement value)
        => obj.TryGetProperty(camel, out value) || obj.TryGetProperty(pascal, out value);

    private static string? GetString(JsonElement obj, string camel, string pascal)
        => TryGetProperty(obj, camel, pascal, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal sealed class ToolSourceFunction(
        string contractName,
        string sourceKey,
        string rawToolName,
        string functionName,
        string description,
        JsonElement jsonSchema,
        EdgeAuthority? edge,
        string conversation,
        Func<string, string, string, string, CancellationToken, Task<JsonElement?>> call) : AIFunction
    {
        public string ContractName => contractName;

        public override string Name => functionName;

        public override string Description => description;

        public override JsonElement JsonSchema => jsonSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (arguments is not null)
            {
                foreach (var entry in arguments)
                {
                    payload[entry.Key] = entry.Value;
                }
            }

            var argsJson = ContractTool.SerializeArguments(payload);
            var runPayload = SerializeRunPayload(rawToolName, argsJson);

            return await AgentTools.Marked(edge, conversation, async () =>
            {
                var raw = await call(contractName, sourceKey, "Run", runPayload, cancellationToken).ConfigureAwait(false);
                if (raw is not { ValueKind: JsonValueKind.Object } obj)
                {
                    return UntrustedText.Fence(SourceUri(sourceKey), string.Empty);
                }

                var isError = TryGetProperty(obj, "isError", "IsError", out var errEl) && errEl.ValueKind == JsonValueKind.True;
                var text = GetString(obj, "text", "Text") ?? string.Empty;
                if (isError)
                {
                    return new { isError = true, message = text };
                }

                return UntrustedText.Fence(SourceUri(sourceKey), text);
            }).ConfigureAwait(false);
        }

        private static string SerializeRunPayload(string tool, string argumentsJson)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("tool", tool);
                writer.WriteString("arguments", argumentsJson);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        private static Uri SourceUri(string key)
        {
            var segment = LastSegment(key);
            if (Uri.TryCreate("mcp://" + segment, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri;
            }

            var safe = new string([.. segment.Select(static c => char.IsAsciiLetterOrDigit(c) || c == '-' ? char.ToLowerInvariant(c) : '-')]).Trim('-');
            if (safe.Length == 0)
            {
                safe = "mcp";
            }

            return new Uri("mcp://" + safe);
        }
    }
}
