using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Tools.Tests;

public sealed class ToolSourcesFacts
{
    private const string Brain = "tool-sources-facts";

    [Fact]
    public async Task ARegisteredSourceIsReadBackWithItsTools()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ToolsModule>().StartAsync(ct);
        Stamp();
        var sources = brain.Get<IToolSources>(Brain + "/tools");

        await sources.Register(Entry(Brain + "/mcp/photocraft", Tool("command_run", "Run an engine command.")));

        var entry = Assert.Single(await sources.Read());
        Assert.Equal(Brain + "/mcp/photocraft", entry.Key);
        Assert.Equal("command_run", Assert.Single(entry.Tools).Name);
    }

    [Fact]
    public async Task ASourceOutsideTheBrainIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ToolsModule>().StartAsync(ct);
        Stamp();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(()
            => brain.Get<IToolSources>(Brain + "/tools").Register(Entry("other/mcp/x", Tool("echo", "Echo."))));
    }

    [Fact]
    public async Task RegisteringAndLeavingPublishToolsChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ToolsModule>().StartAsync(ct);
        Stamp();
        var sources = brain.Get<IToolSources>(Brain + "/tools");
        await using var changes = await brain.SubscribeAsync<ToolsChanged>(sources, ct);

        await sources.Register(Entry(Brain + "/mcp/a", Tool("one", "One."), Tool("two", "Two.")));
        await sources.Unregister(Brain + "/mcp/a");

        var seen = new List<ToolsChanged>();
        await foreach (var change in changes.ReadAllAsync(ct))
        {
            seen.Add(change);
            if (seen.Count == 2) { break; }
        }
        Assert.Equal([(Brain + "/mcp/a", 2), (Brain + "/mcp/a", 0)], seen.Select(change => (change.Key, change.Count)));
        Assert.Empty(await sources.Read());
    }

    [Fact]
    public async Task TheCapabilitySourceTurnsToolsIntoKeyedEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ToolsModule>().StartAsync(ct);
        Stamp();
        await brain.Get<IToolSources>(Brain + "/tools").Register(Entry(Brain + "/mcp/photocraft", Tool("render_preview", "Render the document as PNG.")));
        var capabilities = brain.SiloServices.GetServices<IRuntimeCapabilitySource>().Single(source => source is ToolCapabilitySource);

        var entry = Assert.Single(await capabilities.Read(Brain, ct));

        Assert.Equal($"tool:{Brain}/mcp/photocraft/render_preview", entry.Id);
        Assert.Equal(Brain + "/mcp/photocraft", entry.Key);
        Assert.Equal("render_preview", entry.Tool);
        Assert.Equal("Test.IFake", entry.Contract);
        Assert.Contains("Run(\"render_preview\"", Assert.Single(entry.Methods), StringComparison.Ordinal);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => capabilities.Read("another-brain", ct));
    }

    [Fact]
    public async Task ToolListsAndDescriptionsAreClipped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ToolsModule>().StartAsync(ct);
        Stamp();
        var sources = brain.Get<IToolSources>(Brain + "/tools");
        var many = Enumerable.Range(0, 250).Select(index => Tool("t" + index, new string('d', 1500))).ToArray();

        await sources.Register(Entry(Brain + "/mcp/big", many));

        var tools = Assert.Single(await sources.Read()).Tools;
        Assert.Equal(ToolSourcesNeuron.MaxTools, tools.Length);
        Assert.All(tools, tool => Assert.Equal(ToolSourcesNeuron.MaxDescription, tool.Description.Length));
    }

    private static ToolSourceEntry Entry(string key, params ToolDescriptor[] tools) => new(key, "Test.IFake", "Test", tools);

    private static ToolDescriptor Tool(string name, string description) => new(name, name, description, "{\"type\":\"object\"}", false);

    private static void Stamp() => CallerContextStamper.Stamp(new()
    { PrincipalId = "owner", AccountId = "owner", BrainId = Brain, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
}
