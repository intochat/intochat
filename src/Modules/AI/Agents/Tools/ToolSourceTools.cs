using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Contracts;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Surface;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Agents.Tools;

internal static class ToolSourceTools
{
    public const string ToolSourceContract = "IntoChat.OS.Modules.Tools.IToolSource";
    private const int MaxToolNameLength = 64;
    private const int MaxDescriptionLength = 1_000;

    public static Task<IReadOnlyList<AIFunction>> For(
        NeuronSurface surface,
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

    /// <summary>
    /// Reads <c>Tools()</c> and builds <see cref="AIFunction"/> wrappers ONLY for the specified canonical tool-source keys,
    /// without exposing any contract methods or discovery meta-tools.
    /// </summary>
    public static async Task<IReadOnlyList<AIFunction>> ForKeys(
        NeuronSurface surface,
        IReadOnlyList<string> sourceKeys,
        EdgeAuthority? edge,
        string conversation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(sourceKeys);

        if (sourceKeys.Count == 0)
        {
            return [];
        }

        var hostedContracts = await surface.Contracts(ct).ConfigureAwait(false);
        var toolSourceTypes = new List<Type>();
        var byConnectorAlias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in hostedContracts)
        {
            Type type;
            try
            {
                type = surface.FindContract(name);
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                OsConsoleLog.For<ToolSourceFunction>().LogSwallowed(error, $"resolving hosted contract {name}");
                continue;
            }

            if (!IsToolSource(type) || string.IsNullOrWhiteSpace(type.FullName))
            {
                continue;
            }

            toolSourceTypes.Add(type);
            if (type.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type is { Length: > 0 } defaultType)
            {
                byConnectorAlias.TryAdd(defaultType, type.FullName);
            }
            if (type.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias is { Length: > 0 } alias)
            {
                byConnectorAlias.TryAdd(alias, type.FullName);
            }
        }

        if (toolSourceTypes.Count == 0)
        {
            return [];
        }

        var ownerBrain = ResolveOwnerBrain(edge, conversation);
        var resolvedSources = new List<(string ContractName, string Key)>(sourceKeys.Count);
        Dictionary<string, string>? fallbackKeyToContract = null;

        foreach (var rawKey in sourceKeys)
        {
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                continue;
            }

            var key = rawKey.Trim();
            if (!IsSameBrainSource(key, ownerBrain))
            {
                OsConsoleLog.For<ToolSourceFunction>().LogWarning(
                    "Denied cross-brain tool source '{SourceKey}' for conversation '{Conversation}' (owner brain '{OwnerBrain}').",
                    key,
                    conversation,
                    ownerBrain);
                continue;
            }

            if (ConnectionKeys.TryParse(key, out var parsed)
                && byConnectorAlias.TryGetValue(parsed.ConnectorId, out var contractName))
            {
                resolvedSources.Add((contractName, key));
                continue;
            }

            // Non-connection IToolSource keys (e.g., custom test sources): resolve exact key from hosted tool sources.
            fallbackKeyToContract ??= await IndexToolSourceKeysAsync(surface, toolSourceTypes, ct).ConfigureAwait(false);
            if (fallbackKeyToContract.TryGetValue(key, out var matchedContract))
            {
                resolvedSources.Add((matchedContract, key));
            }
            else if (toolSourceTypes.Count == 1 && toolSourceTypes[0].FullName is { Length: > 0 } onlyContract)
            {
                resolvedSources.Add((onlyContract, key));
            }
        }

        if (resolvedSources.Count == 0)
        {
            return [];
        }

