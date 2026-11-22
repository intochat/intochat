using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Units;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Agents.Tools;

// One typed AIFunction per [On] operation declared on an installed app component.
// Invoking the tool dispatches the operation's signal to the app's default screen neuron
// under the agent caller stamp and returns the screen's status/view summary to the model.
internal sealed class OperationTool : AIFunction
{
    private const int MaxToolNameLength = 64;
    private const int MaxSummaryLength = 2_000;

    private static readonly HashSet<string> TextProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "message",
        "text",
        "body",
        "title",
        "label",
        "value",
        "subtitle",
        "description",
        "content",
    };

    private readonly ComponentId _componentId;
    private readonly OperationSpec _operation;
    private readonly EdgeAuthority? _edge;
    private readonly INeuronProxies _proxies;
    private readonly string _conversation;
    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _jsonSchema;

    internal OperationTool(
        ComponentId componentId,
        OperationSpec operation,
        string? componentTitle,
        EdgeAuthority? edge,
        INeuronProxies proxies,
        string conversation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(proxies);
        _componentId = componentId;
        _operation = operation;
        _edge = edge;
        _proxies = proxies;
        _conversation = conversation;
        _name = BuildToolName(componentId.Value, operation.Name);
        _description = string.IsNullOrWhiteSpace(operation.Description)
            ? $"Run {operation.Name} on {componentTitle ?? componentId.Value}."
            : operation.Description.Trim();
        _jsonSchema = ParseSchema(operation.ParametersJson);
    }

    public ComponentId ComponentId => _componentId;

    public OperationSpec Operation => _operation;

    public override string Name => _name;

    public override string Description => _description;

    public override JsonElement JsonSchema => _jsonSchema;

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

        return await AgentTools.Marked(_edge, _conversation, async () =>
        {
            var signalAlias = ResolveSignalAlias(_operation);
            var signalPayload = BuildSignalPayload(_operation, argsJson);

            var brain = _edge!.Current!.Brain;
            var screenId = new NeuronId(brain, _componentId.Value, "default");
            var screen = _proxies.Get<IScreen>(screenId);

            await screen.Dispatch(new DigitalBrain.UI.Command(".", signalAlias, signalPayload)).ConfigureAwait(false);
            var view = await screen.Read().ConfigureAwait(false);
            var summary = SummarizeView(view);
            if (string.IsNullOrWhiteSpace(summary))
            {
                return new { ok = true };
            }
            return new { ok = true, message = summary };
        }).ConfigureAwait(false);
    }

    internal static string BuildToolName(string componentId, string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        var rawApp = componentId.StartsWith("app.", StringComparison.Ordinal)
            ? componentId["app.".Length..]
            : componentId;
        var appSnake = ToSnakeCase(rawApp);
        var opSnake = ToSnakeCase(operationName);
        var combined = appSnake.Length == 0
            ? opSnake
            : opSnake.Length == 0
                ? appSnake
                : appSnake + "_" + opSnake;

        if (combined.Length <= MaxToolNameLength)
        {
            return combined;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        var suffix = Convert.ToHexStringLower(hash.AsSpan(0, 3));
        return combined[..57] + "_" + suffix;
    }

    private static string ResolveSignalAlias(OperationSpec operation)
    {
        if (!string.IsNullOrWhiteSpace(operation.SignalAlias))
        {
            return operation.SignalAlias;
        }

        if (operation.Input is not null)
        {
            return operation.Input.GetCustomAttribute<SignalAttribute>(inherit: false)?.Alias ?? operation.Input.Name;
        }

        return operation.Name;
    }

    private static JsonElement BuildSignalPayload(OperationSpec operation, string argsJson)
    {
        if (operation.Input is not null)
        {
            try
            {
                var signal = RuntimeProtocol.Deserialize(Encoding.UTF8.GetBytes(argsJson), operation.Input)
                    ?? throw new ArgumentException($"Arguments for '{operation.Name}' could not be deserialized.");
                return RuntimeProtocol.SerializeToElement(signal, operation.Input);
            }
            catch (JsonException error)
            {
                throw new ArgumentException($"Invalid arguments for '{operation.Name}': {error.Message}", error);
            }
        }

        using var document = JsonDocument.Parse(argsJson);
        return document.RootElement.Clone();
    }

    private static string? SummarizeView(PublishedView? view)
    {
        if (view is null)
        {
            return null;
        }

        if (FindBannerMessage(view.View) is { Length: > 0 } bannerMessage)
        {
            return Clip(bannerMessage);
        }

        var parts = new List<string>();
        CollectText(view.View, parts);
        if (parts.Count == 0)
        {
            return null;
        }

        return Clip(string.Join(" | ", parts));
    }

    private static string? FindBannerMessage(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var isStatusBanner = element.TryGetProperty("key", out var keyProp)
                    && keyProp.ValueKind == JsonValueKind.String
                    && string.Equals(keyProp.GetString(), "banner:status", StringComparison.OrdinalIgnoreCase);
                var isBanner = isStatusBanner
                    || (element.TryGetProperty("kind", out var kindProp)
                        && kindProp.ValueKind == JsonValueKind.String
                        && string.Equals(kindProp.GetString(), "banner", StringComparison.OrdinalIgnoreCase));

                if (isBanner)
                {
                    foreach (var propName in new[] { "message", "body", "text", "description" })
                    {
                        if (element.TryGetProperty(propName, out var msgProp)
                            && msgProp.ValueKind == JsonValueKind.String
                            && msgProp.GetString() is { Length: > 0 } msg)
                        {
                            return msg;
                        }
                    }
                }

                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        && FindBannerMessage(prop.Value) is { Length: > 0 } found)
                    {
                        return found;
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindBannerMessage(item) is { Length: > 0 } found)
                    {
                        return found;
                    }
                }
                break;
        }

        return null;
    }

    private static void CollectText(JsonElement element, List<string> parts)
    {
        if (parts.Count >= 24)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String && TextProperties.Contains(prop.Name))
                    {
                        var text = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            parts.Add(text.Trim());
                        }
                    }
                    else if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        CollectText(prop.Value, parts);
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectText(item, parts);
                }
                break;
        }
    }

    private static string Clip(string text)
        => text.Length <= MaxSummaryLength ? text : text[..MaxSummaryLength];

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

    private static JsonElement ParseSchema(string? parametersJson)
    {
        if (!string.IsNullOrWhiteSpace(parametersJson))
        {
            try
            {
                using var document = JsonDocument.Parse(parametersJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
            }
        }

        using var fallback = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return fallback.RootElement.Clone();
    }
}
