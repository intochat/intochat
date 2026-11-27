using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace IntoChat.OS.Modules.Mcp;

// Owns one cached MCP HTTP streaming session: SSRF endpoint validation, header assembly, retry-once recovery,
// tool descriptor mapping, AllowedTools enforcement, 128 KB result truncation, and client disposal.
internal sealed class McpClientSession(HttpMessageHandler? httpHandler = null) : IAsyncDisposable
{
    internal const int MaxText = 128 * 1024;
    private static readonly TimeSpan CallBudget = TimeSpan.FromMinutes(5);
    // The SDK's source-generated contracts, so the wire reads below stay trim- and AOT-safe.
    private static readonly JsonTypeInfo<ModelContextProtocol.Protocol.CallToolResult> CallResultJson =
        (JsonTypeInfo<ModelContextProtocol.Protocol.CallToolResult>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ModelContextProtocol.Protocol.CallToolResult));
    private static readonly JsonTypeInfo<ModelContextProtocol.Protocol.Tool> ToolJson =
        (JsonTypeInfo<ModelContextProtocol.Protocol.Tool>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ModelContextProtocol.Protocol.Tool));

    private McpClient? _client;
    private Uri? _endpoint;

    public static Uri ValidateEndpoint(string url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var endpoint)
            || !IsAllowedEndpoint(endpoint))
        {
            throw new ArgumentException("An MCP server URL is an absolute http(s) URL without credentials.", nameof(url));
        }

        return endpoint;
    }

    public static bool IsAllowedEndpoint(Uri endpoint)
    {
        if (endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || string.IsNullOrWhiteSpace(endpoint.Host))
        {
            return false;
        }

        if (IPAddress.TryParse(endpoint.DnsSafeHost, out var ip) && !HttpHosts.IsPublicOrLoopback(ip))
        {
            return false;
        }

        return true;
    }

    public static Dictionary<string, string>? BuildHeaders(string? headerName, string? headerPrefix, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmedName = string.IsNullOrWhiteSpace(headerName) ? "Authorization" : headerName.Trim();
        if (trimmedName.Any(static c => char.IsWhiteSpace(c) || c == ':' || c == '\r' || c == '\n'))
        {
            throw new ArgumentException($"Header name '{headerName}' is not a valid HTTP header name.", nameof(headerName));
        }

        var prefix = headerPrefix ?? (string.Equals(trimmedName, "Authorization", StringComparison.OrdinalIgnoreCase) ? "Bearer " : string.Empty);
        var trimmedToken = token.Trim();
        var value = !string.IsNullOrEmpty(prefix) && !trimmedToken.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? prefix + trimmedToken
            : trimmedToken;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [trimmedName] = value,
        };
    }

    public static ToolDescriptor[] FilterAllowedTools(ToolDescriptor[] tools, string[]? allowedTools)
    {
        if (allowedTools is null)
        {
            return [.. tools];
        }

        var set = new HashSet<string>(allowedTools, StringComparer.Ordinal);
        return [.. tools.Where(t => set.Contains(t.Name))];
    }

    public static bool IsToolAllowed(string tool, string[]? allowedTools)
    {
        if (allowedTools is null)
        {
            return true;
        }

        var trimmed = tool.Trim();
        return allowedTools.Any(a => string.Equals(a, trimmed, StringComparison.Ordinal));
    }

    public async Task<McpClient> CreateClientAsync(Uri endpoint, Dictionary<string, string>? headers, CancellationToken ct)
    {
        if (!IsAllowedEndpoint(endpoint))
        {
            throw new ArgumentException("An MCP server URL is an absolute http(s) URL without credentials.", nameof(endpoint));
        }

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            AdditionalHeaders = headers,
        };

        var transport = httpHandler is not null
            ? new HttpClientTransport(transportOptions, new HttpClient(httpHandler, disposeHandler: false), ownsHttpClient: true)
            : new HttpClientTransport(transportOptions);

        return await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<ToolDescriptor[]> ListToolsAsync(
        Uri endpoint,
        Func<Uri, CancellationToken, Task<McpClient>> createClient,
        string[]? allowedTools,
        CancellationToken ct)
    {
        var tools = await RunWithSessionAsync(
            endpoint,
            createClient,
            async (client, token) => Describe(await client.ListToolsAsync(cancellationToken: token).ConfigureAwait(false)),
            ct).ConfigureAwait(false);

        return FilterAllowedTools(tools, allowedTools);
    }

    public async Task<ToolResult> CallToolAsync(
        Uri endpoint,
        Func<Uri, CancellationToken, Task<McpClient>> createClient,
        string tool,
        string arguments,
        CancellationToken ct)
    {
        var input = Arguments(arguments);
        return await RunWithSessionAsync(
            endpoint,
            createClient,
            async (client, token) => Result(JsonSerializer.SerializeToElement(
                await client.CallToolAsync(tool, input, cancellationToken: token).ConfigureAwait(false),
                CallResultJson)),
            ct).ConfigureAwait(false);
    }

    public async Task<ToolDescriptor[]> ProbeAsync(
        Uri endpoint,
        Dictionary<string, string>? headers,
        string[]? allowedTools,
        CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(CallBudget);
        await using var client = await CreateClientAsync(endpoint, headers, budget.Token).ConfigureAwait(false);
        var tools = Describe(await client.ListToolsAsync(cancellationToken: budget.Token).ConfigureAwait(false));
        return FilterAllowedTools(tools, allowedTools);
    }

    // Runs a single one-shot SSRF-validated MCP HTTP session (used by Salesforce and other plugin connectors).
    public async Task<T> RunEphemeralAsync<T>(
        Uri endpoint,
        Dictionary<string, string>? headers,
        Func<McpClient, CancellationToken, Task<T>> operation,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(operation);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        await using var client = await CreateClientAsync(endpoint, headers, budget.Token).ConfigureAwait(false);
        return await operation(client, budget.Token).ConfigureAwait(false);
    }

    public Task<IList<McpClientTool>> ReadCatalogAsync(
        Uri endpoint,
        Dictionary<string, string>? headers,
        CancellationToken ct,
        TimeSpan? timeout = null)
        => RunEphemeralAsync(
            endpoint,
            headers,
            static (client, token) => client.ListToolsAsync(cancellationToken: token).AsTask(),
            ct,
            timeout);

    internal static JsonElement SerializeCallResult(ModelContextProtocol.Protocol.CallToolResult result)
        => JsonSerializer.SerializeToElement(result, CallResultJson);

    public async Task DropAsync()
    {
        var client = _client;
        (_client, _endpoint) = (null, null);
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await DropAsync().ConfigureAwait(false);

    private async Task<T> RunWithSessionAsync<T>(
        Uri endpoint,
        Func<Uri, CancellationToken, Task<McpClient>> createClient,
        Func<McpClient, CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(CallBudget);
        var reused = _client is not null && _endpoint == endpoint;
        try
        {
            return await operation(await GetOrCreateClientAsync(endpoint, createClient, budget.Token).ConfigureAwait(false), budget.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (reused && error is not OperationCanceledException && error is not McpException)
        {
            OsConsoleLog.For<McpClientSession>().LogSwallowed(error, "retrying reused MCP client session");
            await DropAsync().ConfigureAwait(false);
            return await operation(await GetOrCreateClientAsync(endpoint, createClient, budget.Token).ConfigureAwait(false), budget.Token).ConfigureAwait(false);
        }
        catch (McpException)
        {
            await DropAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<McpClient> GetOrCreateClientAsync(
        Uri endpoint,
        Func<Uri, CancellationToken, Task<McpClient>> createClient,
        CancellationToken ct)
    {
        if (!IsAllowedEndpoint(endpoint))
        {
            throw new ArgumentException("An MCP server URL is an absolute http(s) URL without credentials.", nameof(endpoint));
        }

        if (_client is not null && _endpoint == endpoint)
        {
            return _client;
        }

        await DropAsync().ConfigureAwait(false);
        _client = await createClient(endpoint, ct).ConfigureAwait(false);
        _endpoint = endpoint;
        return _client;
    }

    internal static Dictionary<string, object?> Arguments(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
            }
        }
        catch (JsonException error)
        {
            throw new ArgumentException("arguments must be a JSON object.", nameof(arguments), error);
        }

        throw new ArgumentException("arguments must be a JSON object.", nameof(arguments));
    }

    internal static ToolDescriptor[] Describe(IList<McpClientTool> tools)
        => [.. tools.Select(tool =>
        {
            var wire = JsonSerializer.SerializeToElement(tool.ProtocolTool, ToolJson);
            var name = wire.GetProperty("name").GetString()!;
            return new ToolDescriptor(
                name,
                Text(wire, "title") ?? (wire.TryGetProperty("annotations", out var annotations) ? Text(annotations, "title") : null) ?? name,
                Text(wire, "description") ?? "",
                wire.TryGetProperty("inputSchema", out var schema) ? schema.GetRawText() : "{\"type\":\"object\"}",
                wire.TryGetProperty("annotations", out var hints) && hints.TryGetProperty("readOnlyHint", out var readOnly) && readOnly.ValueKind == JsonValueKind.True);
        })];

    internal static ToolResult Result(JsonElement wire)
    {
        var text = new StringBuilder();
        var blobs = new List<ToolBlob>();
        if (wire.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                switch (Text(block, "type"))
                {
                    case "text":
                        if (text.Length > 0) { text.Append('\n'); }
                        text.Append(Text(block, "text"));
                        break;
                    case "image" or "audio":
                        blobs.Add(new ToolBlob(Text(block, "mimeType") ?? "application/octet-stream", Text(block, "data") ?? ""));
                        break;
                    default:
                        if (text.Length > 0) { text.Append('\n'); }
                        text.Append(block.GetRawText());
                        break;
                }
            }
        }

        if (text.Length == 0 && wire.TryGetProperty("structuredContent", out var structured))
        {
            text.Append(structured.GetRawText());
        }

        var body = text.Length <= MaxText ? text.ToString() : text.ToString(0, MaxText) + "\n[truncated]";
        var isError = wire.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
        return new ToolResult(isError, body, [.. blobs]);
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
