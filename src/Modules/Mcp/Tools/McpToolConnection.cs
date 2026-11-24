using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Tools;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Tools;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Mcp;

[GenerateSerializer, Alias("mcp.tool-connection-state")]
internal sealed record McpToolConnectionState : ConnectionState
{
    [Id(0)] public string Title { get; set; } = "";
    [Id(1)] public string Url { get; set; } = "";
    [Id(2)] public bool Connected { get; set; }
    [Id(3)] public ToolDescriptor[] Tools { get; set; } = [];
    [Id(4)] public string? LastError { get; set; }
    [Id(5)] public DateTimeOffset? ProbedAt { get; set; }
}

// A connection neuron representing an external MCP tool source offering tool@1.
[Neuron(IMcpToolConnection.TypeName, State = "mcp.tool")]
internal sealed partial class McpToolConnection(
    IMcpServer? underlyingServer = null,
    ILogger<McpToolConnection>? logger = null)
    : ConnectionNeuron<McpToolConnectionState>, IMcpToolConnection
{
    private readonly ILogger _log = logger ?? McpConsoleLog.For<McpToolConnection>();

    [DigitalBrain.Slot(Required = false)]
    private partial DigitalBrain.Components.SlotCollection<IMcpServer> McpServers { get; }

    private IMcpServer Server => underlyingServer
        ?? (Context is not null
            ? McpServers[Id.Key]
            : throw new InvalidOperationException("No MCP server or neuron context configured for this connection."));

    public Task<IReadOnlyList<string>> List()
    {
        IReadOnlyList<string> list = Snapshot.Tools.Select(t => t.Name).ToList();
        return Task.FromResult(list);
    }

    public Task<ToolSet> Tools(CancellationToken ct = default)
    {
        return Task.FromResult(new ToolSet(Snapshot.Title, Snapshot.Tools.ToArray()));
    }

    public Task<McpServerState> ReadState(CancellationToken ct = default)
    {
        return Task.FromResult(new McpServerState(
            Snapshot.Title,
            Snapshot.Url,
            Snapshot.Connected,
            Snapshot.Tools.ToArray(),
            Snapshot.LastError,
            Snapshot.ProbedAt));
    }

    public Task<IReadOnlyList<ToolDescriptor>> Search(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<ToolDescriptor>>(Snapshot.Tools.ToArray());
        }

        var trimmed = query.Trim();
        var matches = Snapshot.Tools.Where(t =>
            t.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
            t.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
            t.Description.Contains(trimmed, StringComparison.OrdinalIgnoreCase)).ToList();

        return Task.FromResult<IReadOnlyList<ToolDescriptor>>(matches);
    }

    public Task<bool> Validate(string tool, JsonElement args, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tool)) { return Task.FromResult(false); }
        var descriptor = Snapshot.Tools.FirstOrDefault(t => string.Equals(t.Name, tool.Trim(), StringComparison.OrdinalIgnoreCase));
        if (descriptor is null) { return Task.FromResult(false); }

        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public async Task<JsonElement> Run(string tool, JsonElement args, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        var descriptor = Snapshot.Tools.FirstOrDefault(t => string.Equals(t.Name, tool.Trim(), StringComparison.OrdinalIgnoreCase));
        if (descriptor is null && Snapshot.Tools.Length > 0)
        {
            throw new ToolExecutionException(tool, $"Tool '{tool}' was not found in MCP server '{Snapshot.Title}'.", isNotFound: true);
        }

        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
        {
            throw new ToolExecutionException(tool, "Tool arguments must be a JSON object.", isValidationError: true);
        }

        string argumentsString = args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : args.GetRawText();
        var result = await Run(tool, argumentsString, ct).ConfigureAwait(false);

        if (result.IsError)
        {
            throw new ToolExecutionException(tool, result.Text);
        }

        return ToolResultJson.From(result.Text, _log);
    }

    public async Task<ToolResult> Run(string tool, string arguments, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        if (!Snapshot.Connected && underlyingServer is null)
        {
            throw new InvalidOperationException($"MCP server '{Snapshot.Title}' is not connected.");
        }

        return await Server.Run(tool, arguments, ct).ConfigureAwait(false);
    }

    protected override async Task OnConnectedAsync()
    {
        string rawValue = "";
        await UseCredentialAsync((credential, _) =>
        {
            rawValue = credential.Value ?? "";
            return Task.FromResult(0);
        }, CancellationToken.None).ConfigureAwait(false);

        var (url, title) = ParseSpec(rawValue, _log);
        var next = Snapshot with
        {
            Url = url,
            Title = string.IsNullOrWhiteSpace(title) ? (Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Host : "MCP Server") : title,
            Connected = true,
            LastError = null,
            ProbedAt = Context.Time.GetUtcNow(),
        };

        try
        {
            var serverState = await Server.Connect(new McpServerSpec(next.Title, next.Url), CancellationToken.None).ConfigureAwait(false);
            next = next with { Tools = [.. serverState.Tools] };
        }
        catch (Exception ex)
        {
            _log.LogSwallowed(ex, $"Failed connecting MCP server '{next.Url}'.");
            next = next with { LastError = ex.Message, Connected = false };
        }

        await SaveQuietly(next).ConfigureAwait(false);
    }

    protected override async Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
    {
        var (url, title) = ParseSpec(credential.Value, _log);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
        {
            return ConnectionProbeResult.Failing("Invalid MCP server URL. Absolute HTTP/HTTPS URL required.");
        }

        try
        {
            var serverState = await Server.Connect(new McpServerSpec(title, url), ct).ConfigureAwait(false);
            return ConnectionProbeResult.Connected($"Connected to {serverState.Title} ({serverState.Tools.Length} tools).");
        }
        catch (Exception ex)
        {
            _log.LogSwallowed(ex, $"Probing MCP server '{url}' failed.");
            return ConnectionProbeResult.Failing($"Probe failed: {ex.Message}");
        }
    }

    protected override async Task OnDisconnectedAsync()
    {
        await SaveQuietly(Snapshot with { Connected = false, Tools = [] }).ConfigureAwait(false);
        try
        {
            await Server.Disconnect(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.LogSwallowed(error, "Ignoring MCP server disconnect error.");
        }
    }

    private static (string Url, string Title) ParseSpec(string raw, ILogger log)
    {
        var trimmed = (raw ?? "").Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                var url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                return (url, title);
            }
            catch (JsonException error)
            {
                log.LogSwallowed(error, "Falling back to raw MCP connection string as URL.");
            }
        }
        return (trimmed, "");
    }
}
