using IntoChat.OS.Mcp;
using IntoChat.OS.Tools;

[assembly: AssemblyFixture(typeof(IntoChat.OS.ArtCraft.Tests.ArtCraftHost))]

namespace IntoChat.OS.ArtCraft.Tests;

// The shared host composes ArtCraft with hosted apps pointed at an unreachable PhotoCraft MCP endpoint. Facts that
// script a fake PhotoCraft server keep their own TestBrain ([Trait("host", "own")]); the endpoint is host-global.
public sealed class ArtCraftHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain)
        => brain
            .WithModule<ArtCraftModule, ArtCraftModuleOptions>(options =>
            {
                options.WithHostedApps();
                options.PhotoCraftMcp = "http://localhost:1/mcp";
            })
            .WithModule<McpModule>().WithModule<ToolsModule>();
}
