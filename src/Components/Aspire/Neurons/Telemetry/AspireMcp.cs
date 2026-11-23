using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace IntoChat.Aspire;

// The running AppHost seen through Aspire's own MCP server (`aspire agent mcp`, stdio): real resources, console and structured logs and
// traces, the same data the dashboard shows. Local development only: the AppHost passes its project path (AppHostKey) to the kernel it
// starts, and without it nothing is started. Call runs reading tools only; Command (restart) is the one writing path, gated by AspireNeuron.Restart.
internal sealed class AspireMcp(IConfiguration? configuration) : IAspireTools, IAsyncDisposable
{
    internal const string AppHostKey = "IntoChatOS:Aspire:AppHostPath";
    internal const string CommandKey = "IntoChatOS:Aspire:Command";

    internal static readonly IReadOnlySet<string> ReadTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "list_resources", "list_console_logs", "list_structured_logs", "list_traces", "list_trace_structured_logs",
    };

    // Read from Aspire CLI 17.0.0 (`aspire agent mcp` tools/list: execute_resource_command {resourceName, commandName, arguments?},
    // destructiveHint true) and Aspire.Hosting 17.1.0-preview.1.26569.1 (KnownResourceCommands restart = "resource-restart").
    internal const string CommandTool = "execute_resource_command";
    internal const string ResourceNameArgument = "resourceName";
    internal const string CommandNameArgument = "commandName";
    internal const string RestartCommand = "resource-restart";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;

    public bool Available => !string.IsNullOrWhiteSpace(configuration?[AppHostKey]);

    public async Task<string> Call(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        if (!ReadTools.Contains(tool)) { throw new ArgumentException($"'{tool}' is not one of the Aspire tools Ino may use: {string.Join(", ", ReadTools)}."); }
        var client = await Client(ct).ConfigureAwait(false);
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: ct).ConfigureAwait(false);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return result.IsError == true ? throw new InvalidOperationException(text) : text;
    }

    // Every resource, a page of at most 64 (the server's limit) at a time.
    public async Task<AspireResourceView[]> AllResources(CancellationToken ct)
    {
        const int Page = 64;
        var all = new List<AspireResourceView>();
        for (var offset = 0; offset < 1_000; offset += Page)
        {
            var page = Resources(await Call("list_resources", new Dictionary<string, object?> { ["offset"] = offset, ["limit"] = Page }, ct).ConfigureAwait(false));
            all.AddRange(page);
            if (page.Length < Page) { break; }
        }
        return [.. all];
    }

    public async Task Command(string resourceName, string commandName, CancellationToken ct)
    {
        var client = await Client(ct).ConfigureAwait(false);
        var result = await client.CallToolAsync(CommandTool, new Dictionary<string, object?> { [ResourceNameArgument] = resourceName, [CommandNameArgument] = commandName }, cancellationToken: ct).ConfigureAwait(false);
        if (result.IsError == true) { throw new InvalidOperationException(string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text))); }
    }

    // The "# RESOURCE DATA" section of list_resources, as the contract's views.
    internal static AspireResourceView[] Resources(string text)
    {
        var start = text.IndexOf("# RESOURCE DATA", StringComparison.Ordinal);
        var json = start < 0 ? null : text[(text.IndexOf('[', start) is var open and >= 0 ? open : text.Length)..];
        if (string.IsNullOrWhiteSpace(json)) { return []; }
        using var document = JsonDocument.Parse(json[..(json.LastIndexOf(']') + 1)]);
        return [.. document.RootElement.EnumerateArray().Select(item => new AspireResourceView(
            item.GetProperty("name").GetString() ?? "",
            item.TryGetProperty("state", out var state) ? state.GetString() ?? "" : "",
            item.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array && urls.GetArrayLength() > 0
                ? Url(urls[0]) : ""))];
    }

    private static string Url(JsonElement url) => url.ValueKind == JsonValueKind.String ? url.GetString() ?? ""
        : url.TryGetProperty("url", out var value) ? value.GetString() ?? "" : url.ToString();

    private async Task<McpClient> Client(CancellationToken ct)
    {
        if (_client is not null) { return _client; }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null) { return _client; }
            var appHost = configuration?[AppHostKey];
            if (string.IsNullOrWhiteSpace(appHost)) { throw new InvalidOperationException("No AppHost is known to this kernel, so Aspire's MCP server is not available (local development only)."); }
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "aspire",
                Command = configuration?[CommandKey] is { Length: > 0 } command ? command : "aspire",
                Arguments = ["agent", "mcp", "--non-interactive"],
                WorkingDirectory = Path.GetDirectoryName(appHost),
            });
            _client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
            return _client;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) { await _client.DisposeAsync().ConfigureAwait(false); }
        _gate.Dispose();
    }
}