        return await ForKeys(surface.Call, resolvedSources, edge, conversation, ct).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<AIFunction>> ForKeys(
        Func<string, string, string, string, CancellationToken, Task<JsonElement?>> call,
        IReadOnlyList<(string ContractName, string Key)> sources,
        EdgeAuthority? edge,
        string conversation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(sources);

        var ownerBrain = ResolveOwnerBrain(edge, conversation);
        var tools = new List<AIFunction>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (contractName, rawKey) in sources)
        {
            if (string.IsNullOrWhiteSpace(contractName) || string.IsNullOrWhiteSpace(rawKey))
            {
                continue;
            }

            var key = rawKey.Trim();
            if (!IsSameBrainSource(key, ownerBrain))
            {
                OsConsoleLog.For<ToolSourceFunction>().LogWarning(
                    "Denied cross-brain tool source '{SourceKey}' for conversation '{Conversation}' (owner brain '{OwnerBrain}').",
                    key,
                    conversation,
                    ownerBrain);
                continue;
            }

            if (!seenKeys.Add(key))
            {
                continue;
            }

            await AppendToolsFromSourceAsync(call, contractName, key, edge, conversation, seenNames, tools, ct).ConfigureAwait(false);
        }

        return tools;
    }

    private static string ResolveOwnerBrain(EdgeAuthority? edge, string? conversation)
    {
        if (!string.IsNullOrWhiteSpace(conversation) && conversation.Contains('/', StringComparison.Ordinal))
        {
            var fromConversation = DigitalBrain.Brains.BrainKey.BrainOf(conversation);
            if (!string.IsNullOrWhiteSpace(fromConversation))
            {
                return fromConversation;
            }
        }

        if (edge?.Current?.Brain.Value is { Length: > 0 } callerBrain)
        {
            return callerBrain;
        }

        return string.Empty;
    }

    internal static bool IsSameBrainSource(string key, string ownerBrain)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(ownerBrain))
        {
            return false;
        }

        return key.StartsWith(ownerBrain + "/", StringComparison.Ordinal);
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
                await AppendToolsFromSourceAsync(call, contract.FullName, instance.Key, edge, conversation, seenNames, tools, ct).ConfigureAwait(false);
            }
        }

        return tools;
    }

    private static async Task<Dictionary<string, string>> IndexToolSourceKeysAsync(
        NeuronSurface surface,
        List<Type> toolSourceTypes,
        CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in toolSourceTypes)
        {
            if (string.IsNullOrWhiteSpace(type.FullName)) { continue; }
            try
            {
                var instances = await surface.List(type.FullName, ct).ConfigureAwait(false);
                foreach (var instance in instances)
                {
                    map.TryAdd(instance.Key, type.FullName);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                OsConsoleLog.For<ToolSourceFunction>().LogSwallowed(error, $"indexing tool source keys for {type.FullName}");
            }
        }
        return map;
    }

    private static async Task AppendToolsFromSourceAsync(
        Func<string, string, string, string, CancellationToken, Task<JsonElement?>> call,
        string contractName,
        string sourceKey,
        EdgeAuthority? edge,
        string conversation,
        HashSet<string> seenNames,
        List<AIFunction> tools,
        CancellationToken ct)
    {
        JsonElement? rawSet;
        try
        {
            rawSet = await call(contractName, sourceKey, "Tools", "{}", ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            OsConsoleLog.For<ToolSourceFunction>().LogSwallowed(error, $"reading tools from source {sourceKey}");
            return;
        }

        if (rawSet is not { ValueKind: JsonValueKind.Object } setObj
            || !TryGetProperty(setObj, "tools", "Tools", out var toolsEl)
            || toolsEl.ValueKind != JsonValueKind.Array)
        {
            return;
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
            var toolName = NameOf(sourceKey, rawName);
            if (!seenNames.Add(toolName))
            {
                continue;
            }

            tools.Add(new ToolSourceFunction(
                contractName,
                sourceKey,
                rawName,
                toolName,
                description,
                ParseSchema(schemaJson),
                edge,
                conversation,
                call));
        }
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

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "Neuron contract interfaces passed from AgentDefinition.Contracts are compile-time interfaces whose inheritance hierarchy is preserved by the host.")]
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
