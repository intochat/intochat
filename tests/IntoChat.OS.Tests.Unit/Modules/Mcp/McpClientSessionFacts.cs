using System.Net;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Modules.Tools;

namespace IntoChat.OS.Modules.Mcp.Tests.Unit;

public sealed class McpClientSessionFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.test/mcp")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:secret@example.test/mcp")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/mcp")]
    [InlineData("http://172.16.0.1/mcp")]
    [InlineData("http://192.168.1.1/mcp")]
    [InlineData("http://0.0.0.0/mcp")]
    [InlineData("http://[fe80::1]/mcp")]
    [InlineData("http://[fd00::1]/mcp")]
    public void ValidateEndpointRefusesForbiddenAndNonHttpUrls(string url)
        => Assert.Throws<ArgumentException>(() => McpClientSession.ValidateEndpoint(url));

    [Theory]
    [InlineData("https://learn.microsoft.com/api/mcp")]
    [InlineData("http://127.0.0.1:8080/mcp")]
    [InlineData("http://localhost:5050/mcp")]
    public void ValidateEndpointAllowsHttpsAndLoopback(string url)
    {
        var uri = McpClientSession.ValidateEndpoint(url);
        Assert.True(uri.IsAbsoluteUri);
    }

    [Fact]
    public void BuildHeadersAppliesDefaultBearerPrefixAndValidatesHeaderName()
    {
        Assert.Null(McpClientSession.BuildHeaders("Authorization", "Bearer ", null));
        Assert.Null(McpClientSession.BuildHeaders("Authorization", "Bearer ", " "));

        var defaultAuth = McpClientSession.BuildHeaders(null, null, "tok_123");
        Assert.NotNull(defaultAuth);
        Assert.Equal("Bearer tok_123", defaultAuth!["Authorization"]);

        var alreadyPrefixed = McpClientSession.BuildHeaders("Authorization", "Bearer ", "Bearer tok_123");
        Assert.Equal("Bearer tok_123", alreadyPrefixed!["Authorization"]);

        var customHeader = McpClientSession.BuildHeaders("X-Api-Key", "", "k_456");
        Assert.Equal("k_456", customHeader!["X-Api-Key"]);

        Assert.Throws<ArgumentException>(() => McpClientSession.BuildHeaders("Bad Header", null, "tok"));
    }

    [Fact]
    public void FilterAllowedToolsAndIsToolAllowedEnforceAllowlist()
    {
        ToolDescriptor[] all =
        [
            new("a", "A", "", "{}", true),
            new("b", "B", "", "{}", true),
        ];

        Assert.Equal(["a", "b"], McpClientSession.FilterAllowedTools(all, null).Select(t => t.Name).ToArray());
        Assert.Equal(["a"], McpClientSession.FilterAllowedTools(all, ["a"]).Select(t => t.Name).ToArray());
        Assert.True(McpClientSession.IsToolAllowed("a", ["a"]));
        Assert.False(McpClientSession.IsToolAllowed("b", ["a"]));
        Assert.True(McpClientSession.IsToolAllowed("b", null));
    }

    [Fact]
    public async Task CallToolTruncatesOver128KbAndRetriesOnceOnTransientHttpFailure()
    {
        var huge = new string('x', McpClientSession.MaxText + 1024);
        var handler = new FlakyAndHugeMcpHandler(huge);
        await using var session = new McpClientSession(handler);
        var endpoint = new Uri("https://retry.example.test/mcp");

        var tools = await session.ListToolsAsync(endpoint, (ep, ct) => session.CreateClientAsync(ep, null, ct), null, Ct);
        Assert.Single(tools, t => t.Name == "big");

        handler.FailNextToolCallWithHttpError = true;
        var result = await session.CallToolAsync(endpoint, (ep, ct) => session.CreateClientAsync(ep, null, ct), "big", "{}", Ct);

        Assert.False(result.IsError);
        Assert.Contains("[truncated", result.Text, StringComparison.Ordinal);
        Assert.True(result.Text.Length <= McpClientSession.MaxText + 64);
        Assert.Equal(2, handler.ToolCallAttempts);
    }

    private sealed class FlakyAndHugeMcpHandler(string hugeText) : HttpMessageHandler
    {
        public bool FailNextToolCallWithHttpError { get; set; }
        public int ToolCallAttempts { get; private set; }

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
            if (method == "tools/call")
            {
                ToolCallAttempts++;
                if (FailNextToolCallWithHttpError)
                {
                    FailNextToolCallWithHttpError = false;
                    throw new HttpRequestException("simulated transient drop");
                }
            }

            object? result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "flaky-mcp", version = "1.0.0" },
                },
                "tools/list" => new
                {
                    tools = new[]
                    {
                        new { name = "big", description = "Returns large output", inputSchema = new { type = "object" } },
                    },
                },
                "tools/call" => new
                {
                    content = new[] { new { type = "text", text = hugeText } },
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
