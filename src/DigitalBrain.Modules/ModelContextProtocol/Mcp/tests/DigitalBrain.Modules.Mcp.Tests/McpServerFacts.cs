using System.ComponentModel;
using DigitalBrain.Framework;
using DigitalBrain.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DigitalBrain.Mcp.Tests;

public sealed class McpServerFacts
{
    private const string Brain = "mcp-server-facts";
    private const string Key = Brain + "/mcp/fake";

    [Fact]
    public async Task ConnectingListsTheServersToolsAndRegistersThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServer.StartAsync(ct);
        await using var brain = await Start(ct);
        Stamp();

        var state = await brain.Get<IMcpServer>(Key).Connect(new("Fake", server.Url), ct);

        Assert.True(state.Connected);
        Assert.Equal(["echo", "pixel"], state.Tools.Select(tool => tool.Name).Order());
        Assert.Contains("\"text\"", state.Tools.Single(tool => tool.Name == "echo").InputSchema, StringComparison.Ordinal);
        var entry = Assert.Single(await brain.Get<IToolSources>(Brain + "/tools").Read());
        Assert.Equal((Key, typeof(IMcpServer).FullName), (entry.Key, entry.Contract));
    }

    [Fact]
    public async Task RunCallsTheToolAndReturnsItsText()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServer.StartAsync(ct);
        await using var brain = await Start(ct);
        Stamp();
        var mcp = brain.Get<IMcpServer>(Key);
        await mcp.Connect(new("Fake", server.Url), ct);

        var result = await mcp.Run("echo", """{"text":"hello"}""", ct);

        Assert.False(result.IsError);
        Assert.Equal("echo: hello", result.Text);
    }

    [Fact]
    public async Task RunReturnsImageContentAsABlob()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServer.StartAsync(ct);
        await using var brain = await Start(ct);
        Stamp();
        var mcp = brain.Get<IMcpServer>(Key);
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
        await using var brain = await Start(ct);
        Stamp();

        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Get<IMcpServer>(Key).Run("echo", "{}", ct));
    }

    [Fact]
    public async Task AnUnreachableServerIsAStateErrorNotARegistration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        Stamp();
        var mcp = brain.Get<IMcpServer>(Key);

        await Assert.ThrowsAsync<InvalidOperationException>(() => mcp.Connect(new("Gone", "http://127.0.0.1:1/mcp"), ct));

        var state = await mcp.Read();
        Assert.False(state.Connected);
        Assert.NotNull(state.Error);
        Assert.Empty(await brain.Get<IToolSources>(Brain + "/tools").Read());
    }

    [Fact]
    public async Task DisconnectingUnregistersTheSource()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServer.StartAsync(ct);
        await using var brain = await Start(ct);
        Stamp();
        var mcp = brain.Get<IMcpServer>(Key);
        await mcp.Connect(new("Fake", server.Url), ct);

        await mcp.Disconnect(ct);

        Assert.False((await mcp.Read()).Connected);
        Assert.Empty(await brain.Get<IToolSources>(Brain + "/tools").Read());
    }

    private static Task<ModuleBrain> Start(CancellationToken ct)
        => ModuleTest.Create().WithModule<McpModule>().WithModule<ToolsModule>().StartAsync(ct);

    private static void Stamp() => CallerContextStamper.Stamp(new()
    { PrincipalId = "owner", AccountId = "owner", BrainId = Brain, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });

    [McpServerToolType]
    public sealed class FakeTools
    {
        internal static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        [McpServerTool(Name = "echo", ReadOnly = true), Description("Echo the text back.")]
        public static string Echo(string text) => "echo: " + text;

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
