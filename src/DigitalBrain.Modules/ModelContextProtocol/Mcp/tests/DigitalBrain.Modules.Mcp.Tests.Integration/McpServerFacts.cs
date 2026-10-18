using System.ComponentModel;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Testing;
using DigitalBrain.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DigitalBrain.Mcp.Tests;

public sealed class McpServerFacts(McpHost host)
{
    private const string Server = "mcp/fake";

    [Fact]
    public async Task ConnectingListsTheServersToolsAndSearchFindsThemLive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServer.StartAsync(ct);
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();

        var state = await brain.Get<IMcpServer>(person.Key(Server)).Connect(new("Fake", server.Url), ct);

        Assert.True(state.Connected);
        Assert.Equal(["echo", "next", "pixel"], state.Tools.Select(tool => tool.Name).Order());
        Assert.Contains("\"text\"", state.Tools.Single(tool => tool.Name == "echo").InputSchema, StringComparison.Ordinal);
        var capabilities = await Capabilities(brain).Read(person.BrainId, ct);
        Assert.Equal(["echo", "next", "pixel"], capabilities.Select(entry => entry.Tool).Order());
        Assert.All(capabilities, entry => Assert.Equal((person.Key(Server), typeof(IMcpServer).FullName), (entry.Key, entry.Contract)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Capabilities(brain).Read("another-brain", ct));
    }

    [Fact]
    public async Task RunCallsTheToolAndReturnsItsText()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServer.StartAsync(ct);
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var mcp = brain.Get<IMcpServer>(person.Key(Server));
        await mcp.Connect(new("Fake", server.Url), ct);

        var result = await mcp.Run("echo", """{"text":"hello"}""", ct);

        Assert.False(result.IsError);
        Assert.Equal("echo: hello", result.Text);
    }

    [Fact]
    public async Task CallsShareOneServerSessionSoItsStateCarriesOver()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServer.StartAsync(ct);
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var mcp = brain.Get<IMcpServer>(person.Key(Server));
        await mcp.Connect(new("Fake", server.Url), ct);

        await mcp.Run("next", "{}", ct);
        var second = await mcp.Run("next", "{}", ct);

        Assert.Equal("2", second.Text);
    }

    [Fact]
    public async Task RunReturnsImageContentAsABlob()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServer.StartAsync(ct);
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var mcp = brain.Get<IMcpServer>(person.Key(Server));
        await mcp.Connect(new("Fake", server.Url), ct);

        var result = await mcp.Run("pixel", "{}", ct);

        var blob = Assert.Single(result.Blobs);
        Assert.Equal("image/png", blob.MediaType);
        Assert.Equal(FakeTools.Png, Convert.FromBase64String(blob.Data));
    }

    [Fact]
    public async Task RunBeforeConnectIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();

        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Get<IMcpServer>(person.Key(Server)).Run("echo", "{}", ct));
    }

    [Fact]
    public async Task AnUnreachableServerIsAStateErrorAndOffersNoTools()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var mcp = brain.Get<IMcpServer>(person.Key(Server));

        await Assert.ThrowsAsync<InvalidOperationException>(() => mcp.Connect(new("Gone", "http://127.0.0.1:1/mcp"), ct));

        var state = await mcp.Read();
        Assert.False(state.Connected);
        Assert.NotNull(state.Error);
        Assert.Empty(await Capabilities(brain).Read(person.BrainId, ct));
    }

    [Fact]
    public async Task ADisconnectedServerOffersNoTools()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServer.StartAsync(ct);
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var mcp = brain.Get<IMcpServer>(person.Key(Server));
        await mcp.Connect(new("Fake", server.Url), ct);

        await mcp.Disconnect(ct);

        Assert.False((await mcp.Read()).Connected);
        Assert.Empty(await Capabilities(brain).Read(person.BrainId, ct));
    }

    private static IRuntimeCapabilitySource Capabilities(ITestBrain brain)
        => brain.Services.GetServices<IRuntimeCapabilitySource>().OfType<ToolCapabilitySource>().Single();

    [McpServerToolType]
    public sealed class FakeTools
    {
        internal static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        [McpServerTool(Name = "echo", ReadOnly = true), Description("Echo the text back.")]
        public static string Echo(string text) => "echo: " + text;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Counters = new();

        [McpServerTool(Name = "next"), Description("Count calls within this MCP session.")]
        public static string Next(McpServer server) => Counters.AddOrUpdate(server.SessionId ?? "", 1, (_, count) => count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        [McpServerTool(Name = "pixel"), Description("Return a tiny PNG.")]
        public static ImageContentBlock Pixel() => ImageContentBlock.FromBytes(Png, "image/png");
    }

    private sealed class FakeServer(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url => url;

        public static async Task<FakeServer> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddMcpServer().WithHttpTransport().WithTools<FakeTools>();
            var app = builder.Build();
            app.MapMcp("/mcp");
            await app.StartAsync(ct);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new(app, address + "/mcp");
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}
