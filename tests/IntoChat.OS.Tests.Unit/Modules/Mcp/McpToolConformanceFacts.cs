using System.Net;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Mcp.Tests.Unit;

public sealed class McpToolConformanceFacts : ToolConformance<IMcpToolConnection>, IAsyncDisposable
{
    private readonly List<TestOS> _hosts = [];

    protected override async Task<ToolSubject> Connect(CancellationToken ct)
    {
        var handler = new ConformanceMcpHttpHandler();
        var services = new ServiceCollection()
            .AddSingleton<HttpMessageHandler>(handler)
            .BuildServiceProvider();

        var os = TestOS.Local(o => o.Services = services).Register(typeof(McpModule).Assembly);
        _hosts.Add(os);

        var brain = new BrainId("facts");
        var id = IMcpToolConnection.NeuronIdOf(brain, $"kit-{Guid.NewGuid():N}");
        var conn = os.Get<IMcpToolConnection>(id);
        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", brain)))
        {
            await conn.ConnectServer(new McpConnectionConfig("https://kit.example.test/mcp", "Kit MCP"), ct);
        }

        using var argsDoc = JsonDocument.Parse("""{"query":"hello"}""");
        return new ToolSubject(new ScopedTool(conn, brain), "echo", argsDoc.RootElement.Clone());
    }

    public async ValueTask DisposeAsync()
    {
        for (var i = _hosts.Count - 1; i >= 0; i--)
        {
            await _hosts[i].DisposeAsync();
        }
    }

    private sealed class ScopedTool(IMcpToolConnection inner, BrainId brain) : DigitalBrain.Capabilities.Tools.ITool
    {
        public async Task<IReadOnlyList<string>> List()
        {
            using var _ = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", brain));
            return await inner.List();
        }

        public async Task<JsonElement> Run(string tool, JsonElement args, CancellationToken ct = default)
        {
            using var _ = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", brain));
            return await inner.Run(tool, args, ct);
        }
    }

    private sealed class ConformanceMcpHttpHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is not null ? await request.Content.ReadAsStringAsync(cancellationToken) : "";
            if (string.IsNullOrWhiteSpace(body))
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idProp))
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            var method = root.GetProperty("method").GetString();
            var idVal = JsonSerializer.Deserialize<object>(idProp.GetRawText());
            object? result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "kit-mcp", version = "1.0.0" },
                },
                "tools/list" => new
                {
                    tools = new[]
                    {
                        new { name = "echo", description = "Echoes input", inputSchema = new { type = "object" } },
                    },
                },
                "tools/call" => new
                {
                    content = new[] { new { type = "text", text = """{"ok":true}""" } },
                    isError = false,
                },
                _ => null,
            };

            var payload = result is not null
                ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id = idVal, result })
                : JsonSerializer.Serialize(new { jsonrpc = "2.0", id = idVal, error = new { code = -32601, message = "Method not found" } });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }
}
