using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain;
using DigitalBrain.Limits;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace IntoChat.OS.Modules.Mcp.Tests.Unit;

public sealed class McpPolicyFacts
{

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task Adopt(TestOS os, string brain, PolicyRule rule) => os.AdoptPolicy(brain, rule, Ct);

    private static async Task<(TestOS Os, McpServerNeuron Neuron, string Brain)> CreateHarnessAsync(Uri endpoint)
    {
        var brain = Guid.NewGuid().ToString("N");
        var policiesId = NeuronId.Parse(IPolicies.Key(brain));
        var os = TestOS.Local().Register(typeof(McpModule).Assembly);
        var id = new NeuronId(new BrainId(brain), "mcp.server", $"mcp/{Guid.NewGuid():N}");
        var context = new FakeNeuronContext(id);
        context.Proxies.Register(policiesId, os.Get<IPolicies>(policiesId));
        var tool = new ToolDescriptor("echo", "Echo", "", "{\"type\":\"object\"}", false);
        await context.MemoryState.Save(
            new McpServerStore
            {
                State = new McpServerState("Fixture", endpoint.AbsoluteUri, true, [tool], null, DateTimeOffset.UtcNow),
            },
            []);

        var neuron = new McpServerNeuron();
        NeuronAttachment.Attach(neuron, context);
        await NeuronAttachment.Activate(neuron, CancellationToken.None);
        return (os, neuron, brain);
    }

    [Fact]
    public async Task ARefusingPolicyStopsTheToolRunBeforeTheApiCall()
    {
        await using var server = FakeMcpEndpoint.Start();
        var (os, neuron, brain) = await CreateHarnessAsync(server.Endpoint);
        await using var _ = os;
        var rule = new PolicyRule(
            "r1",
            "mcp.tool.run",
            PolicyMeasure.Count,
            new PolicyWindow(PolicyWindowKind.None, null),
            0m,
            null,
            "Never run an MCP tool",
            os.Clock.GetUtcNow());
        await Adopt(os, brain, rule);

        await Assert.ThrowsAsync<RuleViolationException>(() => neuron.Run("echo", "{}", Ct));
        Assert.Equal(0, server.ToolCalls);
    }

    [Fact]
    public async Task AnAllowedToolRunIsCountedOnce()
    {
        await using var server = FakeMcpEndpoint.Start();
        var (os, neuron, brain) = await CreateHarnessAsync(server.Endpoint);
        await using var _ = os;
        var rule = new PolicyRule(
            "r1",
            "mcp.tool.run",
            PolicyMeasure.Count,
            new PolicyWindow(PolicyWindowKind.Day, null),
            1m,
            null,
            "Allow at most 1 count of run an MCP tool per day",
            os.Clock.GetUtcNow());
        await Adopt(os, brain, rule);

        var first = await neuron.Run("echo", "{}", Ct);
        Assert.False(first.IsError);
        Assert.Equal("ok", first.Text);
        Assert.Equal(1, server.ToolCalls);

        await Assert.ThrowsAsync<RuleViolationException>(() => neuron.Run("echo", "{}", Ct));
        Assert.Equal(1, server.ToolCalls);
    }

    [Fact]
    public async Task AFailedApiCallReleasesTheReservation()
    {
        await using var server = FakeMcpEndpoint.Start();
        var (os, neuron, brain) = await CreateHarnessAsync(server.Endpoint);
        await using var _ = os;
        var rule = new PolicyRule(
            "r1",
            "mcp.tool.run",
            PolicyMeasure.Count,
            new PolicyWindow(PolicyWindowKind.Day, null),
            1m,
            null,
            "Allow at most 1 count of run an MCP tool per day",
            os.Clock.GetUtcNow());
        await Adopt(os, brain, rule);

        server.FailNextToolCall = true;
        await Assert.ThrowsAnyAsync<McpException>(() => neuron.Run("echo", "{}", Ct));

        var second = await neuron.Run("echo", "{}", Ct);
        Assert.False(second.IsError);
        Assert.Equal("ok", second.Text);

        await Assert.ThrowsAsync<RuleViolationException>(() => neuron.Run("echo", "{}", Ct));
    }

    [Fact]
    public void TheManifestDeclaresTheAction()
    {
        var action = Assert.Single(ModuleManifest.Of(typeof(McpModule)).Actions, a => a.Name == "mcp.tool.run");
        Assert.Equal("run an MCP tool", action.Description);
        Assert.Null(action.AmountUnit);
    }

    private sealed class FakeMcpEndpoint : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _toolCalls;
        private int _failNextToolCall;

        private FakeMcpEndpoint(WebApplication app)
        {
            _app = app;
        }

        public int ToolCalls => Volatile.Read(ref _toolCalls);
        public bool FailNextToolCall { get => Volatile.Read(ref _failNextToolCall) == 1; set => Interlocked.Exchange(ref _failNextToolCall, value ? 1 : 0); }
        public Uri Endpoint { get; private set; } = null!;

        public static FakeMcpEndpoint Start()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var endpoint = new FakeMcpEndpoint(app);
            app.Run(endpoint.HandleAsync);
            app.StartAsync().GetAwaiter().GetResult();
            endpoint.Endpoint = new Uri(app.Urls.Single().TrimEnd('/') + "/mcp/");
            return endpoint;
        }

        private async Task HandleAsync(HttpContext context)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var request = document.RootElement;
            var method = request.GetProperty("method").GetString();
            if (!request.TryGetProperty("id", out var id))
            {
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            string json;
            if (method == "initialize")
            {
                var init = new JsonObject
                {
                    ["protocolVersion"] = request.GetProperty("params").GetProperty("protocolVersion").GetString(),
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "fake-mcp", ["version"] = "1.0" },
                };
                json = Reply(id, "result", init);
            }
            else if (method == "tools/call")
            {
                Interlocked.Increment(ref _toolCalls);
                if (Interlocked.CompareExchange(ref _failNextToolCall, 0, 1) == 1)
                {
                    json = Reply(id, "error", new JsonObject { ["code"] = -32603, ["message"] = "Simulated tool failure" });
                }
                else
                {
                    var result = new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ok" }),
                        ["isError"] = false,
                    };
                    json = Reply(id, "result", result);
                }
            }
            else
            {
                json = Reply(id, "error", new JsonObject { ["code"] = -32601, ["message"] = "Method not found" });
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }

        private static string Reply(JsonElement id, string kind, JsonNode body)
            => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()), [kind] = body }.ToJsonString();

        public async ValueTask DisposeAsync()
        {
            try { await _app.StopAsync(); }
            finally { await _app.DisposeAsync(); }
        }
    }
}
