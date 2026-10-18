using System.ComponentModel;
using IntoChat.OS.Mcp;
using IntoChat.OS.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace IntoChat.OS.ArtCraft.Tests;

public sealed class ArtCraftStudioFacts(ArtCraftHost host)
{
    [Trait("host", "own")]
    [Fact]
    public async Task OpeningConnectsTheAppsMcpServerAndNamesItsPage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeCraft.StartAsync(ct);
        await using var brain = await Start(server.Url, ct);
        using var caller = brain.Owner.AsCaller();

        var session = await brain.Get<IArtCraftStudio>(brain.Owner.Key("artcraft")).Open("photocraft", ct);

        Assert.Equal((brain.Owner.Key("mcp/photocraft"), "http://localhost:18110/"), (session.McpKey, session.WebUrl));
        Assert.True((await brain.Get<IMcpServer>(session.McpKey).Read()).Connected);
        Assert.Equal("command_list", Assert.Single((await brain.Get<IMcpServer>(session.McpKey).Tools(ct)).Tools).Name);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task OpeningTwiceKeepsTheConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeCraft.StartAsync(ct);
        await using var brain = await Start(server.Url, ct);
        using var caller = brain.Owner.AsCaller();
        var studio = brain.Get<IArtCraftStudio>(brain.Owner.Key("artcraft"));
        var first = await studio.Open("photocraft", ct);
        var refreshed = (await brain.Get<IMcpServer>(first.McpKey).Read()).RefreshedAt;

        await studio.Open("photocraft", ct);

        Assert.Equal(refreshed, (await brain.Get<IMcpServer>(first.McpKey).Read()).RefreshedAt);
    }

    [Fact]
    public async Task AnUnknownAppIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();

        await Assert.ThrowsAsync<ArgumentException>(() => host.Brain.Get<IArtCraftStudio>(person.Key("artcraft")).Open("paintcraft", ct));
    }

    private static Task<ITestBrain> Start(string mcp, CancellationToken ct)
        => TestBrain.Create()
            .WithModule<ArtCraftModule, ArtCraftModuleOptions>(options =>
            {
                options.WithHostedApps();
                options.PhotoCraftMcp = mcp;
            })
            .WithModule<McpModule>().WithModule<ToolsModule>().StartAsync(ct);

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
