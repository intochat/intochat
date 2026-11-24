using System.Net;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Capabilities.Tools;
using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Mcp.Tests.Unit;

public sealed class McpToolConnectionFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task KeylessConnectServerListsSearchesValidatesRunsAndDisconnects()
    {
        var handler = new FakeMcpHttpHandler(["fetch_data", "send_email"])
        {
            OnToolCall = (name, _) => name == "fetch_data"
                ? (false, """{"sum":42}""")
                : (true, "Internal crash"),
        };

        await using var os = CreateHost(handler);
        var brain = new BrainId("facts");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", brain));

        var id = IMcpToolConnection.NeuronIdOf(brain, "docs");
        var conn = os.Get<IMcpToolConnection>(id);

        Assert.False((await conn.ReadState(Ct)).Connected);
        Assert.Empty((await conn.Tools(Ct)).Tools);

        var connected = await conn.ConnectServer(new McpConnectionConfig("https://docs.example.test/mcp", "Docs"), Ct);
        Assert.True(connected.Connected);
        Assert.Equal("Docs", connected.Title);
        Assert.Equal(2, connected.Tools.Length);

        var list = await conn.List();
        Assert.Equal(2, list.Count);
        Assert.Contains("fetch_data", list);
        Assert.Contains("send_email", list);

        var search = await conn.Search("email", Ct);
        Assert.Equal("send_email", Assert.Single(search).Name);

        Assert.True(await conn.Validate("fetch_data", JsonDocument.Parse("{}").RootElement, Ct));
        Assert.False(await conn.Validate("unknown", JsonDocument.Parse("{}").RootElement, Ct));
        Assert.False(await conn.Validate("fetch_data", JsonDocument.Parse("[1, 2]").RootElement, Ct));

        var result = await conn.Run("fetch_data", JsonDocument.Parse("""{"a":20}""").RootElement, Ct);
        Assert.Equal(42, result.GetProperty("sum").GetInt32());

        var notFound = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            conn.Run("missing", JsonDocument.Parse("{}").RootElement, Ct));
        Assert.True(notFound.IsNotFound);

        var failed = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            conn.Run("send_email", JsonDocument.Parse("{}").RootElement, Ct));
        Assert.False(failed.IsNotFound);
        Assert.Contains("Internal crash", failed.Message, StringComparison.Ordinal);

        await conn.DisconnectServer(Ct);
        var disconnected = await conn.ReadState(Ct);
        Assert.False(disconnected.Connected);
        Assert.Equal("Docs", disconnected.Title);
        Assert.Empty((await conn.Tools(Ct)).Tools);
    }

    [Fact]
    public async Task McpToolConnectionRunWrapsNonJsonTextAsResult()
    {
        var handler = new FakeMcpHttpHandler(["echo"])
        {
            OnToolCall = (_, _) => (false, "plain \"text\""),
        };

        await using var os = CreateHost(handler);
        var brain = new BrainId("facts");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", brain));

        var conn = os.Get<IMcpToolConnection>(IMcpToolConnection.NeuronIdOf(brain, "echo"));
        await conn.ConnectServer(new McpConnectionConfig("https://echo.example.test/mcp", "Echo"), Ct);

        var result = await conn.Run("echo", JsonDocument.Parse("{}").RootElement, Ct);
        Assert.Equal("plain \"text\"", result.GetProperty("result").GetString());
    }

    [Fact]
    public async Task KeyedConnectSealsTokenNeverExposesItAndConfigurePreservesToken()
    {
        var handler = new FakeMcpHttpHandler(["a", "b"]);
        var sealer = new PrefixSealer();
        await using var os = CreateHost(handler, sealer);

        var brain = new BrainId("facts");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", brain));

        var crmId = IMcpToolConnection.NeuronIdOf(brain, "crm");
        var otherId = IMcpToolConnection.NeuronIdOf(brain, "other");
        const string secretToken = "tok_super_secret_987";

        var payload = McpServersBootstrap.SerializeSeedPayload(
            new McpConnectionConfig("https://crm.example.test/api/mcp", "Muncly CRM", HeaderName: "Authorization", HeaderPrefix: "Bearer ", AllowedTools: ["a"]),
            secretToken);

        await os.Get<IConnection>(crmId).Connect("bearer-token", payload);
        await os.Get<IMcpToolConnection>(otherId).ConnectServer(new McpConnectionConfig("https://other.example.test/api/mcp", "Other"), Ct);

        var crm = os.Get<IMcpToolConnection>(crmId);
        var crmState = await crm.ReadState(Ct);
        Assert.True(crmState.Connected);
        Assert.Equal(["a"], crmState.Tools.Select(t => t.Name).ToArray());

        // Sealed token is sent only to crm and never leaked into state or JSON.
        var crmRequests = handler.Requests.Where(r => r.Uri.Host == "crm.example.test").ToArray();
        var otherRequests = handler.Requests.Where(r => r.Uri.Host == "other.example.test").ToArray();
        Assert.NotEmpty(crmRequests);
        Assert.NotEmpty(otherRequests);
        Assert.All(crmRequests, r => Assert.Equal($"Bearer {secretToken}", r.AuthHeader));
        Assert.All(otherRequests, r => Assert.Null(r.AuthHeader));
        Assert.DoesNotContain(secretToken, JsonSerializer.Serialize(crmState), StringComparison.Ordinal);

        // AllowedTools blocks 'b' before hitting the network.
        var callsBefore = handler.ToolCalls.Count;
        var blocked = await Assert.ThrowsAsync<ToolExecutionException>(() => crm.Run("b", "{}", Ct));
        Assert.True(blocked.IsNotFound);
        Assert.Equal(callsBefore, handler.ToolCalls.Count);

        // Configure updates AllowedTools without needing to re-supply or clobber the sealed token.
        var reconfigured = await crm.Configure(
            new McpConnectionConfig("https://crm.example.test/api/mcp", "Muncly CRM Updated", HeaderName: "Authorization", HeaderPrefix: "Bearer ", AllowedTools: ["a", "b"]),
            Ct);
        Assert.Equal("Muncly CRM Updated", reconfigured.Title);
        Assert.Equal(["a", "b"], reconfigured.Tools.Select(t => t.Name).ToArray());

        var res = await crm.Run("b", "{}", Ct);
        Assert.False(res.IsError);
        Assert.Equal($"Bearer {secretToken}", handler.Requests.Last(r => r.Uri.Host == "crm.example.test").AuthHeader);
    }

    [Fact]
    public async Task ConfirmationModeGatesAgentCallsForOncePerSessionAndEveryCall()
    {
        var handler = new FakeMcpHttpHandler(["mutate"]);
        await using var os = CreateHost(handler, withConfirmations: true);

        var brain = new BrainId("facts");
        var id = IMcpToolConnection.NeuronIdOf(brain, "guarded");
        var personCaller = IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", brain);
        var inbox = os.Get<IConfirmationInbox>(IConfirmationRequests.Of(brain));

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(personCaller))
        {
            await os.Get<IMcpToolConnection>(id).ConnectServer(
                new McpConnectionConfig("https://guarded.example.test/mcp", "Guarded", ConfirmationMode: McpConfirmationMode.OncePerSession),
                Ct);
        }

        // Agent caller in a conversation triggers confirmation.
        var agentCaller = IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("facts", "facts"), brain, conversation: "facts/conversations/c1");
        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(agentCaller))
        {
            var first = await os.Get<IMcpToolConnection>(id).Run("mutate", "{}", Ct);
            Assert.True(first.IsError);
            Assert.Contains("Confirmation required", first.Text, StringComparison.Ordinal);
            Assert.Empty(handler.ToolCalls);
        }

        // Approve the requested plan as a person; next calls in the same session succeed without re-prompting.
        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(personCaller))
        {
            var pending = (await inbox.Read()).Pending;
            var plan = Assert.Single(pending);
            await inbox.Resolve(plan.PlanId, approve: true);
        }

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(agentCaller))
        {
            var second = await os.Get<IMcpToolConnection>(id).Run("mutate", "{}", Ct);
            Assert.False(second.IsError);
            Assert.Single(handler.ToolCalls);

            var third = await os.Get<IMcpToolConnection>(id).Run("mutate", "{}", Ct);
            Assert.False(third.IsError);
            Assert.Equal(2, handler.ToolCalls.Count);
        }

        // Switch to EveryCall: approvals are single-use and each new call must confirm again.
        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(personCaller))
        {
            await os.Get<IMcpToolConnection>(id).Configure(
                new McpConnectionConfig("https://guarded.example.test/mcp", "Guarded", ConfirmationMode: McpConfirmationMode.EveryCall),
                Ct);
        }

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(agentCaller))
        {
            var gated = await os.Get<IMcpToolConnection>(id).Run("mutate", "{}", Ct);
            Assert.True(gated.IsError);
            Assert.Contains("Confirmation required", gated.Text, StringComparison.Ordinal);
            Assert.Equal(2, handler.ToolCalls.Count);
        }

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(personCaller))
        {
            var pending = (await inbox.Read()).Pending;
            var plan = Assert.Single(pending);
            await inbox.Resolve(plan.PlanId, approve: true);
        }

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(agentCaller))
        {
            var fourth = await os.Get<IMcpToolConnection>(id).Run("mutate", "{}", Ct);
            Assert.False(fourth.IsError);
            Assert.Equal(3, handler.ToolCalls.Count);

            var fifth = await os.Get<IMcpToolConnection>(id).Run("mutate", "{}", Ct);
            Assert.True(fifth.IsError);
            Assert.Contains("Confirmation required", fifth.Text, StringComparison.Ordinal);
            Assert.Equal(3, handler.ToolCalls.Count);
        }
    }

    [Fact]
    public async Task ConnectServerRefusesInvalidUrlsAndRunRefusesInvalidArguments()
    {
        await using var os = CreateHost(new FakeMcpHttpHandler(["echo"]));
        var brain = new BrainId("facts");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", brain));

        var conn = os.Get<IMcpToolConnection>(IMcpToolConnection.NeuronIdOf(brain, "validate"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => conn.ConnectServer(null!, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => conn.ConnectServer(new McpConnectionConfig("http://169.254.169.254/latest/meta-data/", "Meta"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => conn.Run(" ", "{}", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => conn.Run("echo", "{}", Ct));

        await conn.ConnectServer(new McpConnectionConfig("https://ok.example.test/mcp", "Ok"), Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => conn.Run("echo", "[1,2]", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => conn.Run("echo", "{broken", Ct));
    }

    [Fact]
    public async Task BrainToolsConnectionAggregatesAcrossSources()
    {
        var t1 = new ToolDescriptor("source1_tool", "S1", "First source tool", "{}", true);
        var t2 = new ToolDescriptor("source2_tool", "S2", "Second source tool", "{}", true);

        var source1 = new StubToolSource("S1", [t1], (_, _) => new ToolResult(false, """{"from":1}""", []));
        var source2 = new StubToolSource("S2", [t2], (_, _) => new ToolResult(false, """{"from":2}""", []));

        var brainServices = new ServiceCollection()
            .AddSingleton<IToolSource>(source1)
            .AddSingleton<IToolSource>(source2)
            .BuildServiceProvider();
        var brainTools = ActivatorUtilities.CreateInstance<BrainToolsConnection>(brainServices);
        var context = new FakeNeuronContext(NeuronId.Parse("facts/brain.tools/main"));
        NeuronAttachment.Attach(brainTools, context);
        await NeuronAttachment.Activate(brainTools, Ct);

        var all = await brainTools.List();
        Assert.Equal(2, all.Count);
        Assert.Contains("source1_tool", all);
        Assert.Contains("source2_tool", all);

        var search1 = await brainTools.Search("First", Ct);
        Assert.Equal("source1_tool", Assert.Single(search1).Name);

        var res1 = await brainTools.Run("source1_tool", JsonDocument.Parse("{}").RootElement, Ct);
        Assert.Equal(1, res1.GetProperty("from").GetInt32());

        var res2 = await brainTools.Run("source2_tool", JsonDocument.Parse("{}").RootElement, Ct);
        Assert.Equal(2, res2.GetProperty("from").GetInt32());

        var ex = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            brainTools.Run("missing_tool", JsonDocument.Parse("{}").RootElement, Ct));
        Assert.True(ex.IsNotFound);
    }

    private static TestOS CreateHost(HttpMessageHandler handler, ISecretSealer? sealer = null, bool withConfirmations = false)
    {
        var services = new ServiceCollection()
            .AddSingleton(handler)
            .AddSingleton<IReadOnlyList<DigitalBrain.Capabilities.Connections.Connectors.ConnectorDefinition>>([McpModule.Connector]);
        if (sealer is not null)
        {
            services.AddSingleton(sealer);
        }

        var os = TestOS.Local(o => o.Services = services.BuildServiceProvider())
            .Register(typeof(McpModule).Assembly)
            .Register<ConnectionCatalogNeuron>();
        if (withConfirmations)
        {
            os.Register<ConfirmationsNeuron>();
        }

        return os;
    }

    private sealed class StubToolSource(string title, ToolDescriptor[] tools, Func<string, string, ToolResult> onRun) : IToolSource
    {
        public Task<ToolSet> Tools(CancellationToken ct = default) => Task.FromResult(new ToolSet(title, tools));
        public Task<ToolResult> Run(string tool, string arguments, CancellationToken ct = default) => Task.FromResult(onRun(tool, arguments));
    }

    private sealed class PrefixSealer : ISecretSealer
    {
        public SealedSecret Seal(string name, string plaintext)
            => new() { Cipher = Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext)), KeyId = "v1" };

        public string Open(SealedSecret secret)
            => Encoding.UTF8.GetString(Convert.FromBase64String(secret.Cipher));
    }

    private sealed class FakeMcpHttpHandler(string[] remoteTools) : HttpMessageHandler
    {
        public sealed record RecordedRequest(Uri Uri, string? AuthHeader);
        public List<RecordedRequest> Requests { get; } = [];
        public List<string> ToolCalls { get; } = [];
        public Func<string, string, (bool IsError, string Text)>? OnToolCall { get; set; }

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
            var (isError, text) = OnToolCall is not null
                ? OnToolCall(name, root.GetRawText())
                : (false, $"{{\"ok\":true,\"tool\":\"{name}\"}}");
            return new
            {
                content = new[] { new { type = "text", text } },
                isError,
            };
        }
    }
}
