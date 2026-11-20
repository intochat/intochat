using System.Text.Json;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using IntoChat.OS.Host.Surface;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Agents.Tools;

internal sealed class ContractTool : AIFunction
{
    private static readonly string[] RelativeKeyPrefixes =
    [
        "codeblocks/", "connection/", "diagnostics/", "ino/", "apps/", "context/", "runs/", "flows/", "roslyn/",
    ];

    private readonly Func<string, string, string, string, CancellationToken, Task<object?>> _invoke;
    private readonly JsonElement _jsonSchema;

    public ContractTool(ToolSchema schema, Func<string, string, string, string, CancellationToken, Task<JsonElement?>> call)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(call);
        Schema = schema;
        _jsonSchema = ParseSchema(schema.ParametersJson);
        _invoke = async (contract, key, method, arguments, ct) =>
            (object?)await call(contract, key, method, arguments, ct).ConfigureAwait(false) ?? new { ok = true };
    }

    internal ContractTool(ToolSchema schema, EdgeAuthority? edge, OsNeuronSurface surface, string conversation)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(surface);
        Schema = schema;
        _jsonSchema = ParseSchema(schema.ParametersJson);
        _invoke = async (contract, key, method, arguments, ct) =>
            await AgentTools.Marked(
                edge,
                conversation,
                async () => (object?)await surface.Call(contract, key, method, arguments, ct).ConfigureAwait(false) ?? new { ok = true }).ConfigureAwait(false);
    }

    public ToolSchema Schema { get; }

    public override string Name => Schema.Name;

    public override string Description => Schema.Description;

    public override JsonElement JsonSchema => _jsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        string? rawKey = null;

        if (arguments is not null)
        {
            foreach (var entry in arguments)
            {
                if (Schema.DefaultKey is null && string.Equals(entry.Key, "key", StringComparison.OrdinalIgnoreCase))
                {
                    rawKey = ExtractString(entry.Value);
                    continue;
                }
                payload[entry.Key] = entry.Value;
            }
        }

        string key;
        if (Schema.DefaultKey is { } defaultKey)
        {
            key = defaultKey.StartsWith("{brain}/", StringComparison.OrdinalIgnoreCase)
                ? defaultKey
                : "{brain}/" + defaultKey;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                var hint = string.IsNullOrWhiteSpace(Schema.KeyPattern) ? string.Empty : $" (e.g. {Schema.KeyPattern})";
                return new { isError = true, message = $"Tool '{Schema.Name}' requires a 'key' argument{hint}." };
            }
            key = NormalizeKey(rawKey.Trim(), Schema.KeyPattern);
        }

        var argsJson = JsonSerializer.Serialize(payload, RuntimeProtocol.Json);
        return await _invoke(Schema.Contract, key, Schema.Method, argsJson, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeKey(string key, string? keyPattern)
    {
        if (key.StartsWith("{brain}/", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("{brainScope}/", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith('@'))
        {
            return key;
        }

        var trimmed = key.TrimStart('/');
        if (!string.IsNullOrWhiteSpace(keyPattern))
        {
            var slash = keyPattern.IndexOf('/');
            var patternRoot = slash > 0 ? keyPattern[..(slash + 1)] : keyPattern;
            if (trimmed.StartsWith(patternRoot, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, keyPattern, StringComparison.OrdinalIgnoreCase))
            {
                return "{brain}/" + trimmed;
            }
        }

        if (!trimmed.Contains('/', StringComparison.Ordinal)
            || RelativeKeyPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return "{brain}/" + trimmed;
        }

        return trimmed;
    }

    private static string? ExtractString(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => value?.ToString(),
    };

    private static JsonElement ParseSchema(string parametersJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(parametersJson) ? """{"type":"object","properties":{}}""" : parametersJson);
        return document.RootElement.Clone();
    }
}
