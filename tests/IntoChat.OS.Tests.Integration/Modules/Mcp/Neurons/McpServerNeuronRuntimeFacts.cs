using IntoChat.OS.Tests;
using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Mcp.Tests.Integration;

// The plain MCP server neuron (06-06d) on both runtimes (R4-05): its state rules and what a failed connect saves and publishes.
// The live server path (tools, search, Run) is proven against a real MCP server in McpServerFacts.
public sealed class McpServerNeuronRuntimeFacts(McpFixture fixture) : IClassFixture<McpFixture>
{
    public static TheoryData<NeuronRuntimeKind> Kinds => McpFixture.Kinds;

    [Theory, MemberData(nameof(Kinds))]
    public async Task ANeverConnectedServerReadsEmptyAndOffersNoTools(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var server = runtime.Get<IMcpToolConnection>(runtime.Id(IMcpToolConnection.TypeName, IMcpToolConnection.KeyOf($"empty-{kind}")));

        Assert.False((await server.ReadState(ct)).Connected);
        Assert.Empty((await server.Tools(ct)).Tools);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task AnUnreachableServerIsSavedDisconnectedWithItsErrorAndTheChangeIsPublished(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id(IMcpToolConnection.TypeName, IMcpToolConnection.KeyOf($"unreachable-{kind}"));
        var server = runtime.Get<IMcpToolConnection>(id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.ConnectServer(new McpConnectionConfig("http://127.0.0.1:9/mcp", "Gone"), ct));

        var state = await server.ReadState(ct);
        Assert.Equal(("Gone", false), (state.Title, state.Connected));
        Assert.False(string.IsNullOrEmpty(state.Error));
        var changed = Assert.Single(await runtime.WaitForSignals<McpServerChanged>(id, 1, TimeSpan.FromSeconds(20), ct));
        Assert.Equal("Gone", changed.State.Title);
    }
}
