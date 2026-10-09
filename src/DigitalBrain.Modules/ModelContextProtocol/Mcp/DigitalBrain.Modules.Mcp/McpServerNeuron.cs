using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace DigitalBrain.Mcp;

[GenerateSerializer, Alias("mcp.server-store")]
public sealed class McpServerStore
{
    [Id(0)] public McpServerState? State { get; set; }
}

// Opens one client per operation: servers are local containers or remote services, and the neuron,
// not a process, holds what is known about them.
[GrainType("mcp.server")]
internal sealed class McpServerNeuron(
    [PersistentState("mcp.server", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<McpServerStore> store)
    : Neuron, IMcpServer
{
    private const int MaxText = 128 * 1024;
    private static readonly TimeSpan CallBudget = TimeSpan.FromMinutes(5);

    private string Key => this.GetPrimaryKeyString();
    private string Brain => Key[..Key.IndexOf('/', StringComparison.Ordinal)];

    public async Task<McpServerState> Connect(McpServerSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Title);
        if (!Uri.TryCreate(spec.Url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo))
        { throw new ArgumentException("An MCP server URL is an absolute http(s) URL without credentials.", nameof(spec)); }
        ToolDescriptor[] tools;
        try { tools = await Session(endpoint, async (client, token) => Describe(await client.ListToolsAsync(cancellationToken: token)), ct); }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await Save(new McpServerState(spec.Title, spec.Url, false, [], error.Message, DateTimeOffset.UtcNow));
            throw new InvalidOperationException($"MCP server {spec.Url} is unreachable: {error.Message}", error);
        }
        var state = new McpServerState(spec.Title, spec.Url, true, tools, null, DateTimeOffset.UtcNow);
        await Save(state);
        await Sources().Register(new ToolSourceEntry(Key, typeof(IMcpServer).FullName!, spec.Title, tools));
        return state;
    }

    public async Task Disconnect(CancellationToken ct = default)
    {
        if (store.State?.State is { } state) { await Save(state with { Connected = false }); }
        await Sources().Unregister(Key);
    }

    public Task<McpServerState> Read()
        => Task.FromResult(store.State?.State ?? new McpServerState("", "", false, [], null, null));

    public Task<ToolDescriptor[]> Tools(CancellationToken ct = default)
        => Task.FromResult<ToolDescriptor[]>([.. store.State?.State?.Tools ?? []]);

    public async Task<ToolResult> Run(string tool, string arguments, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        var state = store.State?.State is { Connected: true } connected ? connected
            : throw new InvalidOperationException("The MCP server is not connected; call Connect first.");
        var input = Arguments(arguments);
        var clock = Stopwatch.StartNew();
        var result = await Session(new Uri(state.Url), async (client, token) =>
            Result(JsonSerializer.SerializeToElement(await client.CallToolAsync(tool, input, cancellationToken: token), McpJsonUtilities.DefaultOptions)), ct);
        await PublishAsync(new McpToolRan(tool, result.IsError, (int)clock.ElapsedMilliseconds));
        return result;
    }

    private IToolSources Sources() => GrainFactory.GetGrain<IToolSources>(Brain + "/tools");

    private async Task Save(McpServerState state)
    {
        store.State = new() { State = state };
        await store.WriteStateAsync();
    }

    private static async Task<T> Session<T>(Uri endpoint, Func<McpClient, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(CallBudget);
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = endpoint });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: budget.Token);
        return await operation(client, budget.Token);
    }

    private static Dictionary<string, object?> Arguments(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            { return document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone()); }
        }
        catch (JsonException) { }
        throw new ArgumentException("arguments must be a JSON object.", nameof(arguments));
    }

    // Read from the protocol's own JSON so the descriptor follows the wire shape, not SDK properties.
    private static ToolDescriptor[] Describe(IList<McpClientTool> tools)
        => [.. tools.Select(tool =>
        {
            var wire = JsonSerializer.SerializeToElement(tool.ProtocolTool, McpJsonUtilities.DefaultOptions);
            var name = wire.GetProperty("name").GetString()!;
            return new ToolDescriptor(
                name,
                Text(wire, "title") ?? (wire.TryGetProperty("annotations", out var annotations) ? Text(annotations, "title") : null) ?? name,
                Text(wire, "description") ?? "",
                wire.TryGetProperty("inputSchema", out var schema) ? schema.GetRawText() : "{\"type\":\"object\"}",
                wire.TryGetProperty("annotations", out var hints) && hints.TryGetProperty("readOnlyHint", out var readOnly) && readOnly.ValueKind == JsonValueKind.True);
        })];

    private static ToolResult Result(JsonElement wire)
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
        if (text.Length == 0 && wire.TryGetProperty("structuredContent", out var structured)) { text.Append(structured.GetRawText()); }
        var body = text.Length <= MaxText ? text.ToString() : text.ToString(0, MaxText) + "\n[truncated]";
        var isError = wire.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
        return new ToolResult(isError, body, [.. blobs]);
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
