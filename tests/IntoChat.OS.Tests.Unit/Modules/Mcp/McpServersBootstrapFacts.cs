using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
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
    public void LoopbackHttpAndHttpsUrlsPassValidateAndBlankAllowedToolsThrow()
    {
        var options = new McpModuleOptions
        {
            Servers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["local-ip"] = new() { Url = "http://127.0.0.1:8080/mcp" },
                ["local-host"] = new() { Url = "http://localhost:5050/mcp" },
                ["remote"] = new() { Url = "https://learn.microsoft.com/api/mcp", Title = "Learn", AllowedTools = ["docs_search"] },
            },
        };

        options.Validate();

        var badAllowed = new McpModuleOptions
        {
            Servers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["docs"] = new() { Url = "https://learn.microsoft.com/api/mcp", AllowedTools = ["ok", " "] },
            },
        };
        Assert.Throws<ArgumentException>(badAllowed.Validate);
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
            resolveToolConnection: os.Get<IMcpToolConnection>,
            resolveConnection: os.Get<IConnection>,
            retryBackoff: TimeSpan.Zero);

        await bootstrap.StartAsync(Ct);

        // Must not run before ApplicationStarted fires.
        var serverId = IMcpToolConnection.NeuronIdOf(new BrainId("owner"), "learn");
        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner"))))
        {
            Assert.False((await os.Get<IMcpToolConnection>(serverId).ReadState(Ct)).Connected);
        }

        lifetime.TriggerStarted();
        await bootstrap.WaitForCompletionAsync(Ct);

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner"))))
        {
            var state = await os.Get<IMcpToolConnection>(serverId).ReadState(Ct);
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

        var flaky = new FakeMcpToolConnection(failuresBeforeSuccess: 2);
        var dead = new FakeMcpToolConnection(failuresBeforeSuccess: int.MaxValue);
        var servers = new Dictionary<string, FakeMcpToolConnection>(StringComparer.Ordinal)
        {
            [IMcpToolConnection.KeyOf("flaky")] = flaky,
            [IMcpToolConnection.KeyOf("dead")] = dead,
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
            resolveToolConnection: id => servers[id.Key],
            resolveConnection: _ => throw new NotSupportedException(),
            retryBackoff: TimeSpan.Zero);

        await bootstrap.StartAsync(Ct);
        lifetime.TriggerStarted();
        await bootstrap.WaitForCompletionAsync(Ct);

        Assert.Equal(3, flaky.ConnectAttempts);
        Assert.True((await flaky.ReadState(Ct)).Connected);
        Assert.Equal(5, dead.ConnectAttempts);
        Assert.False((await dead.ReadState(Ct)).Connected);

        await bootstrap.StopAsync(Ct);
    }

    [Fact]
    public async Task ADisabledServerIsDisconnectedNotDeleted()
    {
        await using var os = TestOS.Local().Register(typeof(McpModule).Assembly);
        using var lifetime = new ManualLifetime();

        var serverId = IMcpToolConnection.NeuronIdOf(new BrainId("owner"), "learn");
        var tool = new ToolDescriptor("docs_search", "Docs Search", "Search docs", "{\"type\":\"object\"}", true);
        os.SeedStoredState(serverId, JsonSerializer.Serialize(
            new McpToolConnectionState
            {
                Title = "Microsoft Learn",
                Url = "https://learn.microsoft.com/api/mcp",
                Connected = true,
                Tools = [tool],
                ProbedAt = DateTimeOffset.UtcNow,
            },
            RuntimeProtocol.OptionsFor(typeof(McpToolConnectionState))));

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
            resolveToolConnection: os.Get<IMcpToolConnection>,
            resolveConnection: os.Get<IConnection>,
            retryBackoff: TimeSpan.Zero);

        await bootstrap.StartAsync(Ct);
        lifetime.TriggerStarted();
        await bootstrap.WaitForCompletionAsync(Ct);

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner"))))
        {
            var state = await os.Get<IMcpToolConnection>(serverId).ReadState(Ct);
            Assert.False(state.Connected);
            Assert.Equal("Microsoft Learn", state.Title);
            Assert.Equal("https://learn.microsoft.com/api/mcp", state.Url);
            Assert.Single(state.Tools);
        }

        await bootstrap.StopAsync(Ct);
    }

    [Fact]
    public async Task SeedProviderConnectsOnceOnOwnerBrainAndDoesNotOverwriteExistingConnection()
    {
        await using var endpoint = await LoopbackMcpEndpoint.StartAsync(Ct);
        var sealer = new PrefixSealer();
        var services = new ServiceCollection()
            .AddSingleton(IntoChat.OS.Tests.TestEdge.Authority)
            .AddSingleton<DigitalBrain.Capabilities.Connections.Sealing.ISecretSealer>(sealer)
            .AddSingleton<IReadOnlyList<DigitalBrain.Capabilities.Connections.Connectors.ConnectorDefinition>>([McpModule.Connector])
            .BuildServiceProvider();
        await using var _ = services;

        await using var os = TestOS.Local(o => o.Services = services)
            .Register(typeof(McpModule).Assembly)
            .Register<DigitalBrain.Capabilities.Connections.Catalog.ConnectionCatalogNeuron>();
        using var lifetime = new ManualLifetime();

        var provider = new StubSeedProvider(new McpConnectionSeed(
            "muncly-crm",
            new McpConnectionConfig(endpoint.Url, "Muncly CRM", HeaderName: "Authorization", HeaderPrefix: "Bearer "),
            Token: "tok_seed_123"));

        var options = Options.Create(new McpModuleOptions { Servers = new(StringComparer.OrdinalIgnoreCase) });

        using var bootstrap = new McpServersBootstrap(
            services,
            options,
            lifetime,
            new StubBrains(["owner", "other"]),
            seedProviders: [provider],
            resolveToolConnection: os.Get<IMcpToolConnection>,
            resolveConnection: os.Get<IConnection>,
            retryBackoff: TimeSpan.Zero);

        await bootstrap.StartAsync(Ct);
        lifetime.TriggerStarted();
        await bootstrap.WaitForCompletionAsync(Ct);

        var ownerId = IMcpToolConnection.NeuronIdOf(new BrainId("owner"), "muncly-crm");
        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner"))))
        {
            var state = await os.Get<IMcpToolConnection>(ownerId).ReadState(Ct);
            Assert.True(state.Connected, $"Expected connected, error: {state.Error}");
            Assert.Equal("Muncly CRM", state.Title);
            Assert.Contains("Bearer tok_seed_123", endpoint.AuthHeaders);
            Assert.DoesNotContain("tok_seed_123", JsonSerializer.Serialize(state), StringComparison.Ordinal);
        }

        // Second run with a different seed provider must not overwrite the already-connected owner connection.
        var replacementProvider = new StubSeedProvider(new McpConnectionSeed(
            "muncly-crm",
            new McpConnectionConfig("https://should-not-replace.example.test/mcp", "Replaced"),
            Token: "tok_other"));
        using var lifetime2 = new ManualLifetime();
        using var bootstrap2 = new McpServersBootstrap(
            services,
            options,
            lifetime2,
            new StubBrains(["owner"]),
            seedProviders: [replacementProvider],
            resolveToolConnection: os.Get<IMcpToolConnection>,
            resolveConnection: os.Get<IConnection>,
            retryBackoff: TimeSpan.Zero);

        await bootstrap2.StartAsync(Ct);
        lifetime2.TriggerStarted();
        await bootstrap2.WaitForCompletionAsync(Ct);

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner"))))
        {
            var state = await os.Get<IMcpToolConnection>(ownerId).ReadState(Ct);
            Assert.Equal("Muncly CRM", state.Title);
            Assert.Equal(endpoint.Url, state.Url);
        }
    }

    private sealed class PrefixSealer : DigitalBrain.Capabilities.Connections.Sealing.ISecretSealer
    {
        public DigitalBrain.Capabilities.Secrets.SealedSecret Seal(string name, string plaintext)
            => new() { Cipher = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)), KeyId = "v1" };

        public string Open(DigitalBrain.Capabilities.Secrets.SealedSecret secret)
            => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(secret.Cipher));
    }

    private sealed class StubSeedProvider(McpConnectionSeed? seed) : IMcpConnectionSeedProvider
    {
        public ValueTask<McpConnectionSeed?> ResolveAsync(string ownerBrainId, CancellationToken ct) => ValueTask.FromResult(seed);
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

    private sealed class FakeMcpToolConnection(int failuresBeforeSuccess) : IMcpToolConnection
    {
        private McpServerState _state = new("", "", false, [], null, null);
        public int ConnectAttempts { get; private set; }

        public Task<McpServerState> ConnectServer(McpConnectionConfig config, CancellationToken ct = default)
        {
            ConnectAttempts++;
            if (ConnectAttempts <= failuresBeforeSuccess)
            {
                throw new InvalidOperationException($"MCP server {config.Url} is unreachable.");
            }

            _state = new McpServerState(config.Title ?? "", config.Url, true, [], null, DateTimeOffset.UtcNow);
            return Task.FromResult(_state);
        }

        public Task<McpServerState> Configure(McpConnectionConfig config, CancellationToken ct = default)
            => ConnectServer(config, ct);

        public Task DisconnectServer(CancellationToken ct = default)
        {
            _state = _state with { Connected = false };
            return Task.CompletedTask;
        }

        public Task<McpServerState> ReadState(CancellationToken ct = default) => Task.FromResult(_state);
        public Task<ToolSet> Tools(CancellationToken ct = default) => Task.FromResult(new ToolSet(_state.Title, _state.Connected ? _state.Tools : []));
        public Task<ToolResult> Run(string tool, string arguments, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> List() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<JsonElement> Run(string tool, JsonElement args, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ToolDescriptor>> Search(string query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<bool> Validate(string tool, JsonElement args, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class LoopbackMcpEndpoint : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string Url => _app.Urls.Single() + "/mcp";
        public System.Collections.Concurrent.ConcurrentBag<string> AuthHeaders { get; } = [];

        private LoopbackMcpEndpoint(WebApplication app) => _app = app;

        public static async Task<LoopbackMcpEndpoint> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder();
            var app = builder.Build();
            var endpoint = new LoopbackMcpEndpoint(app);
            app.Urls.Add("http://127.0.0.1:0");
            app.MapPost("/mcp", async context =>
            {
                if (context.Request.Headers.TryGetValue("Authorization", out var auth))
                {
                    endpoint.AuthHeaders.Add(auth.ToString());
                }

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
            return endpoint;
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}
