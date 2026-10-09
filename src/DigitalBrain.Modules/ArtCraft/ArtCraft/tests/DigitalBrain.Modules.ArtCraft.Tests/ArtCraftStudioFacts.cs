using System.ComponentModel;
using DigitalBrain.Framework;
using DigitalBrain.Mcp;
using DigitalBrain.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DigitalBrain.ArtCraft.Tests;

public sealed class ArtCraftStudioFacts
{
    private const string Brain = "artcraft-facts";

    [Fact]
    public async Task OpeningConnectsTheAppsMcpServerAndNamesItsPage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeCraft.StartAsync(ct);
        await using var brain = await Start(server.Url, ct);
        Stamp();

        var session = await brain.Get<IArtCraftStudio>(Brain + "/artcraft").Open("photocraft", ct);

        Assert.Equal((Brain + "/mcp/photocraft", "http://localhost:18110/"), (session.McpKey, session.WebUrl));
        Assert.True((await brain.Get<IMcpServer>(session.McpKey).Read()).Connected);
        var entry = Assert.Single(await brain.Get<IToolSources>(Brain + "/tools").Read());
        Assert.Equal("command_list", Assert.Single(entry.Tools).Name);
    }

    [Fact]
    public async Task OpeningTwiceKeepsTheConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeCraft.StartAsync(ct);
        await using var brain = await Start(server.Url, ct);
        Stamp();
        var studio = brain.Get<IArtCraftStudio>(Brain + "/artcraft");
        var first = await studio.Open("photocraft", ct);
        var refreshed = (await brain.Get<IMcpServer>(first.McpKey).Read()).RefreshedAt;

        await studio.Open("photocraft", ct);

        Assert.Equal(refreshed, (await brain.Get<IMcpServer>(first.McpKey).Read()).RefreshedAt);
    }

    [Fact]
    public async Task AnUnknownAppIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start("http://localhost:1/mcp", ct);
        Stamp();

        await Assert.ThrowsAsync<ArgumentException>(() => brain.Get<IArtCraftStudio>(Brain + "/artcraft").Open("paintcraft", ct));
    }

    private static Task<ModuleBrain> Start(string mcp, CancellationToken ct)
        => ModuleTest.Create()
            .WithModule<ArtCraftModule, ArtCraftModuleOptions>(options =>
            {
                options.WithHostedApps();
                options.PhotoCraftMcp = mcp;
            })
            .WithModule<McpModule>().WithModule<ToolsModule>().StartAsync(ct);

    private static void Stamp() => CallerContextStamper.Stamp(new()
    { PrincipalId = "owner", AccountId = "owner", BrainId = Brain, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });

    [McpServerToolType]
    public sealed class CraftTools
    {
        [McpServerTool(Name = "command_list", ReadOnly = true), Description("List engine commands.")]
        public static string CommandList(string? filter = null) => filter is null ? "[]" : "[]";
    }

    private sealed class FakeCraft(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url => url;

        public static async Task<FakeCraft> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddMcpServer().WithHttpTransport().WithTools<CraftTools>();
            var app = builder.Build();
            app.MapMcp("/mcp");
            await app.StartAsync(ct);
            return new(app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/mcp");
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}
