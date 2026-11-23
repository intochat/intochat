using System.Net;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Mcp.Tests.Unit;

// The neuron's argument and state rules, answered before any session is opened: no server, no socket.
public sealed class McpServerNeuronFacts
{
    private static (TestOS Os, IMcpServer Neuron) Open(McpServerState? state = null)
    {
        var os = TestOS.Local().Register(typeof(McpModule).Assembly);
        var id = NeuronId.Parse($"facts/mcp.server/mcp/{Guid.NewGuid():N}");
        if (state is not null)
        {
            os.SeedStoredState(id, JsonSerializer.Serialize(new McpServerStore { State = state }, RuntimeProtocol.OptionsFor(typeof(McpServerStore))));
        }
        return (os, os.Get<IMcpServer>(id));
    }

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
    public async Task ConnectRefusesAUrlThatIsNotCredentialFreeHttp(string url)
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", new BrainId("facts")));
        var (os, neuron) = Open();
        await using var _ = os;
        await Assert.ThrowsAsync<ArgumentException>(() => neuron.Connect(new McpServerSpec("Title", url), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectRefusesABlankTitleAndANullSpec()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", new BrainId("facts")));
        var (os, neuron) = Open();
        await using var _ = os;
        await Assert.ThrowsAsync<ArgumentException>(() => neuron.Connect(new McpServerSpec(" ", "https://example.test/mcp"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => neuron.Connect(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANeverConnectedServerReadsEmptyAndOffersNoTools()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", new BrainId("facts")));
        var (os, neuron) = Open();
        await using var _ = os;

        var state = await neuron.Read();
        var tools = await neuron.Tools(TestContext.Current.CancellationToken);

        Assert.False(state.Connected);
        Assert.Empty(state.Tools);
        Assert.Empty(tools.Tools);
        Assert.Equal("", tools.Title);
    }

    [Fact]
    public async Task ADisconnectedServerKeepsItsStateButOffersNoTools()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", new BrainId("facts")));
        var tool = new ToolDescriptor("echo", "Echo", "", "{\"type\":\"object\"}", true);
        var (os, neuron) = Open(new McpServerState("Fake", "https://example.test/mcp", false, [tool], null, null));
        await using var _ = os;

        Assert.Equal("Fake", (await neuron.Read()).Title);
        Assert.Empty((await neuron.Tools(TestContext.Current.CancellationToken)).Tools);
    }

    [Fact]
    public async Task AConnectedServerOffersItsToolsUnderItsTitle()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", new BrainId("facts")));
        var tool = new ToolDescriptor("echo", "Echo", "", "{\"type\":\"object\"}", true);
        var (os, neuron) = Open(new McpServerState("Fake", "https://example.test/mcp", true, [tool], null, null));
        await using var _ = os;

        var tools = await neuron.Tools(TestContext.Current.CancellationToken);

        Assert.Equal("Fake", tools.Title);
        Assert.Equal("echo", Assert.Single(tools.Tools).Name);
    }

    [Fact]
    public async Task RunNeedsAToolNameAndAConnectedServer()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", new BrainId("facts")));
        var (os, neuron) = Open();
        await using var _ = os;
        await Assert.ThrowsAsync<ArgumentException>(() => neuron.Run(" ", "{}", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => neuron.Run("echo", "{}", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{broken")]
    public async Task RunRefusesArgumentsThatAreNotAJsonObject(string arguments)
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", new BrainId("facts")));
        var connected = new McpServerState("Fake", "https://example.test/mcp", true, [], null, null);
        var (os, neuron) = Open(connected);
        await using var _ = os;
        await Assert.ThrowsAsync<ArgumentException>(() => neuron.Run("echo", arguments, TestContext.Current.CancellationToken));
    }

    private static TestOS OpenWithServices(McpModuleOptions options, HttpMessageHandler handler)
    {
        var services = new ServiceCollection()
            .AddSingleton<IOptions<McpModuleOptions>>(Options.Create(options))
            .AddSingleton(handler)
            .BuildServiceProvider();
        return TestOS.Local(o => o.Services = services).Register(typeof(McpModule).Assembly);
    }

    [Fact]
    public async Task SecretHeaderIsSentOnlyToTheConfiguredServerAndNeverExposedInReadOrJson()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", new BrainId("facts")));
        var handler = new FakeMcpHttpHandler(["a", "b"]);
        var options = new McpModuleOptions
        {
            Servers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["crm"] = new()
                {
                    Url = "https://crm.example.test/api/mcp",
                    Title = "Muncly CRM",
                    HeaderName = "Authorization",
                    HeaderValue = "Bearer secret-token-123",
                },
                ["other"] = new()
                {
                    Url = "https://other.example.test/api/mcp",
                    Title = "Other MCP",
                },
            },
        };
        options.Validate();

        await using var os = OpenWithServices(options, handler);

        var crm = os.Get<IMcpServer>(NeuronId.Parse("facts/mcp.server/mcp/crm"));
        var other = os.Get<IMcpServer>(NeuronId.Parse("facts/mcp.server/mcp/other"));

        var crmState = await crm.Connect(new McpServerSpec("Muncly CRM", "https://crm.example.test/api/mcp"), TestContext.Current.CancellationToken);
        await other.Connect(new McpServerSpec("Other MCP", "https://other.example.test/api/mcp"), TestContext.Current.CancellationToken);

        Assert.True(crmState.Connected);
        Assert.NotEmpty(handler.Requests);
        var crmRequests = handler.Requests.Where(r => r.Uri.Host == "crm.example.test").ToArray();
        var otherRequests = handler.Requests.Where(r => r.Uri.Host == "other.example.test").ToArray();
        Assert.NotEmpty(crmRequests);
        Assert.NotEmpty(otherRequests);
        Assert.All(crmRequests, r => Assert.Equal("Bearer secret-token-123", r.AuthHeader));
        Assert.All(otherRequests, r => Assert.Null(r.AuthHeader));

        var readJson = JsonSerializer.Serialize(await crm.Read());
        Assert.DoesNotContain("secret-token-123", readJson, StringComparison.Ordinal);

        var optionsJson = JsonSerializer.Serialize(options.Servers["crm"]);
        Assert.DoesNotContain("secret-token-123", optionsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowedToolsFiltersListedToolsAndBlocksDisallowedCallsBeforeNetwork()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", new BrainId("facts")));
        var handler = new FakeMcpHttpHandler(["a", "b"]);
        var options = new McpModuleOptions
        {
            Servers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["scoped"] = new()
                {
                    Url = "https://scoped.example.test/api/mcp",
                    Title = "Scoped",
                    AllowedTools = ["a"],
                },
            },
        };
        options.Validate();

        await using var os = OpenWithServices(options, handler);

        var neuron = os.Get<IMcpServer>(NeuronId.Parse("facts/mcp.server/mcp/scoped"));
        var state = await neuron.Connect(new McpServerSpec("Scoped", "https://scoped.example.test/api/mcp"), TestContext.Current.CancellationToken);

        Assert.Equal(["a"], state.Tools.Select(t => t.Name).ToArray());
        Assert.Equal(["a"], (await neuron.Tools(TestContext.Current.CancellationToken)).Tools.Select(t => t.Name).ToArray());

        var callsBefore = handler.ToolCalls.Count;
        var ex = await Assert.ThrowsAsync<ToolExecutionException>(() => neuron.Run("b", "{}", TestContext.Current.CancellationToken));
        Assert.True(ex.IsNotFound);
        Assert.Equal(callsBefore, handler.ToolCalls.Count);

        var ok = await neuron.Run("a", "{}", TestContext.Current.CancellationToken);
        Assert.False(ok.IsError);
        Assert.Equal(["a"], handler.ToolCalls);
    }

    [Fact]
    public async Task NullAllowedToolsPermitsEveryToolOfferedByTheRemote()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", new BrainId("facts")));
        var handler = new FakeMcpHttpHandler(["a", "b"]);
        var options = new McpModuleOptions
        {
            Servers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["open"] = new()
                {
                    Url = "https://open.example.test/api/mcp",
                    Title = "Open",
                    AllowedTools = null,
                },
            },
        };

        await using var os = OpenWithServices(options, handler);

        var neuron = os.Get<IMcpServer>(NeuronId.Parse("facts/mcp.server/mcp/open"));
        var state = await neuron.Connect(new McpServerSpec("Open", "https://open.example.test/api/mcp"), TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], state.Tools.Select(t => t.Name).ToArray());
        var res = await neuron.Run("b", "{}", TestContext.Current.CancellationToken);
        Assert.False(res.IsError);
        Assert.Equal(["b"], handler.ToolCalls);
    }

    [Fact]
    public void OptionsValidateHeaderPairAndAllowedToolsEntries()
    {
        var missingValue = new McpModuleOptions
        {
            Servers = new() { ["s"] = new() { Url = "https://example.test/mcp", HeaderName = "Authorization" } },
        };
        Assert.Throws<ArgumentException>(missingValue.Validate);

        var missingName = new McpModuleOptions
        {
            Servers = new() { ["s"] = new() { Url = "https://example.test/mcp", HeaderValue = "Bearer x" } },
        };
        Assert.Throws<ArgumentException>(missingName.Validate);

        var invalidHeader = new McpModuleOptions
        {
            Servers = new() { ["s"] = new() { Url = "https://example.test/mcp", HeaderName = "Bad Header", HeaderValue = "Bearer x" } },
        };
        Assert.Throws<ArgumentException>(invalidHeader.Validate);

        var emptyAllowed = new McpModuleOptions
        {
            Servers = new() { ["s"] = new() { Url = "https://example.test/mcp", AllowedTools = ["a", " "] } },
        };
        Assert.Throws<ArgumentException>(emptyAllowed.Validate);

        var valid = new McpModuleOptions
        {
            Servers = new()
            {
                ["s"] = new()
                {
                    Url = "https://example.test/mcp",
                    HeaderName = "Authorization",
                    HeaderValue = "Bearer x",
                    AllowedTools = ["a"],
                },
            },
        };
        valid.Validate();
    }

    private sealed class FakeMcpHttpHandler(string[] remoteTools) : HttpMessageHandler
    {
        public sealed record RecordedRequest(Uri Uri, string? AuthHeader);
        public List<RecordedRequest> Requests { get; } = [];
        public List<string> ToolCalls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var auth = request.Headers.TryGetValues("Authorization", out var vals) ? string.Join(",", vals) : null;
            Requests.Add(new RecordedRequest(request.RequestUri!, auth));

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
                    serverInfo = new { name = "fake-mcp", version = "1.0.0" },
                },
                "tools/list" => new
                {
                    tools = remoteTools.Select(name => new
                    {
                        name,
                        description = $"Tool {name}",
                        inputSchema = new { type = "object" },
                    }).ToArray(),
                },
                "tools/call" => HandleToolCall(root),
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

        private object HandleToolCall(JsonElement root)
        {
            var name = root.GetProperty("params").GetProperty("name").GetString()!;
            ToolCalls.Add(name);
            return new
            {
                content = new[] { new { type = "text", text = $"{{\"ok\":true,\"tool\":\"{name}\"}}" } },
                isError = false,
            };
        }
    }
}
