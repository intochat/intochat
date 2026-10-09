using ModelContextProtocol.Client;

namespace DigitalBrain.Launcher.Telemetry;

// The Aspire CLI's own MCP server sees every running AppHost on this machine; the launcher asks
// it for traces so a revision's spans are read the same way a developer reads them.
internal sealed class AspireCliTelemetry : IAspireTelemetry, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;

    public async Task<int> CountSpans(string appHostPath, string name, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        await client.CallToolAsync("select_apphost", new Dictionary<string, object?> { ["apphost_path"] = appHostPath }, cancellationToken: ct);
        var traces = await client.CallToolAsync("list_traces", new Dictionary<string, object?>(), cancellationToken: ct);
        var text = string.Concat(traces.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(block => block.Text));
        var count = 0;
        var cursor = 0;
        while ((cursor = text.IndexOf(name, cursor, StringComparison.Ordinal)) >= 0) { count++; cursor += name.Length; }
        return count;
    }

    private async Task<McpClient> ClientAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return _client ??= await McpClient.CreateAsync(new StdioClientTransport(new() { Name = "aspire", Command = "aspire", Arguments = ["mcp", "start"] }), cancellationToken: ct);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync() { if (_client is not null) { await _client.DisposeAsync(); } }
}