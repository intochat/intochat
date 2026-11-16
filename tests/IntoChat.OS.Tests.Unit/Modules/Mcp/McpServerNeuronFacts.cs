using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Tools;

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
        var (os, neuron) = Open();
        await using var _ = os;
        await Assert.ThrowsAsync<ArgumentException>(() => neuron.Connect(new McpServerSpec("Title", url), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectRefusesABlankTitleAndANullSpec()
    {
        var (os, neuron) = Open();
        await using var _ = os;
        await Assert.ThrowsAsync<ArgumentException>(() => neuron.Connect(new McpServerSpec(" ", "https://example.test/mcp"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => neuron.Connect(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANeverConnectedServerReadsEmptyAndOffersNoTools()
    {
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
        var tool = new ToolDescriptor("echo", "Echo", "", "{\"type\":\"object\"}", true);
        var (os, neuron) = Open(new McpServerState("Fake", "https://example.test/mcp", false, [tool], null, null));
        await using var _ = os;

        Assert.Equal("Fake", (await neuron.Read()).Title);
        Assert.Empty((await neuron.Tools(TestContext.Current.CancellationToken)).Tools);
    }

    [Fact]
    public async Task AConnectedServerOffersItsToolsUnderItsTitle()
    {
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
        var connected = new McpServerState("Fake", "https://example.test/mcp", true, [], null, null);
        var (os, neuron) = Open(connected);
        await using var _ = os;
        await Assert.ThrowsAsync<ArgumentException>(() => neuron.Run("echo", arguments, TestContext.Current.CancellationToken));
    }
}
