using System.Text.Json;
using DigitalBrain.Capabilities.Tools;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Mcp.Tests.Unit;

public sealed class McpToolConnectionFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeServer : IMcpServer
    {
        public bool Connected { get; set; } = true;
        public List<ToolDescriptor> AvailableTools { get; set; } = [];
        public Func<string, string, ToolResult>? OnRun { get; set; }

        public Task<McpServerState> Connect(McpServerSpec spec, CancellationToken ct = default)
        {
            Connected = true;
            return Task.FromResult(new McpServerState(spec.Title, spec.Url, true, AvailableTools.ToArray(), null, DateTimeOffset.UtcNow));
        }

        public Task Disconnect(CancellationToken ct = default)
        {
            Connected = false;
            return Task.CompletedTask;
        }

        public Task<McpServerState> Read()
            => Task.FromResult(new McpServerState("FakeServer", "http://localhost:8000", Connected, AvailableTools.ToArray(), null, null));

        public Task<ToolSet> Tools(CancellationToken ct = default)
            => Task.FromResult(new ToolSet("FakeServer", AvailableTools.ToArray()));

        public Task<ToolResult> Run(string tool, string arguments, CancellationToken ct = default)
        {
            if (OnRun is not null)
            {
                return Task.FromResult(OnRun(tool, arguments));
            }
            return Task.FromResult(new ToolResult(false, "{\"status\":\"ok\"}", []));
        }
    }

    [Fact]
    public async Task McpToolConnectionListAndSearchReflectState()
    {
        var tool1 = new ToolDescriptor("fetch_data", "Fetch Data", "Fetches remote data", "{}", true);
        var tool2 = new ToolDescriptor("send_email", "Send Email", "Sends an outgoing message", "{}", false);

        var server = new FakeServer { AvailableTools = [tool1, tool2] };
        var conn = await CreateToolConnectionAsync(server, DigitalBrain.NeuronId.Parse("facts/mcp.tool/connection/1"), new McpToolConnectionState
        {
            Title = "FakeServer",
            Url = "http://localhost:8000",
            Connected = true,
            Tools = [tool1, tool2]
        }, Ct);

        var list = await conn.List();
        Assert.Equal(2, list.Count);
        Assert.Contains("fetch_data", list);
        Assert.Contains("send_email", list);

        var searchResult = await conn.Search("email", Ct);
        var match = Assert.Single(searchResult);
        Assert.Equal("send_email", match.Name);

        var searchByDesc = await conn.Search("remote", Ct);
        var matchDesc = Assert.Single(searchByDesc);
        Assert.Equal("fetch_data", matchDesc.Name);
    }

    [Fact]
    public async Task McpToolConnectionValidateChecksExistenceAndArgs()
    {
        var tool1 = new ToolDescriptor("ping", "Ping", "Health check", "{}", true);
        var server = new FakeServer { AvailableTools = [tool1] };
        var conn = await CreateToolConnectionAsync(server, DigitalBrain.NeuronId.Parse("facts/mcp.tool/connection/2"), new McpToolConnectionState
        {
            Title = "FakeServer",
            Connected = true,
            Tools = [tool1]
        }, Ct);

        Assert.True(await conn.Validate("ping", JsonDocument.Parse("{}").RootElement, Ct));
        Assert.False(await conn.Validate("unknown", JsonDocument.Parse("{}").RootElement, Ct));
        Assert.False(await conn.Validate("ping", JsonDocument.Parse("[1, 2]").RootElement, Ct));
    }

    [Fact]
    public async Task McpToolConnectionRunExecutesToolSuccessfully()
    {
        var tool1 = new ToolDescriptor("calc", "Calc", "Adds numbers", "{}", true);
        var server = new FakeServer
        {
            AvailableTools = [tool1],
            OnRun = (t, args) => new ToolResult(false, "{\"sum\": 42}", [])
        };

        var conn = await CreateToolConnectionAsync(server, DigitalBrain.NeuronId.Parse("facts/mcp.tool/connection/3"), new McpToolConnectionState
        {
            Title = "FakeServer",
            Connected = true,
            Tools = [tool1]
        }, Ct);

        var result = await conn.Run("calc", JsonDocument.Parse("{\"a\": 20, \"b\": 22}").RootElement, Ct);
        Assert.Equal(42, result.GetProperty("sum").GetInt32());
    }

    [Fact]
    public async Task McpToolConnectionRunWrapsNonJsonTextAsResult()
    {
        var tool1 = new ToolDescriptor("echo", "Echo", "Echoes text", "{}", true);
        var server = new FakeServer
        {
            AvailableTools = [tool1],
            OnRun = (t, args) => new ToolResult(false, "plain \"text\"", [])
        };

        var conn = await CreateToolConnectionAsync(server, DigitalBrain.NeuronId.Parse("facts/mcp.tool/connection/5"), new McpToolConnectionState
        {
            Title = "FakeServer",
            Connected = true,
            Tools = [tool1]
        }, Ct);

        var result = await conn.Run("echo", JsonDocument.Parse("{}").RootElement, Ct);
        Assert.Equal("plain \"text\"", result.GetProperty("result").GetString());
    }

    [Fact]
    public async Task McpToolConnectionRunThrowsOnNotFoundOrError()
    {
        var tool1 = new ToolDescriptor("failing_tool", "Fail", "Always fails", "{}", true);
        var server = new FakeServer
        {
            AvailableTools = [tool1],
            OnRun = (t, args) => new ToolResult(true, "Internal crash", [])
        };

        var conn = await CreateToolConnectionAsync(server, DigitalBrain.NeuronId.Parse("facts/mcp.tool/connection/4"), new McpToolConnectionState
        {
            Title = "FakeServer",
            Connected = true,
            Tools = [tool1]
        }, Ct);

        var notFoundEx = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            conn.Run("nonexistent", JsonDocument.Parse("{}").RootElement, Ct));
        Assert.True(notFoundEx.IsNotFound);

        var failEx = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            conn.Run("failing_tool", JsonDocument.Parse("{}").RootElement, Ct));
        Assert.False(failEx.IsNotFound);
        Assert.Contains("Internal crash", failEx.Message);
    }

    [Fact]
    public async Task BrainToolsConnectionAggregatesAcrossSources()
    {
        var t1 = new ToolDescriptor("source1_tool", "S1", "First source tool", "{}", true);
        var t2 = new ToolDescriptor("source2_tool", "S2", "Second source tool", "{}", true);

        var server1 = new FakeServer
        {
            AvailableTools = [t1],
            OnRun = (t, _) => new ToolResult(false, "{\"from\": 1}", [])
        };
        var server2 = new FakeServer
        {
            AvailableTools = [t2],
            OnRun = (t, _) => new ToolResult(false, "{\"from\": 2}", [])
        };

        var brainServices = new ServiceCollection()
            .AddSingleton<IToolSource>(server1)
            .AddSingleton<IToolSource>(server2)
            .BuildServiceProvider();
        var brainTools = ActivatorUtilities.CreateInstance<BrainToolsConnection>(brainServices);
        var context = new FakeNeuronContext(DigitalBrain.NeuronId.Parse("facts/brain.tools/main"));
        NeuronAttachment.Attach(brainTools, context);
        await NeuronAttachment.Activate(brainTools, Ct);

        var all = await brainTools.List();
        Assert.Equal(2, all.Count);
        Assert.Contains("source1_tool", all);
        Assert.Contains("source2_tool", all);

        var search1 = await brainTools.Search("First", Ct);
        Assert.Single(search1);
        Assert.Equal("source1_tool", search1[0].Name);

        var res1 = await brainTools.Run("source1_tool", JsonDocument.Parse("{}").RootElement, Ct);
        Assert.Equal(1, res1.GetProperty("from").GetInt32());

        var res2 = await brainTools.Run("source2_tool", JsonDocument.Parse("{}").RootElement, Ct);
        Assert.Equal(2, res2.GetProperty("from").GetInt32());

        var ex = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            brainTools.Run("missing_tool", JsonDocument.Parse("{}").RootElement, Ct));
        Assert.True(ex.IsNotFound);
    }

    private static async Task<McpToolConnection> CreateToolConnectionAsync(
        IMcpServer server,
        DigitalBrain.NeuronId id,
        McpToolConnectionState state,
        CancellationToken ct)
    {
        var services = new ServiceCollection()
            .AddSingleton(server)
            .BuildServiceProvider();
        var conn = ActivatorUtilities.CreateInstance<McpToolConnection>(services);
        var context = new FakeNeuronContext(id);
        await context.MemoryState.Save(state, []);
        NeuronAttachment.Attach(conn, context);
        await NeuronAttachment.Activate(conn, ct);
        return conn;
    }
}
