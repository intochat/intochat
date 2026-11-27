using System.Net;
using System.Text;
using System.Text.Json;

namespace IntoChat.OS.Modules.Salesforce.Tests.Unit;

public sealed class SalesforceMcpProviderFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InvokeAndSchemaHashUseUnifiedMcpClientSessionAndRejectForbiddenEndpoints()
    {
        var handler = new FakeSalesforceMcpHandler();
        var provider = new SalesforceMcpProvider(new Uri("https://salesforce.example.test/mcp"), handler);

        using var args = JsonDocument.Parse("""{"q":"SELECT Id FROM Account LIMIT 1"}""");
        var result = await provider.InvokeAsync("soqlQuery", args.RootElement, "sf-access-token-sample", Ct);

        Assert.True(result.GetProperty("untrustedData").GetBoolean());
        Assert.Equal("Bearer sf-access-token-sample", handler.LastAuthorizationHeader);

        var hash = await provider.ReadToolSchemaHashAsync("soqlQuery", "sf-access-token-sample", Ct);
        Assert.Equal(64, hash.Length);

        // SSRF endpoint is rejected via unified McpClientSession validation.
        var ssrfProvider = new SalesforceMcpProvider(new Uri("http://169.254.169.254/latest/meta-data/"), handler);
        await Assert.ThrowsAsync<SalesforceUnavailableException>(() =>
            ssrfProvider.InvokeAsync("soqlQuery", args.RootElement, "sf-access-token-sample", Ct));
    }

    private sealed class FakeSalesforceMcpHandler : HttpMessageHandler
    {
        public string? LastAuthorizationHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.TryGetValues("Authorization", out var authValues))
            {
                LastAuthorizationHeader = authValues.FirstOrDefault();
            }

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
                    serverInfo = new { name = "sf-mcp", version = "1.0.0" },
                },
                "tools/list" => new
                {
                    tools = new[]
                    {
                        new { name = "soqlQuery", description = "Run SOQL", inputSchema = new { type = "object" } },
                    },
                },
                "tools/call" => new
                {
                    content = new[] { new { type = "text", text = """{"records":[{"Id":"001"}]}""" } },
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
