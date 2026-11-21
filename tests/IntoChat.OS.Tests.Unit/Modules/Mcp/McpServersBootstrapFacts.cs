using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Mcp.Tests.Unit;

public sealed class McpServersBootstrapFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void MicrosoftLearnIsPreconfigured()
    {
        var options = new McpModuleOptions();

        Assert.True(options.Servers.TryGetValue("microsoft-learn", out var learn));
        Assert.Equal("https://learn.microsoft.com/api/mcp", learn.Url);
        Assert.Equal("Microsoft Learn", learn.Title);
        Assert.True(learn.Enabled);
        options.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-leading-dash")]
    [InlineData("UpperCase")]
    [InlineData("has_underscore")]
    [InlineData("slash/name")]
    [InlineData("a23456789012345678901234567890123")] // 33 chars
    public void ABadNameIsRefusedByValidate(string badName)
    {
        var options = new McpModuleOptions
        {
            Servers = new(StringComparer.OrdinalIgnoreCase)
            {
                [badName] = new() { Url = "https://example.org/mcp", Title = "Bad" },
            },
        };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.org/mcp")]
    [InlineData("http://example.org/mcp")]
    [InlineData("https://user:pass@example.org/mcp")]
    public void ABadUrlIsRefusedByValidate(string badUrl)
    {
        var options = new McpModuleOptions
        {
            Servers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["docs"] = new() { Url = badUrl, Title = "Docs" },
            },
        };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void LoopbackHttpAndHttpsUrlsPassValidate()
    {
        var options = new McpModuleOptions
        {
            Servers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["local-ip"] = new() { Url = "http://127.0.0.1:8080/mcp" },
                ["local-host"] = new() { Url = "http://localhost:5050/mcp" },
                ["remote"] = new() { Url = "https://learn.microsoft.com/api/mcp", Title = "Learn" },
            },
        };

        options.Validate();
    }

    [Fact]
    public async Task ConfiguredServersConnectAtStart()
    {
        await using var endpoint = await LoopbackMcpEndpoint.StartAsync(Ct);
        await using var os = TestOS.Local().Register(typeof(McpModule).Assembly);
        using var lifetime = new ManualLifetime();

        var services = new ServiceCollection()
            .AddSingleton(IntoChat.OS.Tests.TestEdge.Authority)
            .BuildServiceProvider();
        await using var _ = services;

        var options = Options.Create(new McpModuleOptions
        {
            Servers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["learn"] = new() { Url = endpoint.Url, Title = "Learn", Enabled = true },
            },
        });

        using var bootstrap = new McpServersBootstrap(
            services,
            options,
            lifetime,
            new StubBrains(["owner"]),
            resolveServer: os.Get<IMcpServer>,
            retryBackoff: TimeSpan.Zero);

        await bootstrap.StartAsync(Ct);

        // Must not run before ApplicationStarted fires.
        var serverId = new NeuronId(new BrainId("owner"), "mcp.server", "mcp/learn");
        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner"))))
        {
            Assert.False((await os.Get<IMcpServer>(serverId).Read()).Connected);
        }

        lifetime.TriggerStarted();
        await bootstrap.WaitForCompletionAsync(Ct);

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner"))))
        {
            var state = await os.Get<IMcpServer>(serverId).Read();
            Assert.True(state.Connected, $"Expected connected, error: {state.Error}");
            Assert.Equal("Learn", state.Title);
            Assert.Equal(endpoint.Url, state.Url);
            Assert.Contains(state.Tools, static t => t.Name == "docs_search");
        }

        await bootstrap.StopAsync(Ct);
    }

    [Fact]
    public async Task AServerDownAtStartDoesNotThrowAndIsRetried()
    {
        using var lifetime = new ManualLifetime();
        var services = new ServiceCollection()
            .AddSingleton(IntoChat.OS.Tests.TestEdge.Authority)
            .BuildServiceProvider();
        await using var _ = services;

        var flaky = new FakeMcpServer(failuresBeforeSuccess: 2);
        var dead = new FakeMcpServer(failuresBeforeSuccess: int.MaxValue);
        var servers = new Dictionary<string, FakeMcpServer>(StringComparer.Ordinal)
        {
            ["mcp/flaky"] = flaky,
            ["mcp/dead"] = dead,
        };

        var options = Options.Create(new McpModuleOptions
        {
            Servers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["flaky"] = new() { Url = "https://flaky.example.org/mcp", Title = "Flaky", Enabled = true },
                ["dead"] = new() { Url = "https://dead.example.org/mcp", Title = "Dead", Enabled = true },
            },
        });

        using var bootstrap = new McpServersBootstrap(
            services,
            options,
            lifetime,
            new StubBrains(["owner"]),
            resolveServer: id => servers[id.Key],
            retryBackoff: TimeSpan.Zero);

        await bootstrap.StartAsync(Ct);
        lifetime.TriggerStarted();
        await bootstrap.WaitForCompletionAsync(Ct);

        Assert.Equal(3, flaky.ConnectAttempts);
        Assert.True((await flaky.Read()).Connected);
        Assert.Equal(5, dead.ConnectAttempts);
        Assert.False((await dead.Read()).Connected);

        await bootstrap.StopAsync(Ct);
    }

    [Fact]
    public async Task ADisabledServerIsDisconnectedNotDeleted()
    {
        await using var os = TestOS.Local().Register(typeof(McpModule).Assembly);
        using var lifetime = new ManualLifetime();

        var serverId = new NeuronId(new BrainId("owner"), "mcp.server", "mcp/learn");
        var tool = new ToolDescriptor("docs_search", "Docs Search", "Search docs", "{\"type\":\"object\"}", true);
        os.SeedStoredState(serverId, JsonSerializer.Serialize(
            new McpServerStore
            {
                State = new McpServerState("Microsoft Learn", "https://learn.microsoft.com/api/mcp", true, [tool], null, DateTimeOffset.UtcNow),
            },
            RuntimeProtocol.OptionsFor(typeof(McpServerStore))));

        var services = new ServiceCollection()
            .AddSingleton(IntoChat.OS.Tests.TestEdge.Authority)
            .BuildServiceProvider();
        await using var _ = services;

        var options = Options.Create(new McpModuleOptions
        {
            Servers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["learn"] = new() { Url = "https://learn.microsoft.com/api/mcp", Title = "Microsoft Learn", Enabled = false },
            },
        });

        using var bootstrap = new McpServersBootstrap(
            services,
            options,
            lifetime,
            new StubBrains(["owner"]),
            resolveServer: os.Get<IMcpServer>,
            retryBackoff: TimeSpan.Zero);

        await bootstrap.StartAsync(Ct);
        lifetime.TriggerStarted();
        await bootstrap.WaitForCompletionAsync(Ct);

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner"))))
        {
            var state = await os.Get<IMcpServer>(serverId).Read();
            Assert.False(state.Connected);
            Assert.Equal("Microsoft Learn", state.Title);
            Assert.Equal("https://learn.microsoft.com/api/mcp", state.Url);
            Assert.Single(state.Tools);
        }

        await bootstrap.StopAsync(Ct);
    }

    private sealed class ManualLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void TriggerStarted() => _started.Cancel();
        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }

    private sealed class StubBrains(string[] brainIds) : IBrainObserver
    {
        public Task<string[]> Brains(CancellationToken cancellationToken = default) => Task.FromResult(brainIds);
        public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken) where T : class => AsyncEnumerable.Empty<BrainInstance>();
        public IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<BrainInstance>();
        public Task<BrainType[]> Types(CancellationToken cancellationToken = default) => Task.FromResult<BrainType[]>([]);
        public Task<ObservedWire[]> Wires(CancellationToken cancellationToken = default) => Task.FromResult<ObservedWire[]>([]);
        public IAsyncEnumerable<SignalDelivery> Deliveries(CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<SignalDelivery>();
        public Task<JsonElement?> State(NeuronId id, CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(null);
    }

    private sealed class FakeMcpServer(int failuresBeforeSuccess) : IMcpServer
    {
        private McpServerState _state = new("", "", false, [], null, null);
        public int ConnectAttempts { get; private set; }

        public Task<McpServerState> Connect(McpServerSpec spec, CancellationToken ct = default)
        {
            ConnectAttempts++;
            if (ConnectAttempts <= failuresBeforeSuccess)
            {
                throw new InvalidOperationException($"MCP server {spec.Url} is unreachable.");
            }

            _state = new McpServerState(spec.Title, spec.Url, true, [], null, DateTimeOffset.UtcNow);
            return Task.FromResult(_state);
        }

        public Task Disconnect(CancellationToken ct = default)
        {
            _state = _state with { Connected = false };
            return Task.CompletedTask;
        }

        public Task<McpServerState> Read() => Task.FromResult(_state);
        public Task<ToolSet> Tools(CancellationToken ct = default) => Task.FromResult(new ToolSet(_state.Title, _state.Connected ? _state.Tools : []));
        public Task<ToolResult> Run(string tool, string arguments, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class LoopbackMcpEndpoint : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string Url => _app.Urls.Single() + "/mcp";

        private LoopbackMcpEndpoint(WebApplication app) => _app = app;

        public static async Task<LoopbackMcpEndpoint> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder();
            var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");
            app.MapPost("/mcp", async context =>
            {
                using var request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                var root = request.RootElement;
                if (!root.TryGetProperty("id", out var id))
                {
                    context.Response.StatusCode = StatusCodes.Status202Accepted;
                    return;
                }

                var method = root.GetProperty("method").GetString();
                if (method is not ("initialize" or "tools/list"))
                {
                    context.Response.ContentType = "text/event-stream";
                    await context.Response.WriteAsync(
                        "event: message\ndata: " + JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "method not found" } }) + "\n\n",
                        context.RequestAborted);
                    return;
                }

                object result = method switch
                {
                    "initialize" => new
                    {
                        protocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString(),
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "test-learn", version = "1" },
                    },
                    "tools/list" => new
                    {
                        tools = new[]
                        {
                            new { name = "docs_search", description = "Search docs", inputSchema = new { type = "object" } },
                        },
                    },
                    _ => throw new InvalidOperationException(method),
                };

                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync(
                    "event: message\ndata: " + JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }) + "\n\n",
                    context.RequestAborted);
            });
            app.MapDelete("/mcp", () => Results.Ok());
            await app.StartAsync(ct);
            return new LoopbackMcpEndpoint(app);
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}
